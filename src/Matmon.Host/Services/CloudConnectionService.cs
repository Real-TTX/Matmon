using System.Net;
using System.Net.Http.Json;
using Matmon.Core;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// Primary-only outbound link to Matmon.Cloud: sends periodic heartbeats + aggregate metadata (version,
/// host, sensor count, active alerts). The link is <b>managed in the UI</b> (System → Cloud) and persisted
/// in the workspace; the environment variables (<c>Matmon__CloudUrl</c>/<c>CloudInstanceId</c>/
/// <c>CloudInstanceToken</c>) are only a first-run bootstrap until the user connects/disconnects in the UI.
/// The loop re-reads settings every few seconds, so connect/disconnect take effect without a restart.
/// Failures are recorded + retried — the cloud link never takes the monitor down.
/// </summary>
public sealed class CloudConnectionService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly ILogger<CloudConnectionService> _logger;

    private string? _lastStatus;

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
        if (_runtimeOptions.Mode != AppMode.Primary)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        HttpClient? client = null;
        string? clientBaseUrl = null;
        var heartbeatInterval = TimeSpan.FromSeconds(Math.Max(15, _runtimeOptions.CloudHeartbeatIntervalSeconds));
        var lastHeartbeat = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                var link = ResolveLink();

                if (!link.Enabled)
                {
                    if (client is not null)
                    {
                        client.Dispose();
                        client = null;
                        clientBaseUrl = null;
                        lastHeartbeat = DateTimeOffset.MinValue;
                        _logger.LogInformation("Matmon.Cloud link disabled");
                    }

                    if (link.Status is not null)
                    {
                        RecordStatus(link.BaseUrl, link.InstanceId, link.Status, heartbeatOk: false, force: false);
                    }

                    continue;
                }

                if (client is null || clientBaseUrl != link.BaseUrl)
                {
                    client?.Dispose();
                    client = new HttpClient { BaseAddress = link.BaseUri, Timeout = TimeSpan.FromSeconds(15) };
                    clientBaseUrl = link.BaseUrl;
                    lastHeartbeat = DateTimeOffset.MinValue;
                    _logger.LogInformation("Matmon.Cloud link enabled -> {Url} (instance {InstanceId})", link.BaseUrl, link.InstanceId);
                }

                if (DateTimeOffset.UtcNow - lastHeartbeat >= heartbeatInterval)
                {
                    lastHeartbeat = DateTimeOffset.UtcNow;
                    try
                    {
                        await SendHeartbeatAsync(client, link.BaseUrl!, link.InstanceId!.Value, link.Token!, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        RecordStatus(link.BaseUrl, link.InstanceId, $"failed: {ex.Message}", heartbeatOk: false, force: true);
                        _logger.LogWarning(ex, "Matmon.Cloud heartbeat failed (will retry)");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Matmon.Cloud tick failed (will retry)");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        client?.Dispose();
    }

    /// <summary>Resolves the effective link: UI settings once the user has taken over, else env bootstrap.</summary>
    private ResolvedLink ResolveLink()
    {
        var settings = _workspaceStore.GetCloudConnectionSettings();

        string? url;
        string? instanceIdRaw;
        string? token;
        bool enabled;

        if (settings.Configured)
        {
            enabled = settings.Enabled;
            url = settings.Url;
            instanceIdRaw = settings.InstanceId;
            token = enabled ? _workspaceStore.GetCloudConnectionToken() : null;
        }
        else
        {
            url = _runtimeOptions.CloudUrl;
            instanceIdRaw = _runtimeOptions.CloudInstanceId;
            token = _runtimeOptions.CloudInstanceToken;
            enabled = !string.IsNullOrWhiteSpace(url);
        }

        if (!enabled || string.IsNullOrWhiteSpace(url))
        {
            // Cleanly off: DisconnectCloud already recorded "disconnected"; don't clobber it every tick.
            return ResolvedLink.Off(null, null);
        }

        var baseUrl = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl + "/", UriKind.Absolute, out var baseUri))
        {
            return ResolvedLink.Off(baseUrl, "invalid cloud url");
        }

        if (!Guid.TryParse(instanceIdRaw, out var instanceId) || string.IsNullOrWhiteSpace(token))
        {
            return ResolvedLink.Off(baseUrl, "not configured (missing instance id/token)");
        }

        return new ResolvedLink(true, baseUrl, baseUri, instanceId, token.Trim(), null);
    }

    private async Task SendHeartbeatAsync(HttpClient client, string baseUrl, Guid instanceId, string token, CancellationToken cancellationToken)
    {
        var allElements = _workspaceStore.GetAllElements();
        var sensorCount = allElements.OfType<SensorElement>().Count();
        var probeCount = allElements.OfType<ProbeElement>().Count();
        var mapCount = _workspaceStore.GetMaps().Count;
        var (openAlerts, ackAlerts, errorAlerts, warningAlerts) = _workspaceStore.GetActiveAlertCounts();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/instances/{instanceId}/heartbeat")
        {
            Content = JsonContent.Create(new CloudHeartbeatRequest(
                MatmonVersion.Current,
                Environment.MachineName,
                null,
                sensorCount,
                openAlerts,
                errorAlerts,
                warningAlerts,
                ackAlerts,
                probeCount,
                mapCount))
        };
        request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            RecordStatus(baseUrl, instanceId, "unauthorized (check the instance token)", heartbeatOk: false, force: true);
            _logger.LogWarning("Matmon.Cloud rejected the instance token");
            return;
        }

        response.EnsureSuccessStatusCode();
        RecordStatus(baseUrl, instanceId, "ok", heartbeatOk: true, force: true);
        await FetchLicenseAsync(client, instanceId, token, cancellationToken);
    }

    /// <summary>Pulls the signed license token from the cloud and caches it for offline validation.</summary>
    private async Task FetchLicenseAsync(HttpClient client, Guid instanceId, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/instances/{instanceId}/license");
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var payload = await response.Content.ReadFromJsonAsync<LicenseResponse>(cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload?.Token))
            {
                _workspaceStore.SetLicenseToken(payload.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Matmon.Cloud license fetch failed (will retry next heartbeat)");
        }
    }

    private sealed record LicenseResponse(string? Token);

    /// <summary>Persists the last outcome. When <paramref name="force"/> is false, skips redundant writes.</summary>
    private void RecordStatus(string? baseUrl, Guid? instanceId, string status, bool heartbeatOk, bool force)
    {
        if (!force && string.Equals(_lastStatus, status, StringComparison.Ordinal))
        {
            return;
        }

        _lastStatus = status;
        _workspaceStore.UpdateCloudConnection(new CloudConnectionState
        {
            InstanceId = instanceId,
            CloudUrl = baseUrl,
            LastStatus = status,
            LastHeartbeatUtc = heartbeatOk ? DateTimeOffset.UtcNow : _workspaceStore.GetCloudConnection().LastHeartbeatUtc
        });
    }

    private sealed record ResolvedLink(bool Enabled, string? BaseUrl, Uri? BaseUri, Guid? InstanceId, string? Token, string? Status)
    {
        public static ResolvedLink Off(string? baseUrl, string? status) => new(false, baseUrl, null, null, null, status);
    }

    private sealed record CloudHeartbeatRequest(
        string? Version, string? Host, string? OperatingSystem, int? SensorCount, int? ActiveAlerts,
        int? ErrorCount, int? WarningCount, int? AckCount, int? ProbeCount, int? MapCount);
}
