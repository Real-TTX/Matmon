using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public class BackupRestoreModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public BackupRestoreModel(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    [BindProperty(SupportsGet = true)]
    public string? FileName { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public BackupRestoreInput Input { get; set; } = new();

    public WorkspaceBackupSnapshotDetails? SnapshotDetails { get; private set; }

    public WorkspaceBackupSnapshotInfo? Snapshot => SnapshotDetails?.Snapshot;

    public IReadOnlyList<BackupRestoreSectionItem> SectionItems { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        if (!TryLoadSnapshot(resetSelection: true))
        {
            return RedirectToConfig();
        }
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        if (!TryLoadSnapshot(resetSelection: false))
        {
            return RedirectToConfig();
        }

        if (!Input.Sections.HasAnySelected())
        {
            ModelState.AddModelError(string.Empty, "Select at least one section to restore.");
            return Page();
        }

        try
        {
            var result = _workspaceStore.RestoreBackupSnapshot(FileName!, Input.Sections.ToSections(defaultToAll: false));
            StatusMessage = result.Message;
            return RedirectToConfig();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }

    public IActionResult OnGetDownload()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(FileName))
        {
            return NotFound();
        }

        var snapshot = _workspaceStore.FindBackupSnapshot(FileName);
        if (snapshot is null)
        {
            return NotFound();
        }

        var stream = _workspaceStore.OpenBackupSnapshotReadStream(snapshot.FileName);
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, "application/json", snapshot.FileName);
    }

    public string FormatDateTime(DateTimeOffset? timestampUtc)
    {
        if (timestampUtc is null)
        {
            return "-";
        }

        return timestampUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    }

    public string FormatSections(WorkspaceBackupSection sections)
    {
        return BackupSectionCatalog.Format(sections);
    }

    public static string FormatCount(int value)
    {
        return value.ToString("N0");
    }

    public static int CountSections(WorkspaceBackupSection sections)
    {
        return Enum.GetValues<WorkspaceBackupSection>()
            .Count(section => section is not WorkspaceBackupSection.None and not WorkspaceBackupSection.All && sections.HasFlag(section));
    }

    private bool TryLoadSnapshot(bool resetSelection)
    {
        if (string.IsNullOrWhiteSpace(FileName))
        {
            ErrorMessage = "Backup snapshot not found.";
            return false;
        }

        SnapshotDetails = _workspaceStore.FindBackupSnapshotDetails(FileName);
        if (SnapshotDetails is null)
        {
            ErrorMessage = $"Backup snapshot '{FileName}' was not found.";
            return false;
        }

        if (resetSelection)
        {
            Input = new BackupRestoreInput();
            Input.Sections.ApplySections(SnapshotDetails.Snapshot.Sections);
        }

        SectionItems = SnapshotDetails.Sections
            .Select(section => new BackupRestoreSectionItem(
                section.Section,
                section.Label,
                section.Description,
                section.Summary,
                section.ItemCount,
                section.Included,
                IsSectionSelected(section.Section),
                GetSectionFieldName(section.Section)))
            .ToArray();

        return true;
    }

    private bool IsSectionSelected(WorkspaceBackupSection section)
    {
        return section switch
        {
            WorkspaceBackupSection.Topology => Input.Sections.Topology,
            WorkspaceBackupSection.Templates => Input.Sections.Templates,
            WorkspaceBackupSection.SensorDefinitions => Input.Sections.SensorDefinitions,
            WorkspaceBackupSection.Notifications => Input.Sections.Notifications,
            WorkspaceBackupSection.Maps => Input.Sections.Maps,
            WorkspaceBackupSection.Users => Input.Sections.Users,
            WorkspaceBackupSection.Alerts => Input.Sections.Alerts,
            WorkspaceBackupSection.SensorHistory => Input.Sections.SensorHistory,
            WorkspaceBackupSection.Events => Input.Sections.Events,
            WorkspaceBackupSection.Statistics => Input.Sections.Statistics,
            WorkspaceBackupSection.BackupJobs => Input.Sections.BackupJobs,
            _ => false
        };
    }

    private static string GetSectionFieldName(WorkspaceBackupSection section)
    {
        return section switch
        {
            WorkspaceBackupSection.Topology => "Input.Sections.Topology",
            WorkspaceBackupSection.Templates => "Input.Sections.Templates",
            WorkspaceBackupSection.SensorDefinitions => "Input.Sections.SensorDefinitions",
            WorkspaceBackupSection.Notifications => "Input.Sections.Notifications",
            WorkspaceBackupSection.Maps => "Input.Sections.Maps",
            WorkspaceBackupSection.Users => "Input.Sections.Users",
            WorkspaceBackupSection.Alerts => "Input.Sections.Alerts",
            WorkspaceBackupSection.SensorHistory => "Input.Sections.SensorHistory",
            WorkspaceBackupSection.Events => "Input.Sections.Events",
            WorkspaceBackupSection.Statistics => "Input.Sections.Statistics",
            WorkspaceBackupSection.BackupJobs => "Input.Sections.BackupJobs",
            _ => string.Empty
        };
    }

    private IActionResult RedirectToConfig()
    {
        // Only follow a local return URL — never an absolute/off-site one (open-redirect guard).
        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return Redirect(ReturnUrl);
        }

        return RedirectToPage("/Config", new { tab = "backup" });
    }
}

public sealed class BackupRestoreInput
{
    public BackupSectionSelectionModel Sections { get; set; } = new();
}

public sealed record BackupRestoreSectionItem(
    WorkspaceBackupSection Section,
    string Label,
    string Description,
    string Summary,
    int ItemCount,
    bool Included,
    bool Selected,
    string FieldName);
