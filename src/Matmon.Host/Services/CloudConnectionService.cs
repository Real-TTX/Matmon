using System.Net;
using System.Net.Http.Json;
using Matmon.Core;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// Primary-only outbound link to Matmon.Cloud: sends periodic heartbeats + aggregate metadata (version,
/// host, sensor count, active alerts) for the instance provisioned in the Matmon.Cloud UI. Enabled only
/// when <c>Matmon__CloudUrl</c> + <c>Matmon__CloudInstanceId</c> + <c>Matmon__CloudInstanceToken</c> are
/// set; fully offline otherwise. Failures are recorded + retried — the cloud link never takes the monitor
/// down. The last outcome is stored in <see cref="CloudConnectionState"/> for the in-app status view.
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

        if (!Guid.TryParse(_runtimeOptions.CloudInstanceId, out var instanceId) || string.IsNullOrWhiteSpace(_runtimeOptions.CloudInstanceToken))
        {
            _workspaceStore.UpdateCloudConnection(new CloudConnectionState
            {
                CloudUrl = baseUrl,
                LastStatus = "not configured (set Matmon__CloudInstanceId + Matmon__CloudInstanceToken from the Matmon.Cloud UI)"
            });
            _logger.LogWarning("Matmon.Cloud URL set but instance id/token missing — create the instance in the cloud UI and set Matmon__CloudInstanceId + Matmon__CloudInstanceToken");
            return;
        }

        var token = _runtimeOptions.CloudInstanceToken!.Trim();

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(15) };
        var interval = TimeSpan.FromSeconds(Math.Max(15, _runtimeOptions.HeartbeatIntervalSeconds));
        _logger.LogInformation("Matmon.Cloud link enabled -> {Url} (instance {InstanceId})", baseUrl, instanceId);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await SendHeartbeatAsync(client, baseUrl, instanceId, token, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordStatus(baseUrl, instanceId, $"failed: {ex.Message}", heartbeatOk: false);
                _logger.LogWarning(ex, "Matmon.Cloud heartbeat failed (will retry)");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SendHeartbeatAsync(HttpClient client, string baseUrl, Guid instanceId, string token, CancellationToken cancellationToken)
    {
        var sensorCount = _workspaceStore.GetAllElements().OfType<SensorElement>().Count();
        var (openAlerts, _, _, _) = _workspaceStore.GetActiveAlertCounts();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/instances/{instanceId}/heartbeat")
        {
            Content = JsonContent.Create(new CloudHeartbeatRequest(
                MatmonVersion.Current,
                Environment.MachineName,
                null,
                sensorCount,
                openAlerts))
        };
        request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            RecordStatus(baseUrl, instanceId, "unauthorized (check Matmon__CloudInstanceToken)", heartbeatOk: false);
            _logger.LogWarning("Matmon.Cloud rejected the instance token");
            return;
        }

        response.EnsureSuccessStatusCode();
        RecordStatus(baseUrl, instanceId, "ok", heartbeatOk: true);
    }

    private void RecordStatus(string baseUrl, Guid instanceId, string status, bool heartbeatOk)
    {
        _workspaceStore.UpdateCloudConnection(new CloudConnectionState
        {
            InstanceId = instanceId,
            CloudUrl = baseUrl,
            LastStatus = status,
            LastHeartbeatUtc = heartbeatOk ? DateTimeOffset.UtcNow : _workspaceStore.GetCloudConnection().LastHeartbeatUtc
        });
    }

    private sealed record CloudHeartbeatRequest(string? Version, string? Host, string? OperatingSystem, int? SensorCount, int? ActiveAlerts);
}
