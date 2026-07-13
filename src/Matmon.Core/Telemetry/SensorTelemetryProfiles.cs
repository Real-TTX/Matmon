namespace Matmon.Core.Telemetry;

/// <summary>
/// Sensible telemetry retention/aggregation defaults for a sensor. Numeric
/// trend sensors (latency, throughput) keep raw samples briefly but summarise
/// them per hour for a long time; availability sensors keep raw data longer and
/// summarise per day. These are only the fallback - an explicit
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
    // Defaults are intentionally lean: ~3 days of full-resolution raw samples, then downsampled
    // statistics for ~2 weeks. Plenty for a home / small-business monitor and keeps the telemetry
    // DB small; bump the retention per-sensor when a longer history is wanted.

    /// <summary>Catch-all default: 3 days raw, hourly buckets, 14 days of statistics.</summary>
    public static readonly SensorTelemetryProfile General =
        new("General", RawObservationDays: 3, StatisticsBucketMinutes: 60, StatisticsRetentionDays: 14, EventRetentionDays: 30);

    /// <summary>Latency/throughput sensors: 3 days raw, hourly buckets, 14 days of statistics.</summary>
    public static readonly SensorTelemetryProfile Responsive =
        new("Responsive metrics", RawObservationDays: 3, StatisticsBucketMinutes: 60, StatisticsRetentionDays: 14, EventRetentionDays: 30);

    /// <summary>Up/down sensors: 3 days raw, daily buckets, 14 days of statistics, longer events.</summary>
    public static readonly SensorTelemetryProfile Availability =
        new("Availability", RawObservationDays: 3, StatisticsBucketMinutes: 1440, StatisticsRetentionDays: 14, EventRetentionDays: 90);

    /// <summary>Probe infrastructure sensors: 2 days raw, hourly buckets, 14 days of statistics.</summary>
    public static readonly SensorTelemetryProfile Infrastructure =
        new("Probe infrastructure", RawObservationDays: 2, StatisticsBucketMinutes: 60, StatisticsRetentionDays: 14, EventRetentionDays: 30);

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
        ["postgres"] = Responsive,
        ["mysql"] = Responsive,
        ["proxmox"] = Responsive,
        ["proxmox-health"] = Responsive,
        ["proxmox-node-health"] = Responsive,
        ["vmware-health"] = Responsive,
        ["vmware-host-health"] = Responsive,
        ["synology"] = Responsive,
        ["synology-health"] = Responsive,
        ["synology-disk"] = Responsive,
        ["windows-disk"] = Responsive,
        ["linux-disk"] = Responsive,
        ["proxmox-disk"] = Responsive,

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
        ["windows-eventlog"] = Responsive,
        ["mail-health"] = Responsive,
        ["windows-update"] = Availability,
        ["linux-update"] = Availability,
        ["synology-update"] = Availability,
        ["unifi-health"] = Availability,

        // Probe infrastructure.
        ["probe-heartbeat"] = Infrastructure,
        ["probe-health"] = Infrastructure,
        ["matmon-update"] = Availability,
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
