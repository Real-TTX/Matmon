using System.Text.Json.Serialization;

namespace Matmon.Core.Domain;

/// <summary>
/// A downsampled summary of one sensor's default channel over a fixed time
/// window (<see cref="BucketMinutes"/>). Buckets are recomputed accurately from
/// the raw observations that fall inside the window — see
/// <c>Matmon.Core.Telemetry.TelemetryRollup</c> — so percentiles and the state
/// distribution reflect the real samples rather than a lossy running average.
/// </summary>
public sealed class SensorStatisticsBucket
{
    public Guid SensorId { get; set; }

    public DateTimeOffset BucketStartUtc { get; set; }

    public int BucketMinutes { get; set; }

    public string DefaultChannelKey { get; set; } = string.Empty;

    public SensorState State { get; set; } = SensorState.Unknown;

    public int SampleCount { get; set; }

    public double? Average { get; set; }

    public double? Minimum { get; set; }

    public double? Maximum { get; set; }

    /// <summary>The low percentile (e.g. the 1st percentile — "bottom 1%").</summary>
    public double? LowPercentile { get; set; }

    /// <summary>The high percentile (e.g. the 99th percentile — "top 1%").</summary>
    public double? HighPercentile { get; set; }

    public double? LastValue { get; set; }

    public string? Unit { get; set; }

    public string? Message { get; set; }

    // State distribution over the bucket window (used for the uptime figure).
    public int HealthyCount { get; set; }

    public int WarningCount { get; set; }

    public int CriticalCount { get; set; }

    /// <summary>
    /// Percentage of state-bearing samples that were up (healthy or warning).
    /// Null when the window holds no up/down samples.
    /// </summary>
    [JsonIgnore]
    public double? UptimePercent
    {
        get
        {
            var total = HealthyCount + WarningCount + CriticalCount;
            return total == 0 ? null : (HealthyCount + WarningCount) * 100d / total;
        }
    }
}
