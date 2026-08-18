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
            _workspaceStore.SetServicePartnerInfo(DemoServicePartnerSeed.Build(_runtimeOptions));
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

                // The cadence is re-read from the resolved link each tick, so a UI change takes effect live.
                if (DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(link.IntervalSeconds))
                {
                    lastHeartbeat = DateTimeOffset.UtcNow;
                    try
                    {
                        await SendHeartbeatAsync(client, link.BaseUrl!, link.InstanceId!.Value, link.Token!, link.IntervalSeconds, stoppingToken);
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
        int? intervalOverride = null;

        if (settings.Configured)
        {
            enabled = settings.Enabled;
            url = settings.Url;
            instanceIdRaw = settings.InstanceId;
            token = enabled ? _workspaceStore.GetCloudConnectionToken() : null;
            intervalOverride = settings.CloudHeartbeatIntervalSeconds;
        }
        else
        {
            url = _runtimeOptions.CloudUrl;
            instanceIdRaw = _runtimeOptions.CloudInstanceId;
            token = _runtimeOptions.CloudInstanceToken;
            enabled = !string.IsNullOrWhiteSpace(url);
        }

        // Effective heartbeat cadence: the UI override wins over the env/default fallback; floored at 15s.
        var intervalSeconds = Math.Max(15, intervalOverride ?? _runtimeOptions.CloudHeartbeatIntervalSeconds);

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

        return new ResolvedLink(true, baseUrl, baseUri, instanceId, token.Trim(), null, intervalSeconds);
    }

    private async Task SendHeartbeatAsync(HttpClient client, string baseUrl, Guid instanceId, string token, int intervalSeconds, CancellationToken cancellationToken)
    {
        // The aggregate metadata is optional (dead-man-switch only needs the beat itself). Never let a failure
        // gathering it abort the heartbeat - a healthy instance must not read as "offline" because a metrics
        // query threw. Fall back to nulls and still post the beat (which refreshes the cloud's online window).
        int? sensorCount = null, probeCount = null, mapCount = null;
        int? openAlerts = null, ackAlerts = null, errorAlerts = null, warningAlerts = null;
        try
        {
            var allElements = _workspaceStore.GetAllElements();
            sensorCount = allElements.OfType<SensorElement>().Count();
            probeCount = allElements.OfType<ProbeElement>().Count();
            mapCount = _workspaceStore.GetMaps().Count;
            (openAlerts, ackAlerts, errorAlerts, warningAlerts) = _workspaceStore.GetActiveAlertCounts();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to gather heartbeat metadata; sending a minimal beat");
        }

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
                ProtocolVersion,
                intervalSeconds))
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

        // Service-partner (branding + consent) is fetched EVERY heartbeat: the ETag below turns an unchanged
        // response into a ~zero-byte 304, so partner branding/consent changes propagate within one beat (~60s)
        // instead of the old 1-in-10 sampling (~10 min) that made the cloud toggles look broken.
        await FetchServicePartnerAsync(client, instanceId, token, cancellationToken);
    }

    // Last service-partner ETag, so the per-beat re-fetch can send If-None-Match and 304 instead of
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

            // Defense-in-depth at CACHE time (the last unvalidated cloud channel): only genuine PNG/JPEG bytes
            // are stored, and the served MIME comes from the magic bytes, NEVER from the payload - the cached
            // logo is served same-origin + anonymously (/api/branding/logo|favicon), so a script-bearing SVG
            // with a spoofed content type from a compromised cloud would otherwise be stored XSS.
            var logoBytes = DecodeBase64(payload.LogoBase64);
            var logoType = BrandingSafety.DetectRasterContentType(logoBytes);
            var smallLogoBytes = DecodeBase64(payload.SmallLogoBase64);
            var smallLogoType = BrandingSafety.DetectRasterContentType(smallLogoBytes);

            _workspaceStore.SetServicePartnerInfo(payload.HasPartner
                ? new ServicePartnerInfo
                {
                    HasPartner = true,
                    Name = payload.Name,
                    ContactEmail = payload.ContactEmail,
                    ContactPhone = payload.ContactPhone,
                    CanManage = payload.CanManage,
                    BrandingSuppressed = payload.BrandingSuppressed,
                    ProductName = payload.ProductName,
                    LogoIsOem = payload.LogoIsOem,
                    Slogan = payload.Slogan,
                    SidebarStyle = payload.SidebarStyle,
                    ContactUrl = payload.ContactUrl,
                    BrandColor = payload.BrandColorHex,
                    BrandColorSecondary = payload.BrandColorSecondaryHex,
                    LogoPng = logoType is null ? null : logoBytes,
                    LogoContentType = logoType,
                    SmallLogoPng = smallLogoType is null ? null : smallLogoBytes,
                    SmallLogoContentType = smallLogoType,
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

    private static byte[]? DecodeBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }
        try { return Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
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
        bool BrandingSuppressed = false,
        string? ProductName = null,
        bool LogoIsOem = false,
        string? BrandColorSecondaryHex = null,
        string? Slogan = null,
        int SidebarStyle = 0,
        string? SmallLogoContentType = null,
        string? SmallLogoBase64 = null);

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

    private sealed record ResolvedLink(bool Enabled, string? BaseUrl, Uri? BaseUri, Guid? InstanceId, string? Token, string? Status, int IntervalSeconds = 30)
    {
        public static ResolvedLink Off(string? baseUrl, string? status) => new(false, baseUrl, null, null, null, status);
    }

    /// <summary>Version of the cloud&lt;-&gt;instance contract this instance speaks. Bump when the heartbeat/API
    /// contract changes; the cloud flags instances reporting an older protocol as "outdated".</summary>
    public const int ProtocolVersion = 1;

    private sealed record CloudHeartbeatRequest(
        string? Version, string? Host, string? OperatingSystem, int? SensorCount, int? ActiveAlerts,
        int? ErrorCount, int? WarningCount, int? AckCount, int? ProbeCount, int? MapCount, int? ProtocolVersion,
        int? HeartbeatIntervalSeconds);
}
