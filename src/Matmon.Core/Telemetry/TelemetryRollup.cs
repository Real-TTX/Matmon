using Matmon.Core.Domain;

namespace Matmon.Core.Telemetry;

/// <summary>
/// Pure downsampling logic: turns the raw observations that fall inside a time
/// window into one <see cref="SensorStatisticsBucket"/> with accurate
/// percentiles and a state distribution. Kept free of storage/IO so it can be
/// unit tested and reused by the rollup service.
/// </summary>
public static class TelemetryRollup
{
    /// <summary>The "bottom 1%" / "top 1%" the UI surfaces by default.</summary>
    public const double DefaultLowPercentile = 0.01;
    public const double DefaultHighPercentile = 0.99;

    /// <summary>
    /// Aggregates every observation in <paramref name="observations"/> (any order)
    /// into a single bucket. Numeric aggregates (avg/min/max/percentiles) only
    /// consider observations that carry a numeric default-channel value; the
    /// state distribution counts every observation. Returns null when the window
    /// is empty.
    /// </summary>
    public static SensorStatisticsBucket? Aggregate(
        Guid sensorId,
        IReadOnlyList<SensorObservation> observations,
        DateTimeOffset bucketStartUtc,
        int bucketMinutes,
        double lowPercentile = DefaultLowPercentile,
        double highPercentile = DefaultHighPercentile)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0)
        {
            return null;
        }

        var values = new List<double>(observations.Count);
        string channelKey = string.Empty;
        string? unit = null;
        int healthy = 0, warning = 0, critical = 0;

        SensorObservation? latest = null;
        foreach (var observation in observations)
        {
            if (latest is null || observation.TimestampUtc >= latest.TimestampUtc)
            {
                latest = observation;
            }

            switch (observation.State)
            {
                case SensorState.Healthy:
                    healthy++;
                    break;
                case SensorState.Warning:
                    warning++;
                    break;
                case SensorState.Critical:
                    critical++;
                    break;
            }

            if (TryResolveSample(observation, out var sample, out var sampleKey, out var sampleUnit))
            {
                values.Add(sample);
                channelKey = sampleKey;
                unit ??= sampleUnit;
            }
        }

        double? lastValue = null;
        if (latest is not null && TryResolveSample(latest, out var latestSample, out _, out _))
        {
            lastValue = latestSample;
        }

        var bucket = new SensorStatisticsBucket
        {
            SensorId = sensorId,
            BucketStartUtc = bucketStartUtc,
            BucketMinutes = bucketMinutes,
            DefaultChannelKey = channelKey,
            Unit = unit,
            State = latest?.State ?? SensorState.Unknown,
            Message = latest?.Message,
            SampleCount = values.Count,
            LastValue = lastValue,
            HealthyCount = healthy,
            WarningCount = warning,
            CriticalCount = critical
        };

        if (values.Count > 0)
        {
            values.Sort();
            bucket.Minimum = values[0];
            bucket.Maximum = values[^1];
            bucket.Average = values.Average();
            bucket.LowPercentile = Percentile(values, lowPercentile);
            bucket.HighPercentile = Percentile(values, highPercentile);
        }

        return bucket;
    }

    /// <summary>
    /// Aggregates the observations into one bucket <em>per numeric channel</em>
    /// (skipping virtual channels), so each channel gets its own long-term
    /// statistics series. The state distribution per channel uses the channel's
    /// own state when it carries one, else the observation's overall state.
    /// Observations that only carry a bare <see cref="SensorObservation.Value"/>
    /// (no channels) fold into a single default-keyed bucket. Returns an empty
    /// list when nothing numeric is present.
    /// </summary>
    public static IReadOnlyList<SensorStatisticsBucket> AggregatePerChannel(
        Guid sensorId,
        IReadOnlyList<SensorObservation> observations,
        DateTimeOffset bucketStartUtc,
        int bucketMinutes,
        double lowPercentile = DefaultLowPercentile,
        double highPercentile = DefaultHighPercentile)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0)
        {
            return [];
        }

        var accumulators = new Dictionary<string, ChannelAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var observation in observations)
        {
            foreach (var sample in EnumerateChannelSamples(observation))
            {
                if (!accumulators.TryGetValue(sample.Key, out var accumulator))
                {
                    accumulator = new ChannelAccumulator(sample.Key);
                    accumulators[sample.Key] = accumulator;
                }

                accumulator.Add(observation.TimestampUtc, sample.Value, sample.Unit, sample.State, observation.Message);
            }
        }

        if (accumulators.Count == 0)
        {
            return [];
        }

        var buckets = new List<SensorStatisticsBucket>(accumulators.Count);
        foreach (var accumulator in accumulators.Values)
        {
            buckets.Add(accumulator.Build(sensorId, bucketStartUtc, bucketMinutes, lowPercentile, highPercentile));
        }

        return buckets;
    }

    // Yields every numeric, non-virtual channel sample of an observation. Falls
    // back to the bare observation value (keyed by the default channel) when the
    // observation carries no channels — mirroring TryResolveSample.
    private static IEnumerable<(string Key, double Value, string? Unit, SensorState State)> EnumerateChannelSamples(SensorObservation observation)
    {
        var yielded = false;
        foreach (var channel in observation.Channels)
        {
            if (channel.IsVirtual || channel.Value is not double value)
            {
                continue;
            }

            var key = string.IsNullOrWhiteSpace(channel.Key) ? "default" : channel.Key;
            yielded = true;
            yield return (key, value, channel.Unit, channel.State ?? observation.State);
        }

        if (!yielded && observation.Value is double bareValue)
        {
            var key = string.IsNullOrWhiteSpace(observation.DefaultChannelKey) ? "default" : observation.DefaultChannelKey;
            yield return (key, bareValue, null, observation.State);
        }
    }

    // Accumulates one channel's samples within a bucket window.
    private sealed class ChannelAccumulator(string channelKey)
    {
        private readonly List<double> _values = [];
        private string? _unit;
        private int _healthy, _warning, _critical;
        private DateTimeOffset _latestTs = DateTimeOffset.MinValue;
        private double? _latestValue;
        private SensorState _latestState = SensorState.Unknown;
        private string? _latestMessage;

        public void Add(DateTimeOffset timestampUtc, double value, string? unit, SensorState state, string? message)
        {
            _values.Add(value);
            _unit ??= unit;

            switch (state)
            {
                case SensorState.Healthy:
                    _healthy++;
                    break;
                case SensorState.Warning:
                    _warning++;
                    break;
                case SensorState.Critical:
                    _critical++;
                    break;
            }

            if (timestampUtc >= _latestTs)
            {
                _latestTs = timestampUtc;
                _latestValue = value;
                _latestState = state;
                _latestMessage = message;
            }
        }

        public SensorStatisticsBucket Build(
            Guid sensorId,
            DateTimeOffset bucketStartUtc,
            int bucketMinutes,
            double lowPercentile,
            double highPercentile)
        {
            var bucket = new SensorStatisticsBucket
            {
                SensorId = sensorId,
                BucketStartUtc = bucketStartUtc,
                BucketMinutes = bucketMinutes,
                DefaultChannelKey = channelKey,
                Unit = _unit,
                State = _latestState,
                Message = _latestMessage,
                SampleCount = _values.Count,
                LastValue = _latestValue,
                HealthyCount = _healthy,
                WarningCount = _warning,
                CriticalCount = _critical
            };

            if (_values.Count > 0)
            {
                _values.Sort();
                bucket.Minimum = _values[0];
                bucket.Maximum = _values[^1];
                bucket.Average = _values.Average();
                bucket.LowPercentile = Percentile(_values, lowPercentile);
                bucket.HighPercentile = Percentile(_values, highPercentile);
            }

            return bucket;
        }
    }

    /// <summary>
    /// Linear-interpolation percentile over an ascending-sorted sample.
    /// <paramref name="p"/> is a fraction in [0, 1].
    /// </summary>
    public static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        ArgumentNullException.ThrowIfNull(sortedAscending);
        if (sortedAscending.Count == 0)
        {
            throw new ArgumentException("Sample must not be empty.", nameof(sortedAscending));
        }

        if (sortedAscending.Count == 1)
        {
            return sortedAscending[0];
        }

        p = Math.Clamp(p, 0d, 1d);
        var rank = p * (sortedAscending.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi)
        {
            return sortedAscending[lo];
        }

        var fraction = rank - lo;
        return sortedAscending[lo] + ((sortedAscending[hi] - sortedAscending[lo]) * fraction);
    }

    /// <summary>
    /// Resolves the numeric default-channel value of an observation, mirroring
    /// how the live graph picks a sensor's primary series.
    /// </summary>
    public static bool TryResolveSample(SensorObservation observation, out double value, out string channelKey, out string? unit)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var defaultChannel = observation.Channels.FirstOrDefault(channel =>
            channel.IsDefault ||
            (!string.IsNullOrWhiteSpace(observation.DefaultChannelKey) &&
             string.Equals(channel.Key, observation.DefaultChannelKey, StringComparison.OrdinalIgnoreCase)));

        if (defaultChannel is null && observation.Channels.Count > 0)
        {
            defaultChannel = observation.Channels[0];
        }

        if (defaultChannel?.Value is double channelValue)
        {
            value = channelValue;
            channelKey = string.IsNullOrWhiteSpace(defaultChannel.Key)
                ? observation.DefaultChannelKey ?? "default"
                : defaultChannel.Key;
            unit = defaultChannel.Unit;
            return true;
        }

        if (observation.Value is double observationValue)
        {
            value = observationValue;
            channelKey = string.IsNullOrWhiteSpace(observation.DefaultChannelKey) ? "default" : observation.DefaultChannelKey;
            unit = defaultChannel?.Unit;
            return true;
        }

        value = default;
        channelKey = string.Empty;
        unit = null;
        return false;
    }
}
