using System.Text.RegularExpressions;
using Matmon.Core.Domain;

namespace Matmon.Core.Telemetry;

/// <summary>What happened to a sensor, one line each: its state changed, an alert was raised / re-opened / recovered /
/// escalated / acknowledged / resolved, alerts were muted / un-muted, the sensor was paused / resumed.</summary>
public enum SensorStatusKind
{
    StateChanged,
    AlertRaised,
    AlertReopened,
    AlertRecovered,
    AlertEscalated,
    AlertAcknowledged,
    AlertResolved,
    AlertMuted,
    AlertUnmuted,
    Paused,
    Resumed
}

/// <summary>The four groups the status history can be filtered by.</summary>
public enum SensorStatusCategory
{
    States,
    Alerts,
    Acknowledgements,
    Pause
}

/// <summary>One line of a sensor's status history.</summary>
/// <param name="FromState">The state before (a state change, a pause); null when not known.</param>
/// <param name="ToState">The state after (a state change), or the alert's state (raise / re-open / acknowledge).</param>
/// <param name="Actor">The user who did it, when recorded.</param>
/// <param name="Duration">A state change: how long the new state held. A resume: how long it was paused. A recovery or
/// resolve: how long the alert had been open. A pause still in effect: how long so far.</param>
/// <param name="Ongoing">The duration runs on (the current state, a pause not yet ended).</param>
/// <param name="ProblemDuration">A state change back to OK: how long the sensor had been in Warning / Error before.</param>
public sealed record SensorStatusEntry(
    DateTimeOffset TimestampUtc,
    SensorStatusKind Kind,
    SensorStatusCategory Category,
    SensorState? FromState,
    SensorState? ToState,
    string? Actor,
    string Message,
    TimeSpan? Duration = null,
    bool Ongoing = false,
    TimeSpan? ProblemDuration = null);

/// <summary>
/// Builds a sensor's status history from what is recorded: its monitoring events (state changes, alerts, mutes, pauses)
/// and its alert records (raised / recovered / acknowledged / resolved times and the escalations sent - kept on the alert,
/// so they fill in what no event says: recoveries from before the recovery event existed and every escalation). Pure, so it
/// is tested without a store. The newest line comes first.
/// </summary>
public static class SensorStatusHistory
{
    /// <summary>Two records of the same thing closer than this are one (an alert record and its event; two paths
    /// resolving the same alert).</summary>
    public static readonly TimeSpan SameMoment = TimeSpan.FromSeconds(5);

    public static SensorStatusCategory CategoryOf(SensorStatusKind kind) => kind switch
    {
        SensorStatusKind.StateChanged => SensorStatusCategory.States,
        SensorStatusKind.Paused or SensorStatusKind.Resumed => SensorStatusCategory.Pause,
        SensorStatusKind.AlertAcknowledged or SensorStatusKind.AlertMuted or SensorStatusKind.AlertUnmuted => SensorStatusCategory.Acknowledgements,
        _ => SensorStatusCategory.Alerts
    };

    public static IReadOnlyList<SensorStatusEntry> Build(
        IEnumerable<MonitoringEvent> events,
        IEnumerable<MonitoringAlert> alerts,
        DateTimeOffset nowUtc)
    {
        var raw = new List<SensorStatusEntry>();
        foreach (var monitoringEvent in events)
        {
            if (FromEvent(monitoringEvent) is { } entry)
            {
                raw.Add(entry);
            }
        }

        foreach (var alert in alerts)
        {
            foreach (var entry in FromAlert(alert))
            {
                // An event already says it (the alert's own times are the event's times) - the record only fills gaps.
                var known = entry.Kind != SensorStatusKind.AlertEscalated && raw.Any(existing =>
                    existing.Kind == entry.Kind && (existing.TimestampUtc - entry.TimestampUtc).Duration() <= SameMoment);
                if (!known)
                {
                    raw.Add(entry);
                }
            }
        }

        var ordered = raw
            .OrderBy(entry => entry.TimestampUtc)
            .ThenBy(entry => Order(entry.Kind))
            .ToList();

        // Consecutive duplicates: a state change to the state it already had (a restart, a restore), or the same alert
        // step recorded twice within moments (the dashboard and the poll path both resolving it).
        var merged = new List<SensorStatusEntry>(ordered.Count);
        SensorState? lastState = null;
        foreach (var entry in ordered)
        {
            if (entry.Kind == SensorStatusKind.StateChanged)
            {
                if (lastState is not null && entry.ToState == lastState)
                {
                    continue;
                }

                lastState = entry.ToState;
                merged.Add(entry);
                continue;
            }

            var previousSame = merged.LastOrDefault(existing => existing.Kind == entry.Kind);
            if (previousSame is not null
                && entry.TimestampUtc - previousSame.TimestampUtc <= SameMoment
                && !merged.Skip(merged.LastIndexOf(previousSame) + 1).Any(between => Breaks(between.Kind, entry.Kind)))
            {
                continue;
            }

            merged.Add(entry);
        }

        return WithStatesAndDurations(merged, nowUtc);
    }

