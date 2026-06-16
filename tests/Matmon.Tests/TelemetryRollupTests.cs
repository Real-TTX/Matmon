using Matmon.Core.Domain;
using Matmon.Core.Telemetry;

namespace Matmon.Tests;

public sealed class TelemetryRollupTests
{
    private static readonly Guid Sensor = new("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset BucketStart = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static SensorObservation Numeric(int minuteOffset, SensorState state, double value)
        => new()
        {
            SensorId = Sensor,
            TimestampUtc = BucketStart.AddMinutes(minuteOffset),
            State = state,
            DefaultChannelKey = "latency",
            Channels = [new() { Key = "latency", Label = "Latency", Value = value, Unit = "ms", IsDefault = true }]
        };

    private static SensorObservation NoValue(int minuteOffset, SensorState state)
        => new()
        {
            SensorId = Sensor,
            TimestampUtc = BucketStart.AddMinutes(minuteOffset),
            State = state,
            DefaultChannelKey = "latency",
            Channels = [new() { Key = "latency", Label = "Latency", Value = null, Unit = "ms", IsDefault = true }]
        };

    [Fact]
    public void Percentile_uses_linear_interpolation()
    {
        var sample = Enumerable.Range(1, 100).Select(n => (double)n).ToList();

        Assert.Equal(1.99, TelemetryRollup.Percentile(sample, 0.01), 3);
        Assert.Equal(99.01, TelemetryRollup.Percentile(sample, 0.99), 3);
        Assert.Equal(50.5, TelemetryRollup.Percentile(sample, 0.5), 3);
    }

    [Fact]
    public void Percentile_single_sample_returns_that_value()
    {
        Assert.Equal(42d, TelemetryRollup.Percentile([42d], 0.99));
    }

    [Fact]
    public void Aggregate_returns_null_for_empty_window()
    {
        Assert.Null(TelemetryRollup.Aggregate(Sensor, [], BucketStart, 60));
    }

    [Fact]
    public void Aggregate_computes_numeric_summary_and_percentiles()
    {
        var observations = new List<SensorObservation>
        {
            Numeric(0, SensorState.Healthy, 10),
            Numeric(1, SensorState.Healthy, 20),
            Numeric(2, SensorState.Healthy, 30),
            Numeric(3, SensorState.Healthy, 40),
        };

        var bucket = TelemetryRollup.Aggregate(Sensor, observations, BucketStart, 60)!;

        Assert.Equal(4, bucket.SampleCount);
        Assert.Equal(25, bucket.Average);
        Assert.Equal(10, bucket.Minimum);
        Assert.Equal(40, bucket.Maximum);
        Assert.Equal("latency", bucket.DefaultChannelKey);
        Assert.Equal("ms", bucket.Unit);
        Assert.Equal(40, bucket.LastValue);
        Assert.Equal(SensorState.Healthy, bucket.State);
        Assert.Equal(100, bucket.UptimePercent);
        Assert.NotNull(bucket.LowPercentile);
        Assert.NotNull(bucket.HighPercentile);
        Assert.True(bucket.LowPercentile < bucket.HighPercentile);
    }

    [Fact]
    public void Aggregate_excludes_non_numeric_samples_but_counts_them_for_uptime()
    {
        var observations = new List<SensorObservation>
        {
            Numeric(0, SensorState.Healthy, 10),
            Numeric(1, SensorState.Healthy, 20),
            Numeric(2, SensorState.Healthy, 30),
            NoValue(3, SensorState.Critical),
        };

        var bucket = TelemetryRollup.Aggregate(Sensor, observations, BucketStart, 60)!;

        // Numeric aggregates only cover the three successful polls.
        Assert.Equal(3, bucket.SampleCount);
        Assert.Equal(20, bucket.Average);
        Assert.Equal(10, bucket.Minimum);
        Assert.Equal(30, bucket.Maximum);

        // State distribution covers every observation -> 75% uptime.
        Assert.Equal(3, bucket.HealthyCount);
        Assert.Equal(1, bucket.CriticalCount);
        Assert.Equal(75, bucket.UptimePercent);

        // Latest observation drives state and (missing) last value.
        Assert.Equal(SensorState.Critical, bucket.State);
        Assert.Null(bucket.LastValue);
    }

    [Fact]
    public void Aggregate_is_order_independent()
    {
        var ascending = new List<SensorObservation>
        {
            Numeric(0, SensorState.Healthy, 5),
            Numeric(1, SensorState.Warning, 15),
            Numeric(2, SensorState.Healthy, 25),
        };
        var shuffled = new List<SensorObservation> { ascending[2], ascending[0], ascending[1] };

        var fromAscending = TelemetryRollup.Aggregate(Sensor, ascending, BucketStart, 60)!;
        var fromShuffled = TelemetryRollup.Aggregate(Sensor, shuffled, BucketStart, 60)!;

        Assert.Equal(fromAscending.Average, fromShuffled.Average);
        Assert.Equal(fromAscending.Minimum, fromShuffled.Minimum);
        Assert.Equal(fromAscending.Maximum, fromShuffled.Maximum);
        Assert.Equal(fromAscending.LastValue, fromShuffled.LastValue);
        Assert.Equal(fromAscending.State, fromShuffled.State);
    }
}
