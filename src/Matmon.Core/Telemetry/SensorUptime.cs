using Matmon.Core.Domain;

namespace Matmon.Core.Telemetry;

/// <summary>
/// Uptime from downsampled statistics buckets. Extracted so the scheduled summary report and the wallboard's
/// SLA widget cannot drift apart - the formula has two non-obvious parts that are easy to get wrong
/// independently:
/// <list type="bullet">
/// <item>Buckets are stored PER CHANNEL, but the healthy/warning/critical distribution is a property of the
/// sensor, not of one channel - so a multi-channel sensor has N identical distributions per window and they
/// must be deduped by window start, or its uptime is counted N times (which still yields the right
/// percentage, but a wildly wrong sample count).</item>
/// <item>"Up" counts Warning as up. A warning is a sensor that answered, so counting it as downtime would
/// make a permanently-warning-but-reachable host read as 0% available.</item>
/// </list>
/// Pure and framework-free, so it is unit-testable without a store.
/// </summary>
public static class SensorUptime
{
    /// <summary>Aggregates <paramref name="buckets"/> at or after <paramref name="fromUtc"/>.
    /// <see cref="UptimeSummary.Percent"/> is null when the window holds no state samples at all - that is
    /// "unknown", which a caller must not render as 0%.</summary>
    public static UptimeSummary FromBuckets(IEnumerable<SensorStatisticsBucket> buckets, DateTimeOffset fromUtc)
    {
        var healthy = 0L;
        var warning = 0L;
        var critical = 0L;
        var samples = 0L;
        var seenWindows = new HashSet<DateTimeOffset>();

        foreach (var bucket in buckets)
        {
            if (bucket.BucketStartUtc < fromUtc || !seenWindows.Add(bucket.BucketStartUtc))
            {
                continue;
            }

            healthy += bucket.HealthyCount;
            warning += bucket.WarningCount;
            critical += bucket.CriticalCount;
            samples += bucket.SampleCount;
        }

        var stateSamples = healthy + warning + critical;
        var percent = stateSamples > 0 ? (double)(healthy + warning) / stateSamples * 100 : (double?)null;
        return new UptimeSummary(healthy, warning, critical, samples, percent);
    }

    /// <summary>Rolls several sensors' summaries into one. Weighted by state samples rather than averaging the
    /// percentages, so a sensor with ten minutes of history cannot outweigh one with a week of it.</summary>
    public static UptimeSummary Combine(IEnumerable<UptimeSummary> summaries)
    {
        var healthy = 0L;
        var warning = 0L;
        var critical = 0L;
        var samples = 0L;

        foreach (var summary in summaries)
        {
            healthy += summary.Healthy;
            warning += summary.Warning;
            critical += summary.Critical;
            samples += summary.Samples;
        }

        var stateSamples = healthy + warning + critical;
        var percent = stateSamples > 0 ? (double)(healthy + warning) / stateSamples * 100 : (double?)null;
        return new UptimeSummary(healthy, warning, critical, samples, percent);
    }
}

/// <param name="Samples">Total observations behind the window - the honest "how much evidence is this
/// percentage based on" number, which is NOT Healthy+Warning+Critical (a bucket can hold samples whose state
/// was never classified).</param>
public readonly record struct UptimeSummary(long Healthy, long Warning, long Critical, long Samples, double? Percent)
{
    public long StateSamples => Healthy + Warning + Critical;

    public bool HasData => StateSamples > 0;
}
