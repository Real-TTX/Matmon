using Matmon.Probe;

namespace Matmon.Host.Services;

/// <summary>Where an update command for one probe stands - what the Probes page shows.</summary>
public sealed record ProbeUpdateCommandState(
    ProbeUpdateCommand Command,
    DateTimeOffset QueuedUtc,
    bool? Accepted,
    string? Message,
    DateTimeOffset? AnsweredUtc)
{
    /// <summary>Queued, the probe has not answered yet.</summary>
    public bool Waiting => Accepted is null;
}

/// <summary>
/// The update commands for remote Docker probes (Primary only, in memory - a restart of the primary forgets a command the
/// probe never took, which is harmless: the button is still there). One command per probe at a time. It rides EVERY
/// heartbeat answer until the probe's answer arrives (the probe remembers the ids it handled, so a repeat is ignored -
/// an answer lost on the way therefore never leaves the command hanging); a probe that reports the target version is
/// done whatever it answered, and a command nobody took within <see cref="AnswerTimeout"/> is given up. The pattern of
/// Matmon.Cloud's install command to an instance.
/// </summary>
public sealed class ProbeUpdateCommands(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(10);

    /// <summary>After the probe took the command: how long the page says "updating" (the helper waits 3 min for the new
    /// container, a pull can take a few more).</summary>
    public static readonly TimeSpan UpdatingWindow = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, ProbeUpdateCommandState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Queues "update to <paramref name="version"/>"; null = queued, else why not.</summary>
    public string? Queue(string probeId, string version, string requestedBy)
    {
        lock (_states)
        {
            if (_states.TryGetValue(probeId, out var existing) && existing.Waiting && !Expired(existing))
            {
                return "an update is already waiting for this probe's next heartbeat";
            }

            var command = new ProbeUpdateCommand(Guid.NewGuid().ToString("N"), version, requestedBy);
            _states[probeId] = new ProbeUpdateCommandState(command, _time.GetUtcNow(), null, null, null);
            return null;
        }
    }

    /// <summary>
    /// Called for every heartbeat of <paramref name="probeId"/>: records the probe's answer it carries, and returns the
    /// command to hand out (null when there is none or it was answered).
    /// </summary>
    public ProbeUpdateCommand? OnHeartbeat(string probeId, string? reportedVersion, ProbeSelfUpdateReport? report)
    {
        lock (_states)
        {
            if (!_states.TryGetValue(probeId, out var state) || !state.Waiting)
            {
                return null;
            }

            var now = _time.GetUtcNow();
            if (report?.CommandId == state.Command.Id && report.CommandAccepted is { } accepted)
            {
                _states[probeId] = state with { Accepted = accepted, Message = report.CommandMessage, AnsweredUtc = now };
                return null;
            }

            if (string.Equals(reportedVersion, state.Command.Version, StringComparison.OrdinalIgnoreCase))
            {
                // The new container answers before its predecessor's answer arrived: it is done all the same.
                _states[probeId] = state with { Accepted = true, Message = $"runs {reportedVersion}", AnsweredUtc = now };
                return null;
            }

            if (Expired(state))
            {
                _states[probeId] = state with { Accepted = false, Message = "the probe did not take the command (is it connected, and a version that knows it?)", AnsweredUtc = now };
                return null;
            }

            return state.Command;
        }
    }

    public ProbeUpdateCommandState? State(string probeId)
    {
        lock (_states)
        {
            if (!_states.TryGetValue(probeId, out var state))
            {
                return null;
            }

            return state.Waiting && Expired(state)
                ? state with { Accepted = false, Message = "the probe did not take the command", AnsweredUtc = state.QueuedUtc + AnswerTimeout }
                : state;
        }
    }

    /// <summary>The probe took the command a moment ago and does not run the target version yet.</summary>
    public bool IsUpdating(string probeId, string? reportedVersion)
    {
        var state = State(probeId);
        return state is { Accepted: true, AnsweredUtc: { } answered }
            && _time.GetUtcNow() - answered < UpdatingWindow
            && !string.Equals(reportedVersion, state.Command.Version, StringComparison.OrdinalIgnoreCase);
    }

    private bool Expired(ProbeUpdateCommandState state) => _time.GetUtcNow() - state.QueuedUtc >= AnswerTimeout;
}
