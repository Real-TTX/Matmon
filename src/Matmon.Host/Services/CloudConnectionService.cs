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
/// Failures are recorded + retried - the cloud link never takes the monitor down.
/// </summary>
public sealed class CloudConnectionService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly CloudUpdateState _updateState;
    private readonly ILogger<CloudConnectionService> _logger;

    private string? _lastStatus;

    public CloudConnectionService(
        IMonitoringWorkspaceStore workspaceStore,
        MatmonRuntimeOptions runtimeOptions,
        CloudUpdateState updateState,
        ILogger<CloudConnectionService> logger)
    {
        _workspaceStore = workspaceStore;
        _runtimeOptions = runtimeOptions;
        _updateState = updateState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtimeOptions.Mode != AppMode.Primary)
        {
            return;
        }

        // Dev/preview: seed a dummy managing partner so co-branding is visible without a real cloud link, and
        // skip the cloud loop entirely (so its disabled-branch clear can't wipe the seeded branding). Off in prod.
        if (_runtimeOptions.DemoServicePartner)
        {
            _workspaceStore.SetServicePartnerInfo(DemoServicePartnerSeed.Build());
            _logger.LogWarning("Matmon__DemoServicePartner is on: seeded a DUMMY service partner for co-branding preview (no real cloud link).");
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
                    // Keep cached partner branding cleared while the link is down. DisconnectCloud() nulls it, but a
                    // heartbeat + FetchServicePartnerAsync already in flight when the operator disconnects can re-cache
                    // it a moment later; this idempotent re-clear runs on the next (now-disabled) cycle, after that
                    // late write, and closes the window (also purges any pre-fix stale cache on a disconnected start).
                    _workspaceStore.SetServicePartnerInfo(null);
                    _servicePartnerETag = null; // force a full re-fetch on the next connect

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
                mapCount,
                ProtocolVersion))
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

        // The heartbeat response carries whether a newer build (same channel) is available - the cloud does the
        // compare (it knows the latest version), so we just cache the result for the UI. Tolerate an older cloud
        // that doesn't return the fields.
        try
        {
            var body = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(cancellationToken);
            _updateState.Set(body?.UpdateAvailable ?? false, body?.LatestVersion);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* older cloud without the fields, or a non-JSON body - ignore */ }

        await FetchLicenseAsync(client, instanceId, token, cancellationToken);

        // Service-partner (incl. the co-branding logo) rarely changes but the logo can be sizeable, so don't
        // pull it every heartbeat - fetch on the first beat then roughly every 10th (~10 min at the default cadence).
        if (_servicePartnerTick++ % 10 == 0)
        {
            await FetchServicePartnerAsync(client, instanceId, token, cancellationToken);
        }
    }

    private int _servicePartnerTick;
    // Last service-partner ETag, so the periodic re-fetch can send If-None-Match and 304 instead of
    // re-downloading the ~256KB logo. In-memory only (a restart just costs one full re-fetch).
    private string? _servicePartnerETag;

    private sealed record HeartbeatResponse(string? Status, string? LatestVersion, bool UpdateAvailable);

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

    /// <summary>Pulls the managing service partner + consent from the cloud and caches it for the System tab.</summary>
    private async Task FetchServicePartnerAsync(HttpClient client, Guid instanceId, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/instances/{instanceId}/service-partner");
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            if (_servicePartnerETag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", _servicePartnerETag);
            }
            using var response = await client.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode == 304)
            {
                return; // unchanged since the last fetch - keep the cached branding, skip the logo transfer
            }
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var payload = await response.Content.ReadFromJsonAsync<ServicePartnerResponse>(cancellationToken);
            if (payload is null)
            {
                return;
            }

            byte[]? logo = null;
            if (!string.IsNullOrWhiteSpace(payload.LogoBase64))
            {
                try
                {
                    logo = Convert.FromBase64String(payload.LogoBase64);
                }
                catch (FormatException)
                {
                    logo = null;
                }
            }

            _workspaceStore.SetServicePartnerInfo(payload.HasPartner
                ? new ServicePartnerInfo
                {
                    HasPartner = true,
                    Name = payload.Name,
                    ContactEmail = payload.ContactEmail,
                    ContactPhone = payload.ContactPhone,
                    CanManage = payload.CanManage,
                    BrandingSuppressed = payload.BrandingSuppressed,
                    ContactUrl = payload.ContactUrl,
                    BrandColor = payload.BrandColorHex,
                    LogoPng = logo,
                    LogoContentType = string.IsNullOrWhiteSpace(payload.LogoContentType) ? "image/png" : payload.LogoContentType,
                }
                : null);

            // Remember the ETag so the next (cadenced) fetch can 304 instead of re-downloading the logo.
            _servicePartnerETag = response.Headers.ETag?.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Matmon.Cloud service-partner fetch failed (will retry next heartbeat)");
        }
    }

    private sealed record ServicePartnerResponse(
        bool HasPartner,
        string? Name,
        string? ContactEmail,
        string? ContactPhone,
        bool CanManage,
        string? ContactUrl = null,
        string? BrandColorHex = null,
        string? LogoContentType = null,
        string? LogoBase64 = null,
        bool BrandingSuppressed = false);

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

    /// <summary>Version of the cloud&lt;-&gt;instance contract this instance speaks. Bump when the heartbeat/API
    /// contract changes; the cloud flags instances reporting an older protocol as "outdated".</summary>
    public const int ProtocolVersion = 1;

    private sealed record CloudHeartbeatRequest(
        string? Version, string? Host, string? OperatingSystem, int? SensorCount, int? ActiveAlerts,
        int? ErrorCount, int? WarningCount, int? AckCount, int? ProbeCount, int? MapCount, int? ProtocolVersion);
}
