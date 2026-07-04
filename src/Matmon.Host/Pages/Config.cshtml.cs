using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace Matmon.Host.Pages;

public class ConfigModel : PageModel
{
    private static readonly HttpClient CloudHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Official Matmon.Cloud address — the default the connect form is prefilled with.</summary>
    public const string DefaultCloudUrl = "https://cloud.matmon.eu";

    private readonly IConfigurationOverviewProvider _configurationOverviewProvider;
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly SummaryReportSender _summaryReportSender;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly ILicenseService _licenseService;

    public ConfigModel(
        IConfigurationOverviewProvider configurationOverviewProvider,
        IMonitoringWorkspaceStore workspaceStore,
        SummaryReportSender summaryReportSender,
        MatmonRuntimeOptions runtimeOptions,
        ILicenseService licenseService)
    {
        _configurationOverviewProvider = configurationOverviewProvider;
        _workspaceStore = workspaceStore;
        _summaryReportSender = summaryReportSender;
        _runtimeOptions = runtimeOptions;
        _licenseService = licenseService;
    }

    public CloudConnectionState CloudConnection { get; private set; } = new();

    public bool CloudUrlConfigured { get; private set; }

    public string? CloudUrl { get; private set; }

    /// <summary>UI-managed cloud link settings (persisted; token not exposed).</summary>
    public CloudConnectionSettings CloudSettings { get; private set; } = new();

    /// <summary>Whether the env-var bootstrap is set (shown as a hint; the UI takes over once used).</summary>
    public bool CloudEnvBootstrapSet { get; private set; }

    public LicenseInfo License { get; private set; } = LicenseInfo.Fallback();

    public int ProbeCount { get; private set; }

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
    public SummaryReportInput SummaryReport { get; set; } = new();

    public DateTimeOffset? SummaryReportLastSentUtc { get; private set; }

    public bool SummaryReportHasSmtp { get; private set; }

    [BindProperty]
    public IFormFile? BackupUpload { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string ActiveTab => NormalizeTab(Tab);

    public IActionResult OnGet()
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

    public IActionResult OnPostSaveSummaryReport()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        try
        {
            _workspaceStore.UpdateSummaryReportSettings(new SummaryReportSettings
            {
                Enabled = SummaryReport.Enabled,
                Cadence = SummaryReport.Cadence,
                HourOfDay = Math.Clamp(SummaryReport.HourOfDay, 0, 23),
                DayOfWeek = SummaryReport.DayOfWeek,
                Recipients = (SummaryReport.Recipients ?? string.Empty).Trim(),
                Subject = string.IsNullOrWhiteSpace(SummaryReport.Subject) ? "Matmon summary report" : SummaryReport.Subject.Trim(),
                AttachPdf = SummaryReport.AttachPdf
            });
            StatusMessage = "Summary report settings saved.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { tab = "reports" });
    }

    public async Task<IActionResult> OnPostSendSummaryReportNowAsync()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        try
        {
            var settings = _workspaceStore.GetSummaryReportSettings();
            var sent = await _summaryReportSender.SendAsync(settings, HttpContext.RequestAborted);
            StatusMessage = sent
                ? "Test summary report sent."
                : "Report not sent — check that recipients and SMTP settings are configured.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { tab = "reports" });
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
        StatusMessage = "Connected to Matmon.Cloud — the first heartbeat is sent within a few seconds.";
        return RedirectToPage(new { tab = "cloud" });
    }

    public IActionResult OnPostCloudUrl()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        var url = (CloudConnect.Url ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            ErrorMessage = "Enter a valid cloud URL.";
            return RedirectToPage(new { tab = "cloud" });
        }

        var settings = _workspaceStore.GetCloudConnectionSettings();
        if (string.IsNullOrWhiteSpace(settings.InstanceId) || !settings.HasToken)
        {
            ErrorMessage = "Connect to Matmon.Cloud first.";
            return RedirectToPage(new { tab = "cloud" });
        }

        // Keep the same instance id + token (null preserves the stored token); only the address changes.
        _workspaceStore.SetCloudConnectionSettings(url, settings.InstanceId, null, enabled: true);
        StatusMessage = $"Cloud URL updated to {url}.";
        return RedirectToPage(new { tab = "cloud" });
    }

    public IActionResult OnPostCloudDisconnect()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        _workspaceStore.DisconnectCloud();
        StatusMessage = "Disconnected from Matmon.Cloud.";
        return RedirectToPage(new { tab = "cloud" });
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
                ErrorMessage = "Cloud sign-in failed — check your e-mail and password.";
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

