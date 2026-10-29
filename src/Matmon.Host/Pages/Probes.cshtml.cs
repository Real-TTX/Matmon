using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Probe;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Where the sensors run - the local primary, remote Docker probes and agents - in ONE list. It used to be two (this page
/// for everyone, a System > Probes tab for admins with edit / install / token), which showed the same table twice with
/// different buttons; the admin actions are this page's row menu now, and the old tab sends its visitors here.
/// A remote Docker probe can be updated from here (it follows the primary's build, see <see cref="ProbeUpdatePolicy"/>);
/// an agent updates itself and gets no such button.
/// </summary>
public class ProbesModel : PageModel
{
    private readonly IConfigurationOverviewProvider _configurationOverviewProvider;
    private readonly ProbeUpdateCommands? _updateCommands;

    public ProbesModel(IConfigurationOverviewProvider configurationOverviewProvider, ProbeUpdateCommands? updateCommands = null)
    {
        _configurationOverviewProvider = configurationOverviewProvider;
        _updateCommands = updateCommands;
    }

    public ConfigurationOverview Overview { get; private set; } = default!;

    public IReadOnlyList<SystemProbeOverview> Probes => Overview.Probes;

    /// <summary>The primary's own build - what a Docker probe is updated to.</summary>
    public string PrimaryVersion => MatmonVersion.Current;

    /// <summary>Set by the actions that live on other pages and come back here (System's token rotation).</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
        Overview = _configurationOverviewProvider.GetOverview();
    }

    /// <summary>The update line of one row: null for the primary, an agent and a probe that never reported.</summary>
    public ProbeUpdateView? UpdateFor(SystemProbeOverview probe) =>
        probe.Role == "Primary" || probe.IsAgent ? null : ProbeUpdateView.Build(probe, PrimaryVersion, _updateCommands);

    /// <summary>The Docker probes the "Update all" button would update.</summary>
    public IReadOnlyList<SystemProbeOverview> UpdatableProbes =>
        Probes.Where(probe => UpdateFor(probe) is { CanStart: true }).ToList();

    public IActionResult OnPostUpdate(Guid probeElementId)
    {
        Overview = _configurationOverviewProvider.GetOverview();
        var probe = Probes.FirstOrDefault(entry => entry.ElementId == probeElementId);
        if (probe is null)
        {
            ErrorMessage = "That probe no longer exists.";
            return RedirectToPage();
        }

        var problem = Queue(probe);
        if (problem is null)
        {
            StatusMessage = $"{probe.Name} gets the update to {PrimaryVersion} with its next heartbeat.";
        }
        else
        {
            ErrorMessage = $"{probe.Name}: {problem}";
        }

        return RedirectToPage();
    }

    public IActionResult OnPostUpdateAll()
    {
        Overview = _configurationOverviewProvider.GetOverview();
        var queued = new List<string>();
        var problems = new List<string>();
        foreach (var probe in UpdatableProbes)
        {
            if (Queue(probe) is { } problem)
            {
                problems.Add($"{probe.Name}: {problem}");
            }
            else
            {
                queued.Add(probe.Name);
            }
        }

        if (queued.Count > 0)
        {
            StatusMessage = $"Update to {PrimaryVersion} queued for {string.Join(", ", queued)} - each takes it with its next heartbeat.";
        }

        if (problems.Count > 0 || queued.Count == 0)
        {
            ErrorMessage = problems.Count > 0 ? string.Join(" ", problems) : "No probe needs an update it can install itself.";
        }

        return RedirectToPage();
    }

    private string? Queue(SystemProbeOverview probe)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return "only an administrator can update a probe";
        }

        if (_updateCommands is null || UpdateFor(probe) is not { } view)
        {
            return "this probe cannot be updated from here";
        }

        if (!view.CanStart)
        {
            return view.CannotStartReason;
        }

        var by = User.Identity?.Name ?? "an admin";
        return _updateCommands.Queue(probe.ProbeId, PrimaryVersion, by);
    }
}

/// <summary>What the Probes page says about updating one remote Docker probe.</summary>
public sealed record ProbeUpdateView(
    /// <summary>The probe runs another build than the primary.</summary>
    bool Available,
    /// <summary>It reported that it can replace its own container (the Docker socket is mounted).</summary>
    bool CanUpdate,
    /// <summary>It sent a self-update report at all (a probe older than this feature does not).</summary>
    bool Reported,
    string? UnavailableReason,
    bool Updating,
    string? UpdatingTo,
    /// <summary>Queued, the probe has not taken it yet.</summary>
    bool Waiting,
    /// <summary>The probe refused the last command (or never took it): why.</summary>
    string? Refusal,
    string? Outcome,
    bool OutcomeProblem)
{
    /// <summary>Whether the Update action applies right now.</summary>
    public bool CanStart => Available && CanUpdate && !Updating && !Waiting;

    public string? CannotStartReason =>
        !Available ? "it already runs the primary's version"
        : !Reported ? "it is too old to be updated from here - pull and recreate it by hand once"
        : !CanUpdate ? $"it cannot update itself: {UnavailableReason ?? "no Docker socket"}"
        : Updating ? "it is being updated right now"
        : Waiting ? "an update is already waiting for its next heartbeat"
        : null;

    public static ProbeUpdateView Build(SystemProbeOverview probe, string primaryVersion, ProbeUpdateCommands? commands)
    {
        var report = probe.SelfUpdate;
        var state = commands?.State(probe.ProbeId);
        // Taken a moment ago and not on the target version yet - unless an outcome arrived since (a rollback or a failure
        // ends "updating" at once; found in the live test, where a rolled-back probe kept saying "updating" for 10 min).
        var endedSince = report?.LastOutcomeUtc is { } outcomeAt && state is not null && outcomeAt >= state.QueuedUtc - TimeSpan.FromMinutes(2); // a little slack for the probe's clock
        var updating = report?.Installing == true || (!endedSince && report?.CanUpdate == true && commands?.IsUpdating(probe.ProbeId, probe.Version) == true);
        return new ProbeUpdateView(
            ProbeUpdatePolicy.ShouldOffer(primaryVersion, probe.Version),
            report?.CanUpdate == true,
            report is not null,
            report?.UnavailableReason,
            updating,
            report?.InstallingTo ?? (updating ? state?.Command.Version : null),
            state?.Waiting == true,
            state is { Accepted: false } ? state.Message : null,
            DescribeOutcome(report),
            report?.LastOutcomeKind is "RolledBack" or "Failed");
    }

    /// <summary>"Updated from X to Y", "Rolled back to X: why", "Update to Y failed: why" - with the date.</summary>
    public static string? DescribeOutcome(ProbeSelfUpdateReport? report)
    {
        if (report?.LastOutcomeKind is not { } kind)
        {
            return null;
        }

        var when = report.LastOutcomeUtc is { } at ? $" ({at.ToDisplay():g})" : string.Empty;
        var detail = string.IsNullOrWhiteSpace(report.LastOutcomeDetail) ? string.Empty : ": " + report.LastOutcomeDetail;
        return kind switch
        {
            "Updated" => $"Updated from {report.LastOutcomeFrom ?? "?"} to {report.LastOutcomeTo ?? "?"}{when}",
            "RolledBack" => $"Update to {report.LastOutcomeTo ?? "?"} rolled back to {report.LastOutcomeFrom ?? "?"}{when}{detail}",
            _ => $"Update to {report.LastOutcomeTo ?? "?"} failed{when}{detail}"
        };
    }
}
