using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Matmon.Host.Pages;

public class ConfigModel : PageModel
{
    private static readonly HttpClient CloudHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Official Matmon.Cloud address - the default the connect form is prefilled with.</summary>
    public const string DefaultCloudUrl = "https://cloud.matmon.eu";

    private readonly IConfigurationOverviewProvider _configurationOverviewProvider;
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly ILicenseService _licenseService;
    private readonly IDataProtectionProvider _dataProtection;

    public ConfigModel(
        IConfigurationOverviewProvider configurationOverviewProvider,
        IMonitoringWorkspaceStore workspaceStore,
        MatmonRuntimeOptions runtimeOptions,
        ILicenseService licenseService,
        IDataProtectionProvider dataProtection)
    {
        _configurationOverviewProvider = configurationOverviewProvider;
        _workspaceStore = workspaceStore;
        _runtimeOptions = runtimeOptions;
        _licenseService = licenseService;
        _dataProtection = dataProtection;
    }

    public CloudConnectionState CloudConnection { get; private set; } = new();

    public bool CloudUrlConfigured { get; private set; }

    public string? CloudUrl { get; private set; }

    /// <summary>UI-managed cloud link settings (persisted; token not exposed).</summary>
    public CloudConnectionSettings CloudSettings { get; private set; } = new();

    /// <summary>Whether the env-var bootstrap is set (shown as a hint; the UI takes over once used).</summary>
    public bool CloudEnvBootstrapSet { get; private set; }

    /// <summary>True when the cloud link is actively managing the license (connected via the UI, or the env
    /// bootstrap is set). While active, manual token entry is disabled - the cloud re-issues it on each heartbeat.</summary>
    public bool CloudLinkActive { get; private set; }

    /// <summary>System-default display timezone (IANA id; empty = server local). Applies to everyone without
    /// their own per-user override (set on /account).</summary>
    [BindProperty] public string? DisplayTimeZoneId { get; set; }
    public IReadOnlyList<SelectListItem> TimeZoneItems { get; private set; } = [];

    /// <summary>The managing service partner (from the cloud), shown on the Service partner tab; null if none.</summary>
    public ServicePartnerInfo? ServicePartnerInfo { get; private set; }

    public LicenseInfo License { get; private set; } = LicenseInfo.Fallback();

    /// <summary>Whether a license token is currently cached (cloud-issued or manually applied) - drives the Clear action.</summary>
    public bool HasStoredLicenseToken { get; private set; }

    /// <summary>Manual license token paste (offline / cloud-unreachable path).</summary>
    [BindProperty]
    public string? LicenseTokenInput { get; set; }

    public int ProbeCount { get; private set; }

    public int SensorCount { get; private set; }

    [BindProperty]
    public CloudProvisionInput CloudProvision { get; set; } = new();

    [BindProperty]
    public string? CloudRenameName { get; set; }

    [BindProperty]
    public CloudConnectInput CloudConnect { get; set; } = new();

    [BindProperty]
    public CloudRelayInput CloudRelay { get; set; } = new();

    [BindProperty]
    public bool CloudFullAccess { get; set; }

    public ConfigurationOverview Overview { get; private set; } = default!;

    public IReadOnlyList<MatmonUser> Users { get; private set; } = [];

    public IReadOnlyList<WorkspaceBackupJob> BackupJobs { get; private set; } = [];

    public IReadOnlyList<WorkspaceBackupSnapshotInfo> BackupSnapshots { get; private set; } = [];

    /// <summary>Config backups stored in Matmon.Cloud for this instance (loaded best-effort on the Backup tab).</summary>
    public IReadOnlyList<CloudBackupView> CloudBackups { get; private set; } = [];

    /// <summary>True when the cloud backup list was successfully fetched (distinguishes "empty" from "offline").</summary>
    public bool CloudBackupsAvailable { get; private set; }

    public sealed record CloudBackupView(Guid Id, DateTimeOffset CreatedUtc, string Label, string Version, long SizeBytes);

    public StorageTelemetryOverview StorageTelemetry { get; private set; } = new(0, 0, 0);

    public IReadOnlyList<SelectListItem> StorageCleanupScopeOptions { get; private set; } = [];

