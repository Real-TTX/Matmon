using System.Globalization;
using System.Security.Cryptography;

namespace Matmon.Core.Domain;

/// <summary>
/// The push sensor ("heartbeat"): instead of Matmon asking, something calls Matmon - a backup script, a cron job, a device
/// that can send an HTTP request - at its own address <c>/api/push/&lt;token&gt;</c>, optionally with a status, a value and
/// a message. Silence is the alarm: no push for longer than expected (+ grace) is Critical. Ideal for backups, where the
/// interesting failure is the job that never ran.
///
/// The token is the sensor's own parameter (<see cref="TokenParameter"/>): its name ends in "token", so it is encrypted at
/// rest like every secret parameter. Clearing it and saving gives a new one - the old address stops working.
///
/// Pure - the host keeps the last push (<c>PushRegistry</c>) and records each push as an observation; the periodic check
/// only works out whether it is overdue.
/// </summary>
public static class PushSensor
{
    public const string Key = "push";
    public const string TokenParameter = "push.token";
    public const string IntervalParameter = "push.interval";
    public const string GraceParameter = "push.grace";
    public const int DefaultIntervalMinutes = 60;
    public const int DefaultGraceMinutes = 15;

    /// <summary>The longest message a push may leave (the rest is cut).</summary>
    public const int MaxMessageLength = 500;

    /// <summary>The virtual channel that carries the last push from one observation to the next (its time as Unix seconds,
    /// its state and message), so it survives a restart.</summary>
    public const string PushedAtChannel = "pushedAt";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = Key,
        DisplayName = "Push (heartbeat)",
        Description = "Something calls Matmon instead - a backup script, a cron job, a device - at this sensor's own address, optionally with a status, a value and a message. No call for longer than expected is Critical.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = IntervalParameter,
                Label = "Expected every (minutes)",
                Kind = SensorParameterKind.Integer,
                Description = "How often the job calls. A daily backup: 1440.",
                DefaultValue = DefaultIntervalMinutes.ToString(CultureInfo.InvariantCulture),
                Min = 1,
                Max = 44_640,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = GraceParameter,
                Label = "Grace (minutes)",
                Kind = SensorParameterKind.Integer,
                Description = "How much later than expected a call may come before the sensor turns Critical - a backup does not always take the same time.",
                DefaultValue = DefaultGraceMinutes.ToString(CultureInfo.InvariantCulture),
                Min = 0,
                Max = 44_640,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = TokenParameter,
                Label = "Token",
                Kind = SensorParameterKind.Text,
                Description = "The secret part of the push address (shown on the sensor's page). Empty it and save for a new one - the old address stops working."
            }
        ]
    };

    /// <summary>One push, as it arrived.</summary>
    public sealed record Push(DateTimeOffset ReceivedUtc, SensorState State, double? Value, string? Message);

    /// <summary>A new token: 24 random bytes, URL-safe.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Gives a push sensor a token when it has none (created, or emptied to get a new one). True when it did.</summary>
    public static bool EnsureToken(string? sensorTypeKey, MonitoringSettings settings)
    {
        if (!string.Equals(sensorTypeKey, Key, StringComparison.OrdinalIgnoreCase)
            || (settings.Parameters.TryGetValue(TokenParameter, out var token) && !string.IsNullOrWhiteSpace(token)))
        {
            return false;
        }

        settings.Parameters[TokenParameter] = NewToken();
        return true;
    }

    /// <summary>The status a push names: ok / up / success / 0 = Healthy, warning / warn / 1 = Warning, error / critical /
    /// down / fail / 2 = Critical; none = Healthy (the call itself is the good news). Null for a word that means nothing.</summary>
    public static SensorState? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "ok" or "up" or "healthy" or "success" or "succeeded" or "0" => SensorState.Healthy,
        "warning" or "warn" or "1" => SensorState.Warning,
        "error" or "critical" or "down" or "fail" or "failed" or "failure" or "2" => SensorState.Critical,
        _ => null
    };

    public static string? CleanMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var oneLine = string.Join(' ', message.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return oneLine.Length <= MaxMessageLength ? oneLine : oneLine[..MaxMessageLength];
    }

    /// <summary>The sensor's result at <paramref name="now"/>, given the last push (null = none yet).</summary>
    public static SensorExecutionResult Evaluate(Push? last, MonitoringSettings settings, DateTimeOffset now)
    {
        if (last is null)
        {
            return SensorExecutionResult.Unknown("Waiting for the first push.");
        }

        var interval = TimeSpan.FromMinutes(ReadMinutes(settings, IntervalParameter, DefaultIntervalMinutes, minimum: 1));
        var grace = TimeSpan.FromMinutes(ReadMinutes(settings, GraceParameter, DefaultGraceMinutes, minimum: 0));
        var age = now - last.ReceivedUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        var overdue = age > interval + grace;
        var state = overdue ? SensorState.Critical : last.State;
        var message = overdue
            ? $"No push for {FormatAge(age)} - expected every {FormatAge(interval)}."
            : $"Last push {FormatAge(age)} ago" + (last.Message is { } said ? $": {said}" : ".");
        return Build(state, message, last, age);
    }

    /// <summary>The result recorded the moment a push arrives.</summary>
    public static SensorExecutionResult FromPush(Push push) =>
        Build(push.State, push.Message is { } said ? $"Pushed: {said}" : "Pushed.", push, TimeSpan.Zero);

    /// <summary>The last push carried by an observation (its virtual channel), or null.</summary>
    public static Push? FromObservation(SensorObservation? observation)
    {
        var channel = observation?.Channels.FirstOrDefault(candidate => candidate.Key == PushedAtChannel);
        if (channel?.Value is not { } epoch)
        {
            return null;
        }

        var value = observation!.Channels.FirstOrDefault(candidate => candidate.Key == "value")?.Value;
        return new Push(DateTimeOffset.FromUnixTimeSeconds((long)epoch), channel.State ?? SensorState.Healthy, value, channel.Message);
    }

    private static SensorExecutionResult Build(SensorState state, string message, Push push, TimeSpan age)
    {
        var channels = new List<SensorChannelValue>
        {
            new()
            {
                Key = "sinceLastPush",
                Label = "Since last push",
                Value = Math.Round(age.TotalSeconds),
                Unit = "s",
                MeasurementKind = SensorMeasurementKind.Duration,
                IsDefault = true
            }
        };
        if (push.Value is { } value)
        {
            channels.Add(new SensorChannelValue { Key = "value", Label = "Value", Value = value });
        }

        channels.Add(new SensorChannelValue
        {
            Key = PushedAtChannel,
            Label = "Last push",
            Value = push.ReceivedUtc.ToUnixTimeSeconds(),
            State = push.State,
            Message = push.Message,
            IsVirtual = true,
            LogByDefault = false
        });

        return new SensorExecutionResult(state, TimeSpan.Zero, Math.Round(age.TotalSeconds), message)
        {
            DefaultChannelKey = "sinceLastPush",
            Channels = channels
        };
    }

    private static int ReadMinutes(MonitoringSettings settings, string key, int fallback, int minimum) =>
        MonitoringSettings.TryReadParameterInt(settings, key, out var minutes) && minutes >= minimum ? minutes : fallback;

    public static string FormatAge(TimeSpan age) => age.TotalMinutes switch
    {
        < 1 => $"{Math.Max(0, (int)age.TotalSeconds)} s",
        < 120 => $"{(int)age.TotalMinutes} min",
        < 48 * 60 => $"{age.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h",
        _ => $"{age.TotalDays.ToString("0.#", CultureInfo.InvariantCulture)} days"
    };
}
