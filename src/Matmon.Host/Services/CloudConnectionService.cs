using System.Net;
using System.Net.Http.Json;
using Matmon.Core;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// Primary-only outbound link to Matmon.Cloud: registers this instance once (storing the returned
/// token), then sends periodic heartbeats + aggregate metadata (version, host, sensor count, active
/// alerts). Enabled only when <c>Matmon__CloudUrl</c> is set; fully offline otherwise. Failures are
/// logged and retried on the next tick — the cloud link must never take the monitor down.
/// </summary>
public sealed class CloudConnectionService : BackgroundService
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly ILogger<CloudConnectionService> _logger;

    public CloudConnectionService(
        IMonitoringWorkspaceStore workspaceStore,
        MatmonRuntimeOptions runtimeOptions,
        ILogger<CloudConnectionService> logger)
    {
        _workspaceStore = workspaceStore;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtimeOptions.Mode != AppMode.Primary || string.IsNullOrWhiteSpace(_runtimeOptions.CloudUrl))
        {
            return;
        }

        var baseUrl = _runtimeOptions.CloudUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl + "/", UriKind.Absolute, out var baseUri))
        {
            _logger.LogWarning("Matmon__CloudUrl '{Url}' is not a valid absolute URL — cloud link disabled", _runtimeOptions.CloudUrl);
            return;
        }

        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(15) };
        var interval = TimeSpan.FromSeconds(Math.Max(15, _runtimeOptions.HeartbeatIntervalSeconds));

        // Let the app finish starting before the first sync.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Matmon.Cloud link enabled -> {Url}", baseUrl);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await EnsureRegisteredAsync(client, baseUrl, stoppingToken);
                await SendHeartbeatAsync(client, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Matmon.Cloud sync failed (will retry)");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task EnsureRegisteredAsync(HttpClient client, string baseUrl, CancellationToken cancellationToken)
    {
        var state = _workspaceStore.GetCloudConnection();
        if (state.IsRegistered && string.Equals(state.RegisteredUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var response = await client.PostAsJsonAsync("api/instances/register", new CloudRegisterRequest(ResolveInstanceName()), cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<CloudRegisterResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Empty register response from Matmon.Cloud.");

        _workspaceStore.UpdateCloudConnection(new CloudConnectionState
        {
            InstanceId = payload.InstanceId,
            Token = payload.Token,
            PublicToken = payload.PublicToken,
            RegisteredUrl = baseUrl
        });
        _logger.LogInformation("Registered with Matmon.Cloud as instance {InstanceId}", payload.InstanceId);
    }

    private async Task SendHeartbeatAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var state = _workspaceStore.GetCloudConnection();
        if (!state.IsRegistered)
        {
            return;
        }

        var sensorCount = _workspaceStore.GetAllElements().OfType<SensorElement>().Count();
        var (openAlerts, _, _, _) = _workspaceStore.GetActiveAlertCounts();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/instances/{state.InstanceId}/heartbeat")
        {
            Content = JsonContent.Create(new CloudHeartbeatRequest(
                MatmonVersion.Current,
                Environment.MachineName,
                null,
                sensorCount,
                openAlerts))
        };
        request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", state.Token);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Token no longer accepted (cloud reset / instance removed) — clear so we re-register.
            _logger.LogWarning("Matmon.Cloud rejected the instance token; clearing to re-register");
            _workspaceStore.UpdateCloudConnection(new CloudConnectionState());
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private string ResolveInstanceName()
    {
        if (!string.IsNullOrWhiteSpace(_runtimeOptions.CloudInstanceName))
        {
            return _runtimeOptions.CloudInstanceName.Trim();
        }

        var root = _workspaceStore.GetAllElements().OfType<ProbeElement>().FirstOrDefault(probe => probe.ParentId is null);
        return string.IsNullOrWhiteSpace(root?.Name) ? Environment.MachineName : root!.Name;
    }

    private sealed record CloudRegisterRequest(string Name);
    private sealed record CloudRegisterResponse(Guid InstanceId, string Token, string PublicToken);
    private sealed record CloudHeartbeatRequest(string? Version, string? Host, string? OperatingSystem, int? SensorCount, int? ActiveAlerts);
}