    public IReadOnlyList<SelectListItem> StorageCleanupAgeOptions { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? InstallProbeId { get; set; }

    [BindProperty]
    public StorageCleanupInput StorageCleanup { get; set; } = new();

    [BindProperty]
    public IFormFile? BackupUpload { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string ActiveTab => NormalizeTab(Tab);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (InstallProbeId.HasValue)
        {
            return RedirectToPage("/ProbeInstall", new { probeId = InstallProbeId.Value, returnUrl = "/Config?tab=probes" });
        }

        if (IsRestrictedTabRequestedByNonAdmin())
        {
            return Forbid();
        }

        LoadView();
        if (MatmonSecurity.IsAdmin(User) && IsActiveTab("backup"))
        {
            await LoadCloudBackupsAsync(cancellationToken);
        }

        return Page();
    }

    public IActionResult OnPostDeleteUser(Guid userId)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        try
        {
            if (MatmonSecurity.IsCurrentUser(User, userId))
            {
                throw new InvalidOperationException("You cannot delete the account you are currently using.");
            }

            _workspaceStore.DeleteUser(userId);
            StatusMessage = "User deleted.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { tab = "users" });
    }

    public IActionResult OnPostRotateProbeToken(Guid probeId)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        try
        {
            var element = _workspaceStore.FindElement(probeId)
                ?? throw new InvalidOperationException("Probe not found.");
            if (element is not ProbeElement probe)
            {
                throw new InvalidOperationException("Token can only be rotated for probes.");
            }

            _workspaceStore.RotateProbeToken(probe.Id);
            StatusMessage = $"Token for '{probe.Name}' rotated.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { tab = "probes" });
    }

    public IActionResult OnPostCleanupStorage()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        try
        {
            StorageCleanup.OlderThanDays = Math.Clamp(StorageCleanup.OlderThanDays, 0, 3650);
            var result = _workspaceStore.CleanupStorage(StorageCleanup.Scope, StorageCleanup.OlderThanDays);
            StatusMessage = result.TotalRemoved > 0
                ? $"Cleanup removed {FormatCount(result.TotalRemoved)} entries ({FormatCount(result.SensorHistoryRemoved)} history, {FormatCount(result.StatisticsRemoved)} statistics, {FormatCount(result.EventsRemoved)} events)."
                : "Cleanup finished. Nothing matched the selected age and scope.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { tab = "storage" });
    }

    public IActionResult OnPostRunBackupJob(Guid backupJobId)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        try
        {
            var snapshot = _workspaceStore.RunBackupJob(backupJobId, "Manual backup triggered from System > Backup.");
            StatusMessage = $"Backup '{snapshot.DisplayName}' created.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { tab = "backup" });
    }

    public IActionResult OnPostUploadBackupSnapshot()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        if (BackupUpload is null || BackupUpload.Length == 0)
        {
            ErrorMessage = "Select a backup file to upload.";
            return RedirectToPage(new { tab = "backup" });
        }

        try
        {
            using var stream = BackupUpload.OpenReadStream();
            var snapshot = _workspaceStore.ImportBackupSnapshot(stream, BackupUpload.FileName);
            StatusMessage = $"Backup '{snapshot.DisplayName}' uploaded.";
            return RedirectToPage("/BackupRestore", new { fileName = snapshot.FileName, returnUrl = "/Config?tab=backup" });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage(new { tab = "backup" });
        }
    }

    public IActionResult OnPostDeleteBackupJob(Guid backupJobId)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        try
        {
            if (!_workspaceStore.DeleteBackupJob(backupJobId))
            {
                throw new InvalidOperationException("Backup job not found.");
            }

            StatusMessage = "Backup job deleted.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { tab = "backup" });
    }

    public IActionResult OnPostCloudConnect()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        var url = (CloudConnect.Url ?? string.Empty).Trim();
        var instanceId = (CloudConnect.InstanceId ?? string.Empty).Trim();
        var token = (CloudConnect.Token ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(instanceId))
        {
            ErrorMessage = "Cloud URL and instance id are required.";
            return RedirectToPage(new { tab = "cloud" });
        }

        if (!Guid.TryParse(instanceId, out _))
        {
            ErrorMessage = "The instance id must be the GUID issued by Matmon.Cloud.";
            return RedirectToPage(new { tab = "cloud" });
        }

        // A token is required unless one is already stored (editing url/id without re-entering the secret).
        if (string.IsNullOrWhiteSpace(token) && !_workspaceStore.GetCloudConnectionSettings().HasToken)
        {
            ErrorMessage = "The instance token is required to connect.";
            return RedirectToPage(new { tab = "cloud" });
        }

