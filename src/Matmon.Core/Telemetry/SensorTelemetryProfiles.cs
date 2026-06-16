namespace Matmon.Core.Telemetry;

/// <summary>
/// Sensible telemetry retention/aggregation defaults for a sensor. Numeric
/// trend sensors (latency, throughput) keep raw samples briefly but summarise
/// them per hour for a long time; availability sensors keep raw data longer and
/// summarise per day. These are only the fallback — an explicit
/// <see cref="Matmon.Core.Domain.MonitoringSettings"/> override always wins.
/// </summary>
public sealed record SensorTelemetryProfile(
    string Name,
    int RawObservationDays,
    int StatisticsBucketMinutes,
    int StatisticsRetentionDays,
    int EventRetentionDays);

public static class SensorTelemetryProfiles
{
    /// <summary>Catch-all default — mirrors Matmon's historical retention.</summary>
    public static readonly SensorTelemetryProfile General =
        new("General", RawObservationDays: 7, StatisticsBucketMinutes: 60, StatisticsRetentionDays: 90, EventRetentionDays: 30);

    /// <summary>Latency/throughput sensors: short raw window, hourly buckets, kept a year.</summary>
    public static readonly SensorTelemetryProfile Responsive =
        new("Responsive metrics", RawObservationDays: 3, StatisticsBucketMinutes: 60, StatisticsRetentionDays: 365, EventRetentionDays: 30);

    /// <summary>Up/down sensors: longer raw window, daily buckets, kept a year.</summary>
    public static readonly SensorTelemetryProfile Availability =
        new("Availability", RawObservationDays: 14, StatisticsBucketMinutes: 1440, StatisticsRetentionDays: 365, EventRetentionDays: 90);

    /// <summary>Probe infrastructure sensors: minimal raw retention, hourly buckets.</summary>
    public static readonly SensorTelemetryProfile Infrastructure =
        new("Probe infrastructure", RawObservationDays: 2, StatisticsBucketMinutes: 60, StatisticsRetentionDays: 180, EventRetentionDays: 30);

    private static readonly Dictionary<string, SensorTelemetryProfile> ByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        // Latency / throughput / numeric trend metrics.
        ["ping"] = Responsive,
        ["http"] = Responsive,
        ["http-advanced"] = Responsive,
        ["dns"] = Responsive,
        ["ntp"] = Responsive,
        ["snmp"] = Responsive,
        ["snmp-interface"] = Responsive,
        ["ups-snmp"] = Responsive,
        ["mssql"] = Responsive,
        ["proxmox"] = Responsive,
        ["synology"] = Responsive,
        ["synology-health"] = Responsive,
        ["disk-smart"] = Responsive,

        // Availability / up-down style.
        ["tcp-port"] = Availability,
        ["ssl-certificate"] = Availability,
        ["certificate-chain"] = Availability,
        ["docker-container"] = Availability,
        ["windows-service"] = Availability,
        ["windows-process"] = Availability,
        ["windows-health"] = Availability,
        ["linux-ssh-health"] = Availability,
        ["powershell"] = Availability,
        ["backup-job"] = Availability,

        // Probe infrastructure.
        ["probe-heartbeat"] = Infrastructure,
        ["probe-health"] = Infrastructure,
    };

    /// <summary>All distinct profiles, for documentation/UI listing.</summary>
    public static IReadOnlyList<SensorTelemetryProfile> All { get; } =
        [General, Responsive, Availability, Infrastructure];

    public static SensorTelemetryProfile Resolve(string? sensorTypeKey)
    {
        if (!string.IsNullOrWhiteSpace(sensorTypeKey) && ByKey.TryGetValue(sensorTypeKey.Trim(), out var profile))
        {
            return profile;
        }

        return General;
    }
}
