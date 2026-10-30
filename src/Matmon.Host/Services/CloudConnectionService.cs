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
    private readonly SelfUpdate.SelfUpdateService _selfUpdate;
    private readonly CloudProbeRelay _probes;
    private readonly ILogger<CloudConnectionService> _logger;

    private string? _lastStatus;

    public CloudConnectionService(
        IMonitoringWorkspaceStore workspaceStore,
        MatmonRuntimeOptions runtimeOptions,
        CloudUpdateState updateState,
        SelfUpdate.SelfUpdateService selfUpdate,
        CloudProbeRelay probes,
        ILogger<CloudConnectionService> logger)
    {
        _workspaceStore = workspaceStore;
        _runtimeOptions = runtimeOptions;
        _updateState = updateState;
        _selfUpdate = selfUpdate;
        _probes = probes;
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
        var lastIntervalSeconds = 0;
        string? lastUpdatingTo = null;

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

                // The cadence is re-read from the resolved link each tick, so a UI change takes effect live. A CHANGED
                // cadence beats at once: the cloud's offline grace comes from the interval the last beat reported, so
                // waiting out a longer new interval first made the cloud declare the instance offline (and mail/push
                // everyone) before the beat that would have told it about the slower cadence.
                if (link.IntervalSeconds != lastIntervalSeconds)
                {
                    lastIntervalSeconds = link.IntervalSeconds;
                    lastHeartbeat = DateTimeOffset.MinValue;
                }

                // An update that starts (or ends without a restart) is told at once: the cloud console shows it over the
                // whole page, also to whoever opens the console while the instance is away restarting.
                var updatingTo = UpdatingTo();
                if (updatingTo != lastUpdatingTo)
                {
                    lastUpdatingTo = updatingTo;
                    lastHeartbeat = DateTimeOffset.MinValue;
                }

                if (DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(link.IntervalSeconds))
                {
                    lastHeartbeat = DateTimeOffset.UtcNow;
                    try
                    {
                        await SendHeartbeatAsync(client, link.BaseUrl!, link.InstanceId!.Value, link.Token!, link.IntervalSeconds, stoppingToken);
                    }
                    // Only a real shutdown ends the loop. An HttpClient timeout throws TaskCanceledException
                    // (an OperationCanceledException) whose token is NOT stoppingToken - catching that here used
                    // to break the loop, so ONE slow beat stopped all heartbeats until a restart (the instance
                    // then read "offline" in the cloud forever while still showing its last "ok"). Treat a
                    // timeout as a failed beat and retry on the next tick.
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Includes a stray HttpClient-timeout OperationCanceledException - log + keep ticking.
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

        // The remote probes (no agents) and the answers to probe-update commands from the last answer.
        var probeAnswers = _probes.PendingAnswers();
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
                intervalSeconds,
                UpdatingTo(),
                _selfUpdate.CanSelfUpdate,
                OfferedVersion(),
                _workspaceStore.GetSelfUpdateSettings().AllowCloudInstall,
                _commandAnswer?.Id,
                _commandAnswer?.Accepted,
                _commandAnswer?.Message,
                _probes.Reports(),
                probeAnswers.Count == 0 ? null : probeAnswers))
        };
        request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            RecordStatus(baseUrl, instanceId, "unauthorized (check the instance token, or reconnect)", heartbeatOk: false, force: true);
            _logger.LogWarning("Matmon.Cloud rejected the instance token");
            return;
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // The token is genuine but the cloud has blocked the instance - a deliberate lock, not a bad token.
            // Keep beating (retry) so the instance recovers the moment the administrator reactivates it.
            var reason = await ReadForbiddenReasonAsync(response, cancellationToken);
            var status = reason == "instance_deleted"
                ? "deleted in the cloud (contact your provider)"
                : "deactivated by the cloud administrator";
            RecordStatus(baseUrl, instanceId, status, heartbeatOk: false, force: true);
            _logger.LogWarning("Matmon.Cloud blocked this instance ({Reason})", reason ?? "deactivated");
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
            // The answer to the last command was carried by this beat - say it once.
            _commandAnswer = null;
            _probes.AnswersDelivered(probeAnswers);
            if (body?.InstallUpdate is { } command)
            {
                StartCloudInstall(command);
            }

            // Probe updates asked for in the cloud: queued here like the Probes page's Update, answered with the next beat.
            _probes.Handle(body?.UpdateProbes);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* older cloud without the fields, or a non-JSON body - ignore */ }

        await FetchLicenseAsync(client, instanceId, token, cancellationToken);

        // Service-partner (branding) is fetched EVERY heartbeat: the ETag below turns an unchanged
        // response into a ~zero-byte 304, so partner branding changes propagate within one beat (~60s)
        // instead of the old 1-in-10 sampling (~10 min) that made the cloud toggles look broken.
        await FetchServicePartnerAsync(client, instanceId, token, cancellationToken);
    }

    // Last service-partner ETag, so the per-beat re-fetch can send If-None-Match and 304 instead of
    // re-downloading the ~256KB logo. In-memory only (a restart just costs one full re-fetch).
    private string? _servicePartnerETag;

    private sealed record HeartbeatResponse(
        string? Status,
        string? LatestVersion,
        bool UpdateAvailable,
        InstallCommand? InstallUpdate = null,
        IReadOnlyList<CloudProbeCommand>? UpdateProbes = null);

    /// <summary>"Install the update", sent by the cloud with a heartbeat answer (owner, partner or platform admin).</summary>
    private sealed record InstallCommand(Guid Id, string? RequestedBy, string? Via);

    private sealed record CommandAnswer(Guid Id, bool Accepted, string? Message);

    /// <summary>The answer to the last install command, carried by the next heartbeat.</summary>
    private volatile CommandAnswer? _commandAnswer;

    private readonly HashSet<Guid> _handledCommands = [];

    /// <summary>Runs the install in the background (the heartbeat must not wait for a download) and keeps the answer for the
    /// next beat; a command already handled is ignored (an answer lost on the way could otherwise start it twice).</summary>
    private void StartCloudInstall(InstallCommand command)
    {
        lock (_handledCommands)
        {
            if (!_handledCommands.Add(command.Id))
            {
                return;
            }
        }

        var by = $"{command.RequestedBy ?? "someone"} ({command.Via ?? "Matmon Cloud"})";
        _ = Task.Run(async () =>
        {
            string? problem;
            try
            {
                problem = await _selfUpdate.InstallFromCloudAsync(by, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Update requested from Matmon Cloud failed to start");
                problem = exception.Message;
            }

            _commandAnswer = new CommandAnswer(command.Id, problem is null, problem ?? "started");
        });
    }

    /// <summary>The version this installation found and would install, else null.</summary>
    private string? OfferedVersion() => _selfUpdate.Status.Offer is { Available: true } offer ? offer.Version ?? "a new version" : null;

    private sealed record ForbiddenBody(string? Error);

    /// <summary>Reads the <c>{ "error": "instance_deactivated" | "instance_deleted" }</c> body the cloud returns on
    /// a 403 heartbeat, so the UI can explain the block. Tolerates an older cloud with an empty/non-JSON body.</summary>
    private static async Task<string?> ReadForbiddenReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ForbiddenBody>(cancellationToken);
            return body?.Error;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
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

    /// <summary>Pulls the managing service partner from the cloud and caches it for the System tab.</summary>
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
        int? HeartbeatIntervalSeconds,
        string? UpdatingTo,
        bool? CanSelfUpdate,
        string? UpdateOffer,
        bool? CloudInstallAllowed,
        Guid? UpdateCommandId,
        bool? UpdateCommandAccepted,
        string? UpdateCommandMessage,
        // Optional (protocol 1 stays): the remote probes and the answers to probe-update commands. An older cloud ignores them.
        IReadOnlyList<CloudProbeReport>? Probes,
        IReadOnlyList<CloudProbeCommandAnswer>? ProbeCommandAnswers);

    /// <summary>The version an update is being installed to right now ("installing" on System > Updates), else null.</summary>
    private string? UpdatingTo()
    {
        var status = _selfUpdate.Status;
        return status.Installing ? status.Offer?.Version ?? "a new version" : null;
    }
}
