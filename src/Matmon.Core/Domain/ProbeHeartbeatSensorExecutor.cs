using System.Diagnostics;

namespace Matmon.Core.Domain;

public sealed class ProbeHeartbeatSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "probe-heartbeat",
        DisplayName = "Probe Heartbeat",
        Description = "Monitors the age of the last heartbeat received from a secondary probe.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters = []
    };

    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(3);
    private static readonly DateTimeOffset ProcessStartedUtc = ReadProcessStart();

    private readonly IProbeHeartbeatLookup _probeHeartbeatLookup;

    private static DateTimeOffset ReadProcessStart()
    {
        try
        {
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    public ProbeHeartbeatSensorExecutor(IProbeHeartbeatLookup probeHeartbeatLookup)
    {
        _probeHeartbeatLookup = probeHeartbeatLookup;
    }

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "probe id is required"));
        }

        var watch = Stopwatch.StartNew();
        try
        {
            if (!_probeHeartbeatLookup.TryGetLastHeartbeat(context.Target, out var lastSeenUtc, out var probeName))
            {
                watch.Stop();
                var missingChannel = new SensorChannelValue
                {
                    Key = "ageSeconds",
                    Label = "Age",
                    Value = null,
                    Unit = "s",
                    IsDefault = true
                };

                // The heartbeat registry lives in memory: right after the primary restarted it knows no probe yet, and
                // a heartbeat sensor polled before the probe's next beat used to go Critical - an alert and a
                // notification after every primary update, then a "Recovered" one. For the first minutes that is
                // "no data yet", not a dead probe.
                if (DateTimeOffset.UtcNow - ProcessStartedUtc < StartupGrace)
                {
                    return ValueTask.FromResult(new SensorExecutionResult(
                        SensorState.Unknown,
                        watch.Elapsed,
                        null,
                        $"waiting for the first heartbeat of probe '{context.Target}' since this primary started")
                    {
                        DefaultChannelKey = missingChannel.Key,
                        Channels = [missingChannel]
                    });
                }

                return ValueTask.FromResult(
                    SensorExecutionResult.Critical(
                        watch.Elapsed,
                        $"probe '{context.Target}' has not sent a heartbeat yet",
                        null,
                        missingChannel.Key,
                        [missingChannel]));
            }

            var ageSeconds = Math.Max((DateTimeOffset.UtcNow - lastSeenUtc).TotalSeconds, 0);
            var ageChannel = new SensorChannelValue
            {
                Key = "ageSeconds",
                Label = "Age",
                Value = ageSeconds,
                Unit = "s",
                IsDefault = true
            };

            watch.Stop();

            var message = string.IsNullOrWhiteSpace(probeName)
                ? $"heartbeat {ageSeconds:0.#}s ago"
                : $"{probeName} heartbeat {ageSeconds:0.#}s ago";

            var result = SensorExecutionResult.Healthy(
                watch.Elapsed,
                message,
                ageSeconds,
                ageChannel.Key,
                [ageChannel]);

            return ValueTask.FromResult(SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result));
        }
        catch (Exception ex)
        {
            watch.Stop();
            return ValueTask.FromResult(SensorExecutionResult.Critical(watch.Elapsed, ex.Message));
        }
    }
}