    // A pause between two pauses (a resume) makes the second a pause of its own, however quick.
    private static bool Breaks(SensorStatusKind between, SensorStatusKind kind) => (kind, between) switch
    {
        (SensorStatusKind.Paused, SensorStatusKind.Resumed) or (SensorStatusKind.Resumed, SensorStatusKind.Paused) => true,
        (SensorStatusKind.AlertRaised, SensorStatusKind.AlertResolved) or (SensorStatusKind.AlertResolved, SensorStatusKind.AlertRaised) => true,
        (SensorStatusKind.AlertMuted, SensorStatusKind.AlertUnmuted) or (SensorStatusKind.AlertUnmuted, SensorStatusKind.AlertMuted) => true,
        _ => false
    };

    private static IReadOnlyList<SensorStatusEntry> WithStatesAndDurations(List<SensorStatusEntry> entries, DateTimeOffset nowUtc)
    {
        var result = new SensorStatusEntry[entries.Count];
        SensorState? observed = null;      // the last state a check produced
        DateTimeOffset? problemSince = null; // when the current Warning / Error streak began
        DateTimeOffset? pausedSince = null;
        DateTimeOffset? alertSince = null;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            switch (entry.Kind)
            {
                case SensorStatusKind.StateChanged:
                {
                    var from = observed;
                    var to = entry.ToState;
                    TimeSpan? problem = null;
                    if (to == SensorState.Healthy && problemSince is { } since)
                    {
                        problem = entry.TimestampUtc - since;
                    }

                    if (IsProblem(to))
                    {
                        problemSince ??= entry.TimestampUtc;
                    }
                    else if (to is not null)
                    {
                        // Unknown (held, no data) neither ends nor starts a streak; anything else ends it.
                        if (to != SensorState.Unknown)
                        {
                            problemSince = null;
                        }
                    }

                    // How long the state held: until the next state change or pause.
                    var end = entries.Skip(i + 1)
                        .FirstOrDefault(next => next.Kind is SensorStatusKind.StateChanged or SensorStatusKind.Paused)?.TimestampUtc;
                    result[i] = entry with
                    {
                        FromState = from,
                        Duration = (end ?? nowUtc) - entry.TimestampUtc,
                        Ongoing = end is null,
                        ProblemDuration = problem
                    };
                    observed = to;
                    break;
                }

                case SensorStatusKind.Paused:
                {
                    pausedSince = entry.TimestampUtc;
                    var resumed = entries.Skip(i + 1).Any(next => next.Kind == SensorStatusKind.Resumed);
                    result[i] = entry with
                    {
                        FromState = observed,
                        ToState = SensorState.Paused,
                        Duration = resumed ? null : nowUtc - entry.TimestampUtc,
                        Ongoing = !resumed
                    };
                    break;
                }

                case SensorStatusKind.Resumed:
                    result[i] = entry with
                    {
                        FromState = SensorState.Paused,
                        ToState = null,
                        Duration = pausedSince is { } paused ? entry.TimestampUtc - paused : null
                    };
                    pausedSince = null;
                    break;

                case SensorStatusKind.AlertRaised:
                    alertSince = entry.TimestampUtc;
                    result[i] = entry;
                    break;

                case SensorStatusKind.AlertRecovered:
                    result[i] = entry with { Duration = alertSince is { } openedR ? entry.TimestampUtc - openedR : null };
                    break;

                case SensorStatusKind.AlertResolved:
                    result[i] = entry with { Duration = alertSince is { } opened ? entry.TimestampUtc - opened : null };
                    alertSince = null;
                    break;

                default:
                    result[i] = entry;
                    break;
            }
        }