        _workspaceStore.SetCloudConnectionSettings(url, instanceId, string.IsNullOrWhiteSpace(token) ? null : token, enabled: true);
        StatusMessage = "Connected to Matmon.Cloud - the first heartbeat is sent within a few seconds.";
        return RedirectToPage(new { tab = "cloud" });
    }

    /// <summary>Apply a signed license token by hand (offline / cloud-unreachable). Verified against the baked
    /// public key before it's stored, so an invalid/expired/foreign token is rejected.</summary>
    public IActionResult OnPostSetLicenseToken()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        // While the cloud link is active it OWNS the license (re-issued each heartbeat), so a manual token would
        // just be overwritten - refuse it and tell the admin to disconnect first for offline licensing.
        var cloud = _workspaceStore.GetCloudConnectionSettings();
        var cloudActive = cloud.Configured ? cloud.Enabled : !string.IsNullOrWhiteSpace(_runtimeOptions.CloudUrl);
        if (cloudActive)
        {
            ErrorMessage = "Manual license entry is disabled while connected to Matmon.Cloud - the cloud manages the license and would overwrite it on the next heartbeat. Disconnect the cloud first (System → Cloud).";
            return RedirectToPage(new { tab = "license" });
        }

        var token = (LicenseTokenInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ErrorMessage = "Paste a license token to apply.";
            return RedirectToPage(new { tab = "license" });
        }

        var verified = LicenseCrypto.Verify(token, LicensePublicKey.Spki);
        if (verified is null)
        {
            ErrorMessage = "That token is not valid (wrong signature, expired, or malformed). Nothing was changed.";
            return RedirectToPage(new { tab = "license" });
        }

        _workspaceStore.SetLicenseToken(token);
        StatusMessage = $"License applied: {verified.DisplayName}. Note: a connected cloud will overwrite this on its next heartbeat.";
        return RedirectToPage(new { tab = "license" });
    }

    /// <summary>Set the system-default display timezone (admin). Applied live so timestamps switch immediately.</summary>
    public IActionResult OnPostDisplayTimeZone()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        _workspaceStore.SetDisplayTimeZoneId(DisplayTimeZoneId);
        DisplayTimeZone.SystemDefault = DisplayTimeZone.Resolve(DisplayTimeZoneId) ?? TimeZoneInfo.Local;
        StatusMessage = "System display timezone saved.";
        return RedirectToPage(new { tab = "general" });
    }

    /// <summary>Set the customer's consent for the managing service partner to access this instance. Posts the
    /// change to Matmon.Cloud (the authority); the next heartbeat re-syncs it. Admin-only.</summary>
    // Cloud config backup = everything except the bulky telemetry sections.
    private const WorkspaceBackupSection CloudConfigSections =
        WorkspaceBackupSection.All & ~(WorkspaceBackupSection.SensorHistory | WorkspaceBackupSection.Events | WorkspaceBackupSection.Statistics);

    private (string? Url, string? InstanceId, string? Token) ResolveCloud()
    {
        var settings = _workspaceStore.GetCloudConnectionSettings();
        var token = _workspaceStore.GetCloudConnectionToken();
        var url = (settings.Configured ? settings.Url : _runtimeOptions.CloudUrl)?.Trim().TrimEnd('/');
        var instanceId = settings.Configured ? settings.InstanceId : _runtimeOptions.CloudInstanceId;
        return string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(token)
            ? (null, null, null)
            : (url, instanceId, token);
    }

    private async Task LoadCloudBackupsAsync(CancellationToken cancellationToken)
    {
        var (url, instanceId, token) = ResolveCloud();
        if (url is null || instanceId is null || token is null)
        {
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/api/instances/{instanceId}/backups");
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await CloudHttp.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                CloudBackups = await response.Content.ReadFromJsonAsync<List<CloudBackupView>>(cts.Token) ?? [];
                CloudBackupsAvailable = true;
            }
        }
        catch (Exception)
        {
            // Best-effort: leave the list empty if the cloud is unreachable.
        }
    }

    public async Task<IActionResult> OnPostCloudBackupNowAsync(CancellationToken cancellationToken)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        var (url, instanceId, token) = ResolveCloud();
        if (url is null || instanceId is null || token is null)
        {
            ErrorMessage = "Not connected to Matmon.Cloud.";
            return RedirectToPage(new { tab = "backup" });
        }

        try
        {
            var bytes = _workspaceStore.CreateBackupBytes(CloudConfigSections, "Manual cloud backup");
            var label = Uri.EscapeDataString($"Config {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/instances/{instanceId}/backups?label={label}")
            {
                Content = new ByteArrayContent(bytes)
            };
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await CloudHttp.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                StatusMessage = "Configuration backed up to Matmon.Cloud.";
            }
            else
            {
                ErrorMessage = $"Matmon.Cloud rejected the backup ({(int)response.StatusCode}).";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not reach Matmon.Cloud: {ex.Message}";
        }

        return RedirectToPage(new { tab = "backup" });
    }

    public async Task<IActionResult> OnPostCloudRestoreBackupAsync(Guid backupId, CancellationToken cancellationToken)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        var (url, instanceId, token) = ResolveCloud();
        if (url is null || instanceId is null || token is null)
        {
            ErrorMessage = "Not connected to Matmon.Cloud.";
            return RedirectToPage(new { tab = "backup" });
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/api/instances/{instanceId}/backups/{backupId}");
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await CloudHttp.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                ErrorMessage = $"Could not download the backup ({(int)response.StatusCode}).";
                return RedirectToPage(new { tab = "backup" });
            }

            var blob = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var result = _workspaceStore.RestoreBackupBytes(blob, CloudConfigSections);
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Restore from cloud failed: {ex.Message}";
        }

        return RedirectToPage(new { tab = "backup" });
    }

    public async Task<IActionResult> OnPostCloudDeleteBackupAsync(Guid backupId, CancellationToken cancellationToken)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        var (url, instanceId, token) = ResolveCloud();
        if (url is not null && instanceId is not null && token is not null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"{url}/api/instances/{instanceId}/backups/{backupId}");
                request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
                using var response = await CloudHttp.SendAsync(request, cancellationToken);
                StatusMessage = response.IsSuccessStatusCode ? "Cloud backup deleted." : $"Delete failed ({(int)response.StatusCode}).";
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Delete failed: {ex.Message}";
            }
        }

        return RedirectToPage(new { tab = "backup" });
    }

    public async Task<IActionResult> OnPostServicePartnerConsentAsync(bool canManage, CancellationToken cancellationToken)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        var settings = _workspaceStore.GetCloudConnectionSettings();
        var token = _workspaceStore.GetCloudConnectionToken();
        var url = (settings.Configured ? settings.Url : _runtimeOptions.CloudUrl)?.Trim().TrimEnd('/');
        var instanceId = settings.Configured ? settings.InstanceId : _runtimeOptions.CloudInstanceId;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(token))
        {
            ErrorMessage = "Not connected to Matmon.Cloud.";
            return RedirectToPage(new { tab = "partner" });
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/instances/{instanceId}/service-partner/consent")
            {
                Content = JsonContent.Create(new { canManage })
            };
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await CloudHttp.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                ErrorMessage = $"Matmon.Cloud rejected the change ({(int)response.StatusCode}).";
                return RedirectToPage(new { tab = "partner" });
            }

            // Reflect immediately; the next heartbeat re-syncs the authoritative value from the cloud.
            var current = _workspaceStore.GetServicePartnerInfo();
            if (current is not null)
            {
                current.CanManage = canManage;
                _workspaceStore.SetServicePartnerInfo(current);
            }
            StatusMessage = canManage ? "Service partner access granted." : "Service partner access revoked.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not reach Matmon.Cloud: {ex.Message}";
        }

        return RedirectToPage(new { tab = "partner" });
    }

    /// <summary>Clear the cached license token - the instance falls back to Free (until the cloud re-issues one).</summary>
    public IActionResult OnPostClearLicenseToken()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        _workspaceStore.SetLicenseToken(null);
        StatusMessage = "License token cleared - the instance is now on the Free fallback.";
        return RedirectToPage(new { tab = "license" });
    }

    public async Task<IActionResult> OnPostCloudDisconnect()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        // Tell the cloud we're unlinking FIRST (while the token is still present) so it marks this instance
        // offline immediately, instead of showing it "online" until the heartbeat times out (~150s). Best-effort.
        await NotifyCloudDisconnectAsync();

        _workspaceStore.DisconnectCloud();
        StatusMessage = "Disconnected from Matmon.Cloud.";
        return RedirectToPage(new { tab = "cloud" });
    }

    private async Task NotifyCloudDisconnectAsync()
    {
        try
        {
            var settings = _workspaceStore.GetCloudConnectionSettings();
            var token = _workspaceStore.GetCloudConnectionToken();
            if (!settings.Enabled
                || string.IsNullOrWhiteSpace(settings.Url)
                || !Guid.TryParse(settings.InstanceId, out var instanceId)
                || string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var baseUrl = settings.Url.Trim().TrimEnd('/');
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/instances/{instanceId}/disconnect");
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await client.SendAsync(request);
        }
        catch
        {
            // Cloud unreachable or any error: the local disconnect still proceeds; the cloud will time the
            // instance out (~150s) as a fallback. A deliberate disconnect must never fail on the cloud round-trip.
        }
    }

    /// <summary>
    /// UniFi-style connect (OAuth): redirect the admin's browser to the cloud consent page to claim this
    /// instance. PKCE - we keep the verifier in a data-protected cookie and send only the challenge; the
    /// callback (<see cref="CloudClaimModel"/>) redeems the returned code for the id + token. The account
    /// password never touches this instance.
    /// </summary>
    public IActionResult OnPostCloudClaim(string? returnUrl)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        var url = (CloudProvision.Url ?? string.Empty).Trim().TrimEnd('/');
        var name = (CloudProvision.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.MachineName;
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var cloudUri)
            || (cloudUri.Scheme != Uri.UriSchemeHttp && cloudUri.Scheme != Uri.UriSchemeHttps))
        {
            ErrorMessage = "The cloud URL is not a valid address.";
            return RedirectToPage(new { tab = "cloud" });
        }

        var nonce = CloudClaimFlow.Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = CloudClaimFlow.Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = CloudClaimFlow.Challenge(verifier);

        // Carry a local return target (e.g. the setup wizard's cloud step) through the PKCE round-trip so the
        // callback sends the browser back there instead of always landing on System → Cloud.
        var safeReturn = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        var payload = JsonSerializer.Serialize(new CloudClaimFlow.State(nonce, verifier, url, safeReturn));
        var protector = _dataProtection.CreateProtector(CloudClaimFlow.ProtectorPurpose);
        Response.Cookies.Append(CloudClaimFlow.CookieName, protector.Protect(payload), new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        var redirectUri = $"{Request.Scheme}://{Request.Host}{Url.Content("~/CloudClaim")}";
        var target = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"{url}/instances/claim", new Dictionary<string, string?>
        {
            ["redirect_uri"] = redirectUri,
            ["state"] = nonce,
            ["name"] = name,
            ["code_challenge"] = challenge
        });
        return Redirect(target);
    }

    /// <summary>UniFi-style connect: sign in to the cloud account + self-register this instance by name.</summary>
    public async Task<IActionResult> OnPostCloudProvisionAsync(CancellationToken cancellationToken)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        var url = (CloudProvision.Url ?? string.Empty).Trim().TrimEnd('/');
        var email = (CloudProvision.Email ?? string.Empty).Trim();
        var password = CloudProvision.Password ?? string.Empty;
        var name = (CloudProvision.Name ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ErrorMessage = "Cloud URL, e-mail and password are required.";
            return RedirectToPage(new { tab = "cloud" });
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            ErrorMessage = "The cloud URL is not a valid address.";
            return RedirectToPage(new { tab = "cloud" });
        }

        try
        {
            using var response = await CloudHttp.PostAsJsonAsync($"{url}/api/provision", new { email, password, name }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                ErrorMessage = "Cloud sign-in failed - check your e-mail and password.";
                return RedirectToPage(new { tab = "cloud" });
            }

            if (!response.IsSuccessStatusCode)
            {
                ErrorMessage = $"Cloud provisioning failed ({(int)response.StatusCode}).";
                return RedirectToPage(new { tab = "cloud" });
            }

            var result = await response.Content.ReadFromJsonAsync<ProvisionResult>(cancellationToken);
            if (result is null || string.IsNullOrWhiteSpace(result.InstanceId) || string.IsNullOrWhiteSpace(result.Token))
            {
                ErrorMessage = "Matmon.Cloud returned an unexpected response.";
                return RedirectToPage(new { tab = "cloud" });
            }

            _workspaceStore.SetCloudConnectionSettings(url, result.InstanceId, result.Token, enabled: true);
            StatusMessage = $"Connected to Matmon.Cloud as '{name}'.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not reach Matmon.Cloud: {ex.Message}";
        }

        return RedirectToPage(new { tab = "cloud" });
    }

    public async Task<IActionResult> OnPostCloudRenameAsync(CancellationToken cancellationToken)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        var name = (CloudRenameName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Enter a display name.";
            return RedirectToPage(new { tab = "cloud" });
        }

        var settings = _workspaceStore.GetCloudConnectionSettings();
        var url = (settings.Url ?? string.Empty).Trim().TrimEnd('/');
        var token = _workspaceStore.GetCloudConnectionToken();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(settings.InstanceId) || string.IsNullOrWhiteSpace(token))
        {
            ErrorMessage = "Connect to Matmon.Cloud first, then set the display name.";
            return RedirectToPage(new { tab = "cloud" });
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/instances/{settings.InstanceId}/name")
            {
                Content = JsonContent.Create(new { name })
            };
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await CloudHttp.SendAsync(request, cancellationToken);
            StatusMessage = response.IsSuccessStatusCode
                ? $"Cloud display name set to '{name}'."
                : $"Matmon.Cloud rejected the rename ({(int)response.StatusCode}).";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not reach Matmon.Cloud: {ex.Message}";
        }

        return RedirectToPage(new { tab = "cloud" });
    }

    private sealed record ProvisionResult(string? InstanceId, string? Token);

    public IActionResult OnPostCloudRelay()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        _workspaceStore.SetCloudRelaySettings(CloudRelay.RelayAlerts);
        StatusMessage = CloudRelay.RelayAlerts
            ? "Cloud alert relay enabled - use the \"Matmon Cloud\" sender in your notification rules."
            : "Cloud alert relay disabled.";
        return RedirectToPage(new { tab = "cloud" });
    }

    public IActionResult OnPostCloudFullAccess()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        // Full Access is a licensed feature - refuse to enable it on a plan that doesn't include it.
        if (CloudFullAccess && !_licenseService.Current.TunnelEnabled)
        {
            ErrorMessage = "Full Access isn't included in your plan. Upgrade the plan in Matmon.Cloud to enable it.";
            return RedirectToPage(new { tab = "cloud" });
        }

        _workspaceStore.SetCloudFullAccess(CloudFullAccess);
        StatusMessage = CloudFullAccess
            ? "Full Access enabled - you can now operate this instance from Matmon.Cloud."
            : "Full Access disabled.";
        return RedirectToPage(new { tab = "cloud" });
    }

    public bool IsActiveTab(string tab)
    {
        return string.Equals(ActiveTab, tab, StringComparison.OrdinalIgnoreCase);
    }

    public string FormatDateTime(DateTimeOffset? timestampUtc)
    {
        if (timestampUtc is null)
        {
            return "-";
        }

        return timestampUtc.Value.ToDisplay().ToString("dd.MM.yyyy HH:mm:ss");
    }

    public static string FormatPercent(double? value)
    {
        return value.HasValue
            ? $"{value.Value:0.##}%"
            : "-";
    }

    public static string FormatCount(long value)
    {
        return value.ToString("N0", CultureInfo.CurrentCulture);
    }

    public bool IsCurrentUser(MatmonUser user)
    {
        return MatmonSecurity.IsCurrentUser(User, user.Id);
    }

    /// <summary>Whether the account has a local password (cloud/SSO accounts may not - shown on the user row).</summary>
    public bool HasLocalPassword(MatmonUser user) => _workspaceStore.HasLocalPassword(user.Id);

    public bool CanInstallProbe(SystemProbeOverview probe)
    {
        return ProbeInstallCommandBuilder.CanInstallProbe(probe);
    }

    public static string FormatBackupSections(WorkspaceBackupSection sections)
    {
        return BackupSectionCatalog.Format(sections);
    }

    private static string NormalizeTab(string? tab)
    {
        return tab?.Trim().ToLowerInvariant() switch
        {
            "probes" => "probes",
            "storage" => "storage",
            "backup" => "backup",
            "reports" => "reports",
            "cloud" => "cloud",
            "license" => "license",
            "partner" => "partner",
            "users" => "users",
            _ => "general"
        };
    }

    private bool IsRestrictedTabRequestedByNonAdmin()
    {
        var tab = NormalizeTab(Tab);
        return (string.Equals(tab, "users", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tab, "backup", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tab, "license", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tab, "partner", StringComparison.OrdinalIgnoreCase)) &&
            !MatmonSecurity.IsAdmin(User);
    }

    private void LoadView()
    {
        Overview = _configurationOverviewProvider.GetOverview();
        Users = _workspaceStore.GetUsers();
        License = _licenseService.Current;
        HasStoredLicenseToken = !string.IsNullOrEmpty(_workspaceStore.GetLicenseToken());
        var allElements = _workspaceStore.GetAllElements();
        ProbeCount = allElements.OfType<ProbeElement>().Count();
        SensorCount = allElements.OfType<SensorElement>().Count();
        CloudConnection = _workspaceStore.GetCloudConnection();
        CloudSettings = _workspaceStore.GetCloudConnectionSettings();
        CloudEnvBootstrapSet = !string.IsNullOrWhiteSpace(_runtimeOptions.CloudUrl);
        CloudLinkActive = CloudSettings.Configured ? CloudSettings.Enabled : CloudEnvBootstrapSet;
        DisplayTimeZoneId = _workspaceStore.GetDisplayTimeZoneId();
        TimeZoneItems = TimeZoneOptions.Build(DisplayTimeZoneId, "Server local");
        ServicePartnerInfo = _workspaceStore.GetServicePartnerInfo();
        // Effective values shown in the form: UI settings once configured, else the env bootstrap.
        CloudUrl = CloudSettings.Configured ? CloudSettings.Url : _runtimeOptions.CloudUrl;
        CloudUrlConfigured = !string.IsNullOrWhiteSpace(CloudUrl);
        CloudConnect.Url ??= string.IsNullOrWhiteSpace(CloudUrl) ? DefaultCloudUrl : CloudUrl;
        CloudConnect.InstanceId ??= CloudSettings.Configured ? CloudSettings.InstanceId : _runtimeOptions.CloudInstanceId;
        CloudProvision.Url ??= string.IsNullOrWhiteSpace(CloudUrl) ? DefaultCloudUrl : CloudUrl;
        CloudProvision.Name ??= _workspaceStore.GetAllElements().OfType<ProbeElement>().FirstOrDefault()?.Name ?? Environment.MachineName;
        if (!Request.HasFormContentType)
        {
            CloudRelay.RelayAlerts = CloudSettings.RelayAlerts;
            CloudFullAccess = CloudSettings.FullAccessEnabled;
        }

        BackupJobs = _workspaceStore.GetBackupJobs();
        BackupSnapshots = _workspaceStore.GetBackupSnapshots();
        StorageTelemetry = _workspaceStore.GetStorageTelemetryOverview();
        StorageCleanupScopeOptions =
        [
            new SelectListItem("Telemetry history + statistics", StorageCleanupScope.Telemetry.ToString()),
            new SelectListItem("Sensor history only", StorageCleanupScope.SensorHistory.ToString()),
            new SelectListItem("Events only", StorageCleanupScope.Events.ToString()),
            new SelectListItem("Statistics only", StorageCleanupScope.Statistics.ToString()),
            new SelectListItem("Everything", StorageCleanupScope.Everything.ToString())
        ];
        StorageCleanupAgeOptions =
        [
            new SelectListItem("Older than 7 days", "7"),
            new SelectListItem("Older than 30 days", "30"),
            new SelectListItem("Older than 90 days", "90"),
            new SelectListItem("Older than 180 days", "180"),
            new SelectListItem("Older than 365 days", "365"),
            new SelectListItem("All selected data", "0")
        ];
    }
}

public sealed class StorageCleanupInput
{
    public StorageCleanupScope Scope { get; set; } = StorageCleanupScope.Telemetry;

    public int OlderThanDays { get; set; } = 30;
}

public sealed class CloudProvisionInput
{
    public string? Url { get; set; }

    public string? Email { get; set; }

    public string? Password { get; set; }

    public string? Name { get; set; }
}

public sealed class CloudConnectInput
{
    public string? Url { get; set; }

    public string? InstanceId { get; set; }

    public string? Token { get; set; }
}

public sealed class CloudRelayInput
{
    public bool RelayAlerts { get; set; }
}

