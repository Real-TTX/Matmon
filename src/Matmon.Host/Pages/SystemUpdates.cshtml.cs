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

    /// <summary>The update screen: shown right after "Install", it waits for Matmon to come back and then reloads with
    /// <see cref="Result"/>.</summary>
    public UpdateScreen? Screen { get; private set; }

    /// <summary>How the update just watched by the update screen ended - success, rolled back, failed, or not known yet.</summary>
    public UpdateResult? Result { get; private set; }

    public async Task OnGetAsync(string? installing, string? finished, string? to, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(installing))
        {
            Screen = new UpdateScreen(installing, to ?? "the new version");
        }
        else if (!string.IsNullOrWhiteSpace(finished))
        {
            // Back after a restart: read the outcome now rather than at the first hourly look.
            await updates.RefreshOutcomeAsync(cancellationToken);
            Result = DescribeResult(finished, to, updates.Status);
        }

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
        var target = updates.Status.Offer?.Version;
        var problem = await updates.InstallNowAsync(cancellationToken);
        if (problem is not null)
        {
            ErrorMessage = $"The update did not start: {problem}";
            return RedirectToPage();
        }

        // The update screen watches the restart and comes back with the outcome.
        return RedirectToPage(new { installing = MatmonVersion.Current, to = target });
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

    /// <summary>
    /// The outcome of the update the screen watched: the installation's own record when it is about this update and
    /// fresh, else what the running version says (a Docker helper writes its record a moment after the new container
    /// is up, so the version may be known before the record is).
    /// </summary>
    public static UpdateResult DescribeResult(string fromVersion, string? toVersion, SelfUpdateStatus status)
    {
        if (status.LastOutcome is { } outcome
            && DateTimeOffset.UtcNow - outcome.AtUtc < TimeSpan.FromMinutes(30)
            && string.Equals(outcome.FromVersion, fromVersion, StringComparison.Ordinal))
        {
            return outcome.Kind switch
            {
                SelfUpdateOutcomeKind.Updated => new UpdateResult("success", $"Matmon was updated to {outcome.ToVersion}.", outcome.Detail),
                SelfUpdateOutcomeKind.RolledBack => new UpdateResult("danger",
                    $"The update to {outcome.ToVersion} did not come up and was rolled back - {outcome.FromVersion} runs again.", outcome.Detail),
                _ => new UpdateResult("danger", $"The update to {outcome.ToVersion} failed - {fromVersion} still runs.", outcome.Detail)
            };
        }

        return !string.Equals(status.CurrentVersion, fromVersion, StringComparison.Ordinal)
            ? new UpdateResult("success", $"Matmon was updated to {status.CurrentVersion}.", null)
            : new UpdateResult("warning",
                $"Matmon {status.CurrentVersion} is running; the update{(string.IsNullOrWhiteSpace(toVersion) ? "" : $" to {toVersion}")} did not complete.",
                "Its outcome appears under Last update as soon as it is known.");
    }

    private void Load()
    {
        Status = updates.Status;
        Settings = store.GetSelfUpdateSettings();
    }
}

public sealed record UpdateScreen(string FromVersion, string ToVersion);

/// <param name="Tone">success | warning | danger - the MatUi alert it is shown in.</param>
public sealed record UpdateResult(string Tone, string Message, string? Detail);

public sealed class SelfUpdateSettingsInput
{
    public bool AutoUpdate { get; set; }

    public int WindowStartHour { get; set; } = 3;

    public int WindowEndHour { get; set; } = 5;
}
