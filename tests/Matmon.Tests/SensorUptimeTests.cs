using Matmon.Core.Domain;
using Matmon.Core.Telemetry;

namespace Matmon.Tests;

/// <summary>
/// The uptime formula shared by the scheduled summary report and the wallboard's SLA widget. Both of its
/// non-obvious rules are pinned here, because both are the kind of thing a well-meaning edit would "fix":
/// per-channel buckets must be deduped, and a warning counts as up.
/// </summary>
public class SensorUptimeTests
{
    private static readonly DateTimeOffset Window = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static SensorStatisticsBucket Bucket(
        DateTimeOffset start, int healthy, int warning, int critical, string channel = "default", int samples = -1) =>
        new()
        {
            SensorId = Guid.Empty,
            BucketMinutes = 60,
            BucketStartUtc = start,
            DefaultChannelKey = channel,
            HealthyCount = healthy,
            WarningCount = warning,
            CriticalCount = critical,
            SampleCount = samples < 0 ? healthy + warning + critical : samples
        };

    [Fact]
    public void A_warning_counts_as_up()
    {
        // A warning means the sensor answered. Counting it as downtime would make a permanently-warning but
        // perfectly reachable host read as 0% available.
        var summary = SensorUptime.FromBuckets([Bucket(Window, healthy: 0, warning: 10, critical: 0)], Window);

        Assert.Equal(100, summary.Percent);
    }

    [Fact]
    public void Critical_samples_are_the_only_downtime()
    {
        var summary = SensorUptime.FromBuckets([Bucket(Window, healthy: 70, warning: 20, critical: 10)], Window);

        Assert.Equal(90, summary.Percent);
    }

    [Fact]
    public void Buckets_for_the_same_window_are_counted_once_across_channels()
    {
        // Statistics are stored PER CHANNEL, but the healthy/warning/critical distribution belongs to the
        // sensor - so a three-channel sensor has three identical distributions per window. Without the dedupe
        // the percentage still comes out right, which is exactly what makes the bug easy to miss; the sample
        // count is what gives it away.
        var buckets = new[]
        {
            Bucket(Window, 9, 0, 1, channel: "cpu"),
            Bucket(Window, 9, 0, 1, channel: "mem"),
            Bucket(Window, 9, 0, 1, channel: "disk")
        };

        var summary = SensorUptime.FromBuckets(buckets, Window);

        Assert.Equal(90, summary.Percent);
        Assert.Equal(10, summary.StateSamples);
    }

    [Fact]
    public void Buckets_before_the_window_are_ignored()
    {
        var buckets = new[]
        {
            Bucket(Window.AddHours(-5), 0, 0, 100),
            Bucket(Window.AddHours(1), 10, 0, 0)
        };

        var summary = SensorUptime.FromBuckets(buckets, Window);

        Assert.Equal(100, summary.Percent);
        Assert.Equal(10, summary.StateSamples);
    }

    [Fact]
    public void No_state_samples_is_unknown_not_zero()
    {
        // Rendering "0%" for a sensor that simply has no history yet would read as a total outage.
        var empty = SensorUptime.FromBuckets([], Window);

        Assert.Null(empty.Percent);
        Assert.False(empty.HasData);
    }

    [Fact]
    public void Combine_weights_by_samples_rather_than_averaging_percentages()
    {
        // One sensor at 100% with 10 minutes of history must not drag a 50% sensor with a week of it up to 75%.
        var big = SensorUptime.FromBuckets([Bucket(Window, healthy: 500, warning: 0, critical: 500)], Window);
        var small = SensorUptime.FromBuckets([Bucket(Window, healthy: 10, warning: 0, critical: 0)], Window);

        var combined = SensorUptime.Combine([big, small]);

        Assert.Equal(510d / 1010 * 100, combined.Percent!.Value, 6);
        Assert.Equal(1010, combined.StateSamples);
    }

    [Fact]
    public void Combine_of_nothing_is_unknown()
    {
        Assert.Null(SensorUptime.Combine([]).Percent);
    }
}
