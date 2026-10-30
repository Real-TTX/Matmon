using Matmon.Host.Pages;
using Matmon.Probe;

namespace Matmon.Host.Services;

/// <summary>One remote probe as the cloud heartbeat reports it (Matmon.Cloud shows it under its instance). Never an agent,
/// never the instance's own root probe, never a token.</summary>
public sealed record CloudProbeReport(
    Guid Id,
    string Name,
    string? Version,
    bool Connected,
    DateTimeOffset? LastSeenUtc,
    /// <summary>It said it can replace its own container (the Docker socket is mounted).</summary>
    bool CanUpdate,
    /// <summary>It runs another build than this instance (<see cref="ProbeUpdatePolicy.ShouldOffer"/>).</summary>
    bool UpdateAvailable,
    string? UpdatingTo,
    /// <summary>"Updated from X to Y (date)", "... rolled back ...", "... failed ...".</summary>
    string? LastOutcome,
    bool OutcomeProblem,
    /// <summary>Why the probe refused the last command (or never took it).</summary>
    string? Refusal,
    /// <summary>Why an update cannot be started right now, when one is available.</summary>
    string? CannotUpdateReason);

/// <summary>"Update this probe", sent by the cloud with a heartbeat answer (owner, instance admin or managing partner).</summary>
public sealed record CloudProbeCommand(Guid Id, Guid ProbeId, string? RequestedBy, string? Via);

/// <summary>The instance's answer to a probe command, carried by its next heartbeat.</summary>
public sealed record CloudProbeCommandAnswer(Guid Id, bool Accepted, string? Message);

/// <summary>
/// The remote probes in the cloud: their state rides every cloud heartbeat, and an update asked for in the cloud ("with a
/// detour through the primary" - the cloud cannot reach a probe) comes back with a heartbeat answer and is queued here
/// exactly like the Probes page's Update (<see cref="ProbeUpdateView.TryQueue"/>) - so the probe takes it with its next
/// heartbeat to this primary. Refused in words when the admin switched off "Matmon Cloud may start an update" (the same
/// switch as the instance's own update). Primary only, in memory: answers wait for the next beat, handled ids are
/// remembered so a repeat is ignored.
/// </summary>
public sealed class CloudProbeRelay(
    IConfigurationOverviewProvider overview,
    ProbeUpdateCommands commands,
    IMonitoringWorkspaceStore store,
    ILogger<CloudProbeRelay> logger)
{
    /// <summary>At most this many probes ride one heartbeat.</summary>
    public const int MaxProbes = 100;

    private readonly List<CloudProbeCommandAnswer> _answers = [];
    private readonly Queue<Guid> _handled = new();

    public IReadOnlyList<CloudProbeReport>? Reports()
    {
        try
        {
            return BuildReports(overview.GetProbes(), MatmonVersion.Current, commands);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not gather the remote probes for the cloud heartbeat");
            return null;
        }
    }

    /// <summary>The answers the next heartbeat carries.</summary>
    public IReadOnlyList<CloudProbeCommandAnswer> PendingAnswers()
    {
        lock (_answers)
        {
            return _answers.ToArray();
        }
    }

    /// <summary>A beat carrying these answers arrived - they are said.</summary>
    public void AnswersDelivered(IReadOnlyCollection<CloudProbeCommandAnswer> sent)
    {
        lock (_answers)
        {
            _answers.RemoveAll(sent.Contains);
        }
    }

    /// <summary>Queues the commands of a heartbeat answer; the answers ride the next beat.</summary>
    public void Handle(IReadOnlyList<CloudProbeCommand>? received)
    {
        if (received is not { Count: > 0 })
        {
            return;
        }

        var allowed = store.GetSelfUpdateSettings().AllowCloudInstall;
        IReadOnlyList<SystemProbeOverview>? probes = null;
        foreach (var command in received)
        {
            lock (_handled)
            {
                if (_handled.Contains(command.Id))
                {
                    continue;
                }

                _handled.Enqueue(command.Id);
                while (_handled.Count > 50)
                {
                    _handled.Dequeue();
                }
            }

            probes ??= overview.GetProbes();
            var answer = Decide(command, probes, MatmonVersion.Current, commands, allowed);
            var probeName = probes.FirstOrDefault(probe => probe.ElementId == command.ProbeId)?.Name ?? command.ProbeId.ToString();
            logger.LogWarning("Update of probe {Probe} requested from Matmon Cloud by {RequestedBy} ({Via}) - {Outcome}",
                probeName, command.RequestedBy ?? "someone", command.Via ?? "Matmon Cloud", answer.Accepted ? "queued" : "refused: " + answer.Message);
            lock (_answers)
            {
                _answers.Add(answer);
            }
        }
    }

    /// <summary>The remote probes (no agent, not the root) as the heartbeat reports them, at most <see cref="MaxProbes"/>.</summary>
    public static IReadOnlyList<CloudProbeReport> BuildReports(IEnumerable<SystemProbeOverview> probes, string primaryVersion, ProbeUpdateCommands? commands) =>
        probes
            .Where(IsRemoteProbe)
            .Take(MaxProbes)
            .Select(probe =>
            {
                var view = ProbeUpdateView.Build(probe, primaryVersion, commands);
                return new CloudProbeReport(
                    probe.ElementId,
                    probe.Name,
                    probe.Version,
                    probe.State == "Online",
                    probe.LastSeenUtc,
                    view.CanUpdate,
                    view.Available,
                    view.Updating ? view.UpdatingTo ?? primaryVersion : null,
                    view.Outcome,
                    view.OutcomeProblem,
                    view.Refusal,
                    view.Available && !view.CanStart ? view.CannotStartReason : null);
            })
            .ToArray();

    /// <summary>What the instance answers to one probe command from the cloud: queued (as from the Probes page) or why not.</summary>
    public static CloudProbeCommandAnswer Decide(
        CloudProbeCommand command,
        IReadOnlyList<SystemProbeOverview> probes,
        string primaryVersion,
        ProbeUpdateCommands commands,
        bool cloudInstallAllowed)
    {
        if (!cloudInstallAllowed)
        {
            return new CloudProbeCommandAnswer(command.Id, false, "updates from Matmon Cloud are switched off on this instance (System > Updates)");
        }

        var probe = probes.FirstOrDefault(entry => entry.ElementId == command.ProbeId);
        if (probe is null || !IsRemoteProbe(probe))
        {
            return new CloudProbeCommandAnswer(command.Id, false, "this instance has no such remote probe");
        }

        var by = $"{command.RequestedBy ?? "someone"} ({command.Via ?? "Matmon Cloud"}, through Matmon Cloud)";
        return ProbeUpdateView.TryQueue(probe, primaryVersion, commands, by) is { } problem
            ? new CloudProbeCommandAnswer(command.Id, false, problem)
            : new CloudProbeCommandAnswer(command.Id, true, $"queued - {probe.Name} takes the update to {primaryVersion} with its next heartbeat");
    }

    private static bool IsRemoteProbe(SystemProbeOverview probe) => probe.Role != "Primary" && !probe.IsAgent;
}
