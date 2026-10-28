using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Host.Services.Backup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>System > Backup > an SMB job's backups on the share: the list, and "copy to this machine" - after which the
/// normal restore (sections, passphrase) takes the file, so a backup on the NAS restores exactly like a local one.</summary>
public sealed class BackupSmbFilesModel(IMonitoringWorkspaceStore store, SmbBackupRunner smb) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid JobId { get; set; }

    public WorkspaceBackupJob? Job { get; private set; }

    public IReadOnlyList<SmbFile> Files { get; private set; } = [];

    public string? Problem { get; private set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Job = store.FindBackupJob(JobId);
        if (Job is not { Destination: BackupDestination.Smb })
        {
            return RedirectToPage("/Config", new { tab = "backup" });
        }

        try
        {
            Files = await smb.ListAsync(Job, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Problem = exception.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCopyAsync(string fileName, CancellationToken cancellationToken)
    {
        if (store.FindBackupJob(JobId) is not { Destination: BackupDestination.Smb } job)
        {
            return RedirectToPage("/Config", new { tab = "backup" });
        }

        try
        {
            var snapshot = await smb.CopyToLocalAsync(job, fileName, cancellationToken);
            return RedirectToPage("/BackupRestore", new { fileName = snapshot.FileName });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = $"Not copied: {exception.Message}";
            return RedirectToPage(new { jobId = JobId });
        }
    }
}
