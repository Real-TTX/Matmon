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
public sealed class SystemUpdatesModel(SelfUpdateService updates, IMonitoringWorkspaceStore store, InstanceRestarter restarter) : PageModel
{
    /// <summary>How "Restart" restarts this installation (or that nothing would bring it back).</summary>
    public RestartMethod RestartMethod => restarter.Method;
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

    public async Task OnGetAsync(string? installing, string? finished, string? to, string? restarting, bool restarted, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(restarting))
        {
            Screen = new UpdateScreen(restarting, restarting, Restart: true);
        }
        else if (restarted)
        {
            StatusMessage = "Matmon restarted.";
        }
        else if (!string.IsNullOrWhiteSpace(installing))
        {
            Screen = new UpdateScreen(installing, to ?? "the new version");
        }
        else if (!string.IsNullOrWhiteSpace(finished))
        {
            // Back after a restart: read the outcome now rather than at the first hourly look.
            await updates.RefreshOutcomeAsync(cancellationToken);
            Result = DescribeResult(finished, to, updates.Status);
        }
        else if (updates.Status is { Installing: true } running)
        {
            // Back on the page while an update is still running (from another page, another tab, the cloud console):
            // the same screen as right after "Install", not a page that looks idle.
            Screen = new UpdateScreen(running.CurrentVersion, running.Offer?.Version ?? "the new version");
        }

        Load();
        Input = new SelfUpdateSettingsInput
        {
            AutoUpdate = Settings.AutoUpdate,
            WindowStartHour = Settings.WindowStartHour,
            WindowEndHour = Settings.WindowEndHour,
            AllowCloudInstall = Settings.AllowCloudInstall
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

    /// <summary>How long "Install" waits for the install to fail early (no update, nothing to install, a download that
    /// breaks off at once) before it hands over to the update screen. Well below the 30 s after which the remote access
    /// tunnel gives up on a request - the download itself can take minutes.</summary>
    private static readonly TimeSpan InstallAnswerWait = TimeSpan.FromSeconds(10);

    public async Task<IActionResult> OnPostInstallAsync()
    {
        var target = updates.Status.Offer?.Version;
        // The install runs on in the service whatever happens to this request; the page only waits a moment for an
        // early refusal. It used to answer only after the download, which over remote access (30 s per request) ended
        // in a "replay failed" page while the update carried on unseen.
        var install = updates.InstallNowAsync(CancellationToken.None);
        if (await Task.WhenAny(install, Task.Delay(InstallAnswerWait)) == install && await install is { } problem)
        {
            ErrorMessage = $"The update did not start: {problem}";
            return RedirectToPage();
        }

        // The update screen watches the download and the restart and comes back with the outcome.
        return RedirectToPage(new { installing = MatmonVersion.Current, to = target });
    }

    /// <summary>The update screen asks this while Matmon is still up: once nothing is being installed any more and
    /// Matmon did not restart, the install failed (e.g. the download broke off) and the screen shows the outcome.
    /// It also carries what the install is doing - its steps and the download's progress - for the screen to show.</summary>
    public IActionResult OnGetState() => new JsonResult(BuildState(updates.Status));

    public static UpdateScreenState BuildState(SelfUpdateStatus status)
    {
        if (status.Progress is not { } progress)
        {
            return new UpdateScreenState(status.Installing, null, [], null);
        }

        var steps = progress.Steps.Select(step => new UpdateScreenStep(step.Key, step.Title, progress.StateOf(step.Key))).ToList();
        var download = progress.Download is { } state
            ? new UpdateScreenDownload(state.Done, state.Total, Math.Round(state.BytesPerSecond), state.SecondsLeft is { } left ? Math.Ceiling(left) : null,
                state.Attempt, state.Note, state.Complete)
            : null;
        return new UpdateScreenState(status.Installing, progress.Current, steps, download);
    }

    /// <summary>Restarts this installation; the restart screen watches it go away and come back.</summary>
    public IActionResult OnPostRestart()
    {
        if (updates.Status.Installing)
        {
            ErrorMessage = "An update is being installed - it restarts Matmon by itself.";
            return RedirectToPage();
        }

        if (restarter.Restart() is { } problem)
        {
            ErrorMessage = $"Matmon cannot restart itself: {problem}.";
            return RedirectToPage();
        }

        return RedirectToPage(new { restarting = MatmonVersion.Current });
    }

    public IActionResult OnPostSettings()
    {
        store.UpdateSelfUpdateSettings(new SelfUpdateSettings
        {
            AutoUpdate = Input.AutoUpdate,
            WindowStartHour = Input.WindowStartHour,
            WindowEndHour = Input.WindowEndHour,
            AllowCloudInstall = Input.AllowCloudInstall
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

/// <param name="Restart">A restart, not an update: the screen waits for Matmon to go away and come back, whatever version.</param>
public sealed record UpdateScreen(string FromVersion, string ToVersion, bool Restart = false);

/// <param name="Tone">success | warning | danger - the MatUi alert it is shown in.</param>
public sealed record UpdateResult(string Tone, string Message, string? Detail);

public sealed class SelfUpdateSettingsInput
{
    public bool AutoUpdate { get; set; }

    public int WindowStartHour { get; set; } = 3;

    public int WindowEndHour { get; set; } = 5;

    public bool AllowCloudInstall { get; set; } = true;
}

/// <summary>What the update screen polls (<c>?handler=State</c>): installing or not, the steps with their state
/// (done / current / pending) and, while a package downloads, how far it is.</summary>
public sealed record UpdateScreenState(bool Installing, string? Step, IReadOnlyList<UpdateScreenStep> Steps, UpdateScreenDownload? Download);

public sealed record UpdateScreenStep(string Key, string Title, string State);

/// <param name="SecondsLeft">At the current speed; null when that cannot be said yet.</param>
public sealed record UpdateScreenDownload(long Done, long Total, double BytesPerSecond, double? SecondsLeft, int Attempt, string? Note, bool Complete);
