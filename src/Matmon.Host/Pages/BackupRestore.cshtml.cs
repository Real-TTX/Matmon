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

    public WorkspaceBackupSnapshotInfo? Snapshot { get; private set; }

    public IReadOnlyList<BackupSectionChoice> SectionChoices => BackupSectionCatalog.GetChoices();

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

        if (string.IsNullOrWhiteSpace(FileName))
        {
            ErrorMessage = "Backup snapshot not found.";
            return RedirectToConfig();
        }

        Snapshot = _workspaceStore.FindBackupSnapshot(FileName);
        if (Snapshot is null)
        {
            ErrorMessage = $"Backup snapshot '{FileName}' was not found.";
            return RedirectToConfig();
        }

        Input = new BackupRestoreInput();
        Input.Sections.ApplySections(Snapshot.Sections);
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(FileName))
        {
            ErrorMessage = "Backup snapshot not found.";
            return RedirectToConfig();
        }

        Snapshot = _workspaceStore.FindBackupSnapshot(FileName);
        if (Snapshot is null)
        {
            ErrorMessage = $"Backup snapshot '{FileName}' was not found.";
            return RedirectToConfig();
        }

        if (!Input.Sections.HasAnySelected())
        {
            ModelState.AddModelError(string.Empty, "Select at least one section to restore.");
            return Page();
        }

        try
        {
            var result = _workspaceStore.RestoreBackupSnapshot(FileName, Input.Sections.ToSections(defaultToAll: false));
            StatusMessage = result.Message;
            return RedirectToConfig();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
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

    private IActionResult RedirectToConfig()
    {
        if (!string.IsNullOrWhiteSpace(ReturnUrl))
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