        Array.Reverse(result);
        return result;
    }

    private static bool IsProblem(SensorState? state) => state is SensorState.Warning or SensorState.Critical;

    // Same moment: the state change first, then what the monitor made of it, then what a person did.
    private static int Order(SensorStatusKind kind) => kind switch
    {
        SensorStatusKind.StateChanged => 0,
        SensorStatusKind.AlertRecovered => 1,
        SensorStatusKind.AlertRaised => 2,
        SensorStatusKind.AlertReopened => 2,
        SensorStatusKind.AlertEscalated => 3,
        SensorStatusKind.AlertAcknowledged => 4,
        SensorStatusKind.Paused => 5, // a pause resolves the alert: the pause comes first
        SensorStatusKind.AlertMuted => 6,
        SensorStatusKind.AlertResolved => 7,
        SensorStatusKind.AlertUnmuted => 8,
        _ => 9
    };

    private static SensorStatusEntry? FromEvent(MonitoringEvent monitoringEvent)
    {
        SensorStatusKind? kind = monitoringEvent.Kind switch
        {
            MonitoringEventKind.StateChanged => SensorStatusKind.StateChanged,
            MonitoringEventKind.AlertRaised => SensorStatusKind.AlertRaised,
            MonitoringEventKind.AlertReopened => SensorStatusKind.AlertReopened,
            MonitoringEventKind.AlertRecovered => SensorStatusKind.AlertRecovered,
            MonitoringEventKind.AlertAcknowledged => SensorStatusKind.AlertAcknowledged,
            MonitoringEventKind.AlertResolved => SensorStatusKind.AlertResolved,
            MonitoringEventKind.AlertMuted => SensorStatusKind.AlertMuted,
            MonitoringEventKind.AlertUnmuted => SensorStatusKind.AlertUnmuted,
            MonitoringEventKind.Paused => SensorStatusKind.Paused,
            MonitoringEventKind.Resumed => SensorStatusKind.Resumed,
            _ => null
        };
        if (kind is null)
        {
            return null;
        }

        var message = monitoringEvent.Message ?? string.Empty;
        var actor = string.IsNullOrWhiteSpace(monitoringEvent.Actor) ? null : monitoringEvent.Actor.Trim();

        // Who did it: the event's own field, else (older events) the " by <user>" the message carried.
        if (kind is SensorStatusKind.AlertAcknowledged or SensorStatusKind.AlertMuted or SensorStatusKind.AlertUnmuted
            or SensorStatusKind.Paused or SensorStatusKind.Resumed)
        {
            var by = ByPattern.Match(message);
            if (by.Success)
            {
                actor ??= by.Groups["who"].Value.Trim();
                message = message[..by.Index];
            }

            message = kind switch
            {
                SensorStatusKind.AlertMuted => message.StartsWith("Alerts muted", StringComparison.OrdinalIgnoreCase)
                    ? message["Alerts muted".Length..].Trim()
                    : message,
                // The kind says it all.
                _ => string.Empty
            };
        }

        SensorState? toState = monitoringEvent.State;
        if (kind == SensorStatusKind.StateChanged)
        {
            message = StripTransition(message);
        }
        else if (kind is SensorStatusKind.Resumed or SensorStatusKind.AlertMuted or SensorStatusKind.AlertUnmuted
                 or SensorStatusKind.AlertRecovered or SensorStatusKind.AlertResolved)
        {
            // A resume stores "Healthy" and a resolve the state the alert last had - neither is a state the sensor went to.
            toState = null;
        }

        return new SensorStatusEntry(
            monitoringEvent.TimestampUtc,
            kind.Value,
            CategoryOf(kind.Value),
            null,
            toState,
            actor,
            message.Trim());
    }

    private static IEnumerable<SensorStatusEntry> FromAlert(MonitoringAlert alert)
    {
        yield return Entry(alert.FirstSeenUtc, SensorStatusKind.AlertRaised, alert.State, null, alert.Message);
        if (alert.RecoveredUtc is { } recovered && (alert.ResolvedUtc is null || recovered < alert.ResolvedUtc - SameMoment))
        {
            yield return Entry(recovered, SensorStatusKind.AlertRecovered, null, null, "Condition cleared");
        }

        foreach (var mark in alert.Escalations.OrderBy(mark => mark.SentUtc))
        {
            yield return Entry(mark.SentUtc, SensorStatusKind.AlertEscalated, null, null, $"Escalation step {mark.Step + 1} sent");
        }

        if (alert.AcknowledgedUtc is { } acknowledged)
        {
            yield return Entry(acknowledged, SensorStatusKind.AlertAcknowledged, alert.AcknowledgedState, alert.AcknowledgedBy, string.Empty);
        }

        if (alert.ResolvedUtc is { } resolved)
        {
            yield return Entry(resolved, SensorStatusKind.AlertResolved, null, null, alert.Message);
        }
    }

    private static SensorStatusEntry Entry(DateTimeOffset at, SensorStatusKind kind, SensorState? to, string? actor, string message)
        => new(at, kind, CategoryOf(kind), null, to, string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(), message ?? string.Empty);

    private static readonly Regex ByPattern = new(@"\s+by\s+(?<who>[^\s].*)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly string LabelAlternation = string.Join("|",
        Enum.GetValues<SensorState>().Select(MonitoringStatePresentation.Label).Distinct().Select(Regex.Escape));

    private static readonly Regex TransitionPattern = new(
        $@"^(?:state (?:{LabelAlternation})|(?:{LabelAlternation}) -> (?:{LabelAlternation}))(?::\s*)?(?<rest>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline,
        TimeSpan.FromMilliseconds(100));

    /// <summary>A state-change event's message starts with "OK -> Error: " (or "state OK") - the history shows the two
    /// states as pills, so only what the check said (and " via probe") is left.</summary>
    public static string StripTransition(string message)
    {
        var match = TransitionPattern.Match(message ?? string.Empty);
        return match.Success ? match.Groups["rest"].Value.Trim() : (message ?? string.Empty).Trim();
    }
}
