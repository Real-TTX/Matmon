namespace Matmon.Core.Domain;

/// <summary>
/// Default poll interval per sensor type, used as the fallback when neither the sensor nor any
/// ancestor sets an explicit schedule. The tiers are deliberately simple and intent-driven:
/// <list type="bullet">
/// <item><b>Ping</b> - 30 s (the one fast reachability check).</item>
/// <item><b>Most sensors</b> - 5 min (the <see cref="Default"/> "current data" cadence).</item>
/// <item><b>Slow-changing infra</b> (disk SMART, backup jobs) - 6 h.</item>
/// <item><b>Rarely-changing</b> (pending updates, certificate expiry) - once a day.</item>
/// </list>
/// An explicit <see cref="MonitoringSettings.PollingInterval"/> on the sensor/its ancestors always
/// wins; it is only ever clamped to <see cref="Minimum"/>.
/// </summary>
public static class SensorScheduleDefaults
{
    /// <summary>Hard floor on any effective poll interval, regardless of configuration.</summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(15);

    /// <summary>Catch-all fallback: every sensor without a more specific tier polls every 5 minutes.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan SixHours = TimeSpan.FromHours(6);
    private static readonly TimeSpan OncePerDay = TimeSpan.FromHours(24);

    private static readonly Dictionary<string, TimeSpan> ByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        // Fast reachability check.
        ["ping"] = TimeSpan.FromSeconds(30),

        // Slow-changing hardware/health: SMART attributes and backup outcomes barely move between
        // polls, so 6 h keeps the noise/overhead down while still catching failures within hours.
        ["disk-smart"] = SixHours,
        ["synology-disk"] = SixHours,
        ["windows-disk"] = SixHours,
        ["linux-disk"] = SixHours,
        ["proxmox-disk"] = SixHours,
        ["backup-job"] = SixHours,
        // The cloud recomputes update-availability each heartbeat; a 6 h poll surfaces it without churn.
        ["matmon-update"] = SixHours,

        // Rarely-changing: pending OS/package updates and certificate expiry only need a daily look.
        ["windows-update"] = OncePerDay,
        ["linux-update"] = OncePerDay,
        ["synology-update"] = OncePerDay,
        ["ssl-certificate"] = OncePerDay,
        ["certificate-chain"] = OncePerDay,
    };

    public static TimeSpan Resolve(string? sensorTypeKey)
    {
        var interval = !string.IsNullOrWhiteSpace(sensorTypeKey) && ByKey.TryGetValue(sensorTypeKey.Trim(), out var mapped)
            ? mapped
            : Default;

        return interval < Minimum ? Minimum : interval;
    }
}
