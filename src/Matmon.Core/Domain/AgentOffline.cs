using System.Globalization;

namespace Matmon.Core.Domain;

/// <summary>What the primary knows about an agent host that may go offline (a notebook): the option and its warning.</summary>
public interface IAgentOfflinePolicy
{
    /// <summary>True when <paramref name="probeId"/> is an agent whose host may go offline.</summary>
    bool TryGet(string probeId, out int warnDays);
}

/// <summary>
/// The rules for an agent host that may go offline - a notebook that is shut, asleep or on a train. Its agent is the one
/// that measures it, so while the machine is away nothing can be measured and nothing is wrong: the connection sensor shows
/// "offline" (Paused - no alert, an alert it had recovers) instead of an error, and warns only once it has been away longer
/// than the host's limit (default 14 days - a notebook that is gone). Offline means: the agent said it is going away
/// (shutdown, sleep, the service stopping) and has not beaten since, or its last heartbeat is older than
/// <see cref="SilenceAfter"/> (sleep without a word, no network), or this primary has not heard from it since it started.
/// </summary>
public static class AgentOffline
{
    public const int DefaultWarnDays = 14;

    /// <summary>An agent beats every 30 s; three missed beats = away.</summary>
    public static readonly TimeSpan SilenceAfter = TimeSpan.FromSeconds(95);

    /// <summary>The moment the host went offline, or null while it is online. <paramref name="lastSeenUtc"/> null = no
    /// heartbeat since this primary started (then the moment is unknown: <see cref="DateTimeOffset.MinValue"/>).</summary>
    public static DateTimeOffset? Since(DateTimeOffset? lastSeenUtc, DateTimeOffset? goingOfflineUtc, DateTimeOffset now)
    {
        if (lastSeenUtc is not DateTimeOffset seen)
        {
            return goingOfflineUtc ?? DateTimeOffset.MinValue;
        }

        if (goingOfflineUtc is DateTimeOffset notice && notice >= seen)
        {
            return notice;
        }

        return now - seen > SilenceAfter ? seen : null;
    }

    /// <summary>The connection sensor's result for an offline host: Paused with the time it has been away, or a Warning
    /// once that passes <paramref name="warnDays"/> (0 = never).</summary>
    public static SensorExecutionResult Result(DateTimeOffset since, int warnDays, string? name, double? ageSeconds, DateTimeOffset now)
    {
        var channel = new SensorChannelValue { Key = "ageSeconds", Label = "Age", Value = ageSeconds, Unit = "s", IsDefault = true };
        var who = string.IsNullOrWhiteSpace(name) ? "the device" : name;
        if (since == DateTimeOffset.MinValue)
        {
            return new SensorExecutionResult(SensorState.Paused, TimeSpan.Zero, null,
                $"{who} is offline - not heard from since this instance started (allowed for this host)")
            {
                DefaultChannelKey = channel.Key,
                Channels = [channel]
            };
        }

        var away = now - since;
        if (warnDays > 0 && away > TimeSpan.FromDays(warnDays))
        {
            return SensorExecutionResult.Warning(TimeSpan.Zero,
                $"{who} has been offline for {Describe(away)} - longer than the {warnDays} days allowed for this host",
                ageSeconds, channel.Key, [channel]);
        }

        return new SensorExecutionResult(SensorState.Paused, TimeSpan.Zero, ageSeconds,
            $"{who} is offline for {Describe(away)} (allowed for this host)")
        {
            DefaultChannelKey = channel.Key,
            Channels = [channel]
        };
    }

    public static string Describe(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "less than a minute",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} h {span.Minutes} min",
        { TotalDays: < 2 } => $"1 day {span.Hours} h",
        _ => $"{((int)span.TotalDays).ToString(CultureInfo.InvariantCulture)} days"
    };
}