        if (CloudRelay.RelayAlerts && string.IsNullOrWhiteSpace(CloudRelay.Recipients))
        {
            ErrorMessage = "Add at least one recipient to relay alerts to the cloud.";
            return RedirectToPage(new { tab = "cloud" });
        }

        _workspaceStore.SetCloudRelaySettings(CloudRelay.RelayAlerts, CloudRelay.Recipients);
        StatusMessage = CloudRelay.RelayAlerts ? "Cloud alert relay enabled." : "Cloud alert relay disabled.";
        return RedirectToPage(new { tab = "cloud" });
    }

    public IActionResult OnPostCloudFullAccess()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        _workspaceStore.SetCloudFullAccess(CloudFullAccess);
        StatusMessage = CloudFullAccess
            ? "Full Access enabled — you can now operate this instance from Matmon.Cloud."
            : "Full Access disabled.";
        return RedirectToPage(new { tab = "cloud" });
    }

    public IActionResult OnPostDownloadAuditPdf()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        try
        {
            var pdf = _summaryReportSender.BuildAuditPdf(SummaryReport.Cadence);
            return File(pdf, "application/pdf", $"matmon-audit-{DateTimeOffset.Now:yyyyMMdd-HHmm}.pdf");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage(new { tab = "reports" });
        }
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

        return timestampUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
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
            "users" => "users",
            _ => "general"
        };
    }

    private bool IsRestrictedTabRequestedByNonAdmin()
    {
        var tab = NormalizeTab(Tab);
        return (string.Equals(tab, "users", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tab, "backup", StringComparison.OrdinalIgnoreCase)) &&
            !MatmonSecurity.IsAdmin(User);
    }

    private void LoadView()
    {
        Overview = _configurationOverviewProvider.GetOverview();
        Users = _workspaceStore.GetUsers();

        var reportSettings = _workspaceStore.GetSummaryReportSettings();
        SummaryReport = new SummaryReportInput
        {
            Enabled = reportSettings.Enabled,
            Cadence = reportSettings.Cadence,
            HourOfDay = reportSettings.HourOfDay,
            DayOfWeek = reportSettings.DayOfWeek,
            Recipients = reportSettings.Recipients,
            Subject = reportSettings.Subject,
            AttachPdf = reportSettings.AttachPdf
        };
        License = _licenseService.Current;
        ProbeCount = _workspaceStore.GetAllElements().OfType<ProbeElement>().Count();
        CloudConnection = _workspaceStore.GetCloudConnection();
        CloudSettings = _workspaceStore.GetCloudConnectionSettings();
        CloudEnvBootstrapSet = !string.IsNullOrWhiteSpace(_runtimeOptions.CloudUrl);
        // Effective values shown in the form: UI settings once configured, else the env bootstrap.
        CloudUrl = CloudSettings.Configured ? CloudSettings.Url : _runtimeOptions.CloudUrl;
        CloudUrlConfigured = !string.IsNullOrWhiteSpace(CloudUrl);
        CloudConnect.Url ??= string.IsNullOrWhiteSpace(CloudUrl) ? DefaultCloudUrl : CloudUrl;
        CloudConnect.InstanceId ??= CloudSettings.Configured ? CloudSettings.InstanceId : _runtimeOptions.CloudInstanceId;
        CloudProvision.Url ??= string.IsNullOrWhiteSpace(CloudUrl) ? DefaultCloudUrl : CloudUrl;
        CloudProvision.Name ??= _workspaceStore.GetAllElements().OfType<ProbeElement>().FirstOrDefault()?.Name ?? Environment.MachineName;
        CloudRelay.Recipients ??= CloudSettings.RelayRecipients;
        if (!Request.HasFormContentType)
        {
            CloudRelay.RelayAlerts = CloudSettings.RelayAlerts;
            CloudFullAccess = CloudSettings.FullAccessEnabled;
        }

        SummaryReportLastSentUtc = reportSettings.LastSentUtc;
        SummaryReportHasSmtp = _workspaceStore.HasEmailNotifications() ||
            !string.IsNullOrWhiteSpace(_workspaceStore.Workspace.NotificationConfiguration.Email.SmtpHost);
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

    public string? Recipients { get; set; }
}

public sealed class SummaryReportInput
{
    public bool Enabled { get; set; }

    public SummaryReportCadence Cadence { get; set; } = SummaryReportCadence.Daily;

    public int HourOfDay { get; set; } = 7;

    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;

    public string Recipients { get; set; } = string.Empty;

    public string Subject { get; set; } = "Matmon summary report";

    public bool AttachPdf { get; set; }
}
