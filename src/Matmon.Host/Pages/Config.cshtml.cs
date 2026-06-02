using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.Globalization;

namespace Matmon.Host.Pages;

public class ConfigModel : PageModel
{
    private readonly IConfigurationOverviewProvider _configurationOverviewProvider;
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public ConfigModel(
        IConfigurationOverviewProvider configurationOverviewProvider,
        IMonitoringWorkspaceStore workspaceStore)
    {
        _configurationOverviewProvider = configurationOverviewProvider;
        _workspaceStore = workspaceStore;
    }

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
