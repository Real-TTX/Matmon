using Matmon.Host.Services;
using Matmon.Host.Services.SelfUpdate;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// System > Updates (admins): which build runs, whether a newer one is there, install it now, or let the instance
/// install updates on its own inside a nightly window. How it updates depends on what it runs in
/// (<see cref="SelfUpdateMode"/>); when it cannot update itself the page says why and what would make it possible.
/// </summary>
public sealed class SystemUpdatesModel(SelfUpdateService updates, IMonitoringWorkspaceStore store) : PageModel
{
    [BindProperty]
    public SelfUpdateSettingsInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public SelfUpdateStatus Status { get; private set; } = default!;

    public SelfUpdateSettings Settings { get; private set; } = new();

    public string TimeZoneName => DisplayTimeZone.SystemDefault.Id;

    public void OnGet()
    {
        Load();
        Input = new SelfUpdateSettingsInput
        {
            AutoUpdate = Settings.AutoUpdate,
            WindowStartHour = Settings.WindowStartHour,
            WindowEndHour = Settings.WindowEndHour
        };
    }

    public async Task<IActionResult> OnPostCheckAsync(CancellationToken cancellationToken)
    {
        var status = await updates.CheckNowAsync(cancellationToken);
        if (status.LastError is not null)
        {
            ErrorMessage = $"The check failed: {status.LastError}";
        }
        else if (status.Offer is { } offer)
        {
            StatusMessage = offer.Available ? $"Update available: {offer.Version}." : $"No update: {offer.Detail}.";
        }
        else if (status.UnavailableReason is not null)
        {
            ErrorMessage = $"This installation cannot update itself: {status.UnavailableReason}.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostInstallAsync(CancellationToken cancellationToken)
    {
        var problem = await updates.InstallNowAsync(cancellationToken);
        if (problem is null)
        {
            StatusMessage = "The update is being installed. Matmon restarts in a moment - reload this page in a minute or two.";
        }
        else
        {
            ErrorMessage = $"The update did not start: {problem}";
        }

        return RedirectToPage();
    }

    public IActionResult OnPostSettings()
    {
        store.UpdateSelfUpdateSettings(new SelfUpdateSettings
        {
            AutoUpdate = Input.AutoUpdate,
            WindowStartHour = Input.WindowStartHour,
            WindowEndHour = Input.WindowEndHour
        });
        StatusMessage = Input.AutoUpdate
            ? $"Automatic updates are on - installed between {Input.WindowStartHour:00}:00 and {Input.WindowEndHour:00}:00."
            : "Automatic updates are off.";
        return RedirectToPage();
    }

    public static string Describe(SelfUpdateOutcome outcome) => outcome.Kind switch
    {
        SelfUpdateOutcomeKind.Updated => $"Updated from {outcome.FromVersion} to {outcome.ToVersion}",
        SelfUpdateOutcomeKind.RolledBack => $"The update to {outcome.ToVersion} was rolled back - {outcome.FromVersion} runs again",
        _ => $"The update to {outcome.ToVersion} failed"
    };

    private void Load()
    {
        Status = updates.Status;
        Settings = store.GetSelfUpdateSettings();
    }
}

public sealed class SelfUpdateSettingsInput
{
    public bool AutoUpdate { get; set; }

    public int WindowStartHour { get; set; } = 3;

    public int WindowEndHour { get; set; } = 5;
}
