using System.Net.Http.Json;
using Matmon.Core;

namespace Matmon.Host.Services;

public sealed class SlaveHeartbeatService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SlaveHeartbeatService> _logger;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly SlaveProbeRuntimeState _runtimeState;

    public SlaveHeartbeatService(
        IHttpClientFactory httpClientFactory,
        ILogger<SlaveHeartbeatService> logger,
        MatmonRuntimeOptions runtimeOptions,
        SlaveProbeRuntimeState runtimeState)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _runtimeOptions = runtimeOptions;
        _runtimeState = runtimeState;
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
                await Task.Delay(interval, stoppingToken);
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
            system.Networks);

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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runtimeState.RecordHeartbeat(success: false, ex.Message);
            _logger.LogWarning(ex, "Heartbeat from {ProbeId} failed", probeId);
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
