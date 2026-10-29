using System.Net.Http.Json;
using Matmon.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Matmon.Probe;

public sealed class SlaveHeartbeatService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SlaveHeartbeatService> _logger;
    private readonly ProbeRuntimeOptions _runtimeOptions;
    private readonly SlaveProbeRuntimeState _runtimeState;
    private readonly IProbeSelfUpdate? _selfUpdate;

    public SlaveHeartbeatService(
        IHttpClientFactory httpClientFactory,
        ILogger<SlaveHeartbeatService> logger,
        ProbeRuntimeOptions runtimeOptions,
        SlaveProbeRuntimeState runtimeState,
        IProbeSelfUpdate? selfUpdate = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _runtimeOptions = runtimeOptions;
        _runtimeState = runtimeState;
        _selfUpdate = selfUpdate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtimeOptions.Mode != AppMode.Secondary)
        {
            return;
        }

        var primaryUrl = _runtimeOptions.PrimaryUrl;
        if (!Uri.TryCreate(primaryUrl, UriKind.Absolute, out var primaryUri))
        {
            _logger.LogWarning("Secondary mode is active, but no valid primary URL is configured.");
            await WaitIndefinitelyAsync(stoppingToken);
            return;
        }

        var probeId = string.IsNullOrWhiteSpace(_runtimeOptions.ProbeId)
            ? Environment.MachineName
            : _runtimeOptions.ProbeId;

        var probeName = string.IsNullOrWhiteSpace(_runtimeOptions.ProbeName)
            ? Environment.MachineName
            : _runtimeOptions.ProbeName;

        var probeToken = string.IsNullOrWhiteSpace(_runtimeOptions.ProbeToken)
            ? null
            : _runtimeOptions.ProbeToken;

        var intervalSeconds = Math.Clamp(_runtimeOptions.HeartbeatIntervalSeconds, 5, 300);
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        _logger.LogInformation(
            "Secondary probe {ProbeName} ({ProbeId}) heartbeats every {Interval} to {PrimaryUrl}",
            probeName,
            probeId,
            interval,
            primaryUri);

        var client = _httpClientFactory.CreateClient("SecondaryHeartbeatService");
        client.BaseAddress = primaryUri;

        while (!stoppingToken.IsCancellationRequested)
        {
            await SendHeartbeatAsync(client, probeId, probeName, probeToken, stoppingToken);

            try
            {
                await _runtimeState.WaitForNextHeartbeatAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendHeartbeatAsync(HttpClient client, string probeId, string probeName, string? probeToken, CancellationToken stoppingToken)
    {
        var system = ProbeSystemInfoProvider.Collect();
        var request = new ProbeHeartbeatRequest(
            probeId,
            probeName,
            probeToken,
            "secondary heartbeat",
            MatmonVersion.Current,
            system.OperatingSystem,
            system.Host,
            system.Networks,
            _runtimeState.AgentUpdateStatus,
            Math.Clamp(_runtimeOptions.HeartbeatIntervalSeconds, 5, 300),
            _selfUpdate?.Report());

        try
        {
            using var response = await client.PostAsJsonAsync("/api/probes/heartbeat", request, stoppingToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = $"heartbeat returned {response.StatusCode}";
                _runtimeState.RecordHeartbeat(success: false, message);
                _logger.LogWarning("Heartbeat from {ProbeId} returned {StatusCode}", probeId, response.StatusCode);
                return;
            }

            _runtimeState.RecordHeartbeat(success: true, "heartbeat accepted by primary");
            await HandleAnswerAsync(response, stoppingToken);
        }
        // A real shutdown propagates (it ends the service cleanly). An HttpClient timeout also throws an
        // OperationCanceledException (TaskCanceledException) but with a different token - that must NOT escape
        // this un-wrapped loop call, or one slow beat kills the probe's heartbeat loop and it reads offline in
        // the primary until a restart. Treat a timeout as a failed beat and let the loop retry.
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _runtimeState.RecordHeartbeat(success: false, ex.Message);
            _logger.LogWarning(ex, "Heartbeat from {ProbeId} failed", probeId);
        }
    }

    /// <summary>An update command from the primary rides the heartbeat answer (Probes page > Update). Only a Docker probe has
    /// someone to hand it to; an older primary's answer has no such field, and a body that is not JSON is ignored.</summary>
    private async Task HandleAnswerAsync(HttpResponseMessage response, CancellationToken stoppingToken)
    {
        if (_selfUpdate is null)
        {
            return;
        }

        try
        {
            var answer = await response.Content.ReadFromJsonAsync<ProbeHeartbeatResponse>(stoppingToken);
            if (answer?.InstallUpdate is { } command && !string.IsNullOrWhiteSpace(command.Id))
            {
                _selfUpdate.Handle(command);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the primary's heartbeat answer");
        }
    }

    private static async Task WaitIndefinitelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
