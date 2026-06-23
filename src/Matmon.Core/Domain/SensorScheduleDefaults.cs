namespace Matmon.Core.Domain;

/// <summary>
/// Sensible default poll interval per sensor type, used as the fallback when
/// neither the sensor nor any ancestor sets an explicit schedule. Cheap network
/// probes poll often; slow/expensive checks or rarely-changing facts (SMART,
/// certificates, backups) poll far less aggressively. An explicit
/// <see cref="MonitoringSettings.PollingInterval"/> on the sensor/its ancestors
/// always wins — this is only the type fallback (the schedule counterpart to
/// <c>SensorTelemetryProfiles</c>).
/// </summary>
public static class SensorScheduleDefaults
{
    /// <summary>Catch-all fallback when a type is not mapped.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<string, TimeSpan> ByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        // Cheap, fast network reachability / latency probes.
        ["ping"] = TimeSpan.FromSeconds(30),
        ["tcp-port"] = TimeSpan.FromSeconds(60),
        ["http"] = TimeSpan.FromSeconds(60),
        ["http-advanced"] = TimeSpan.FromSeconds(60),
        ["dns"] = TimeSpan.FromSeconds(60),
        ["ntp"] = TimeSpan.FromMinutes(5),

        // SNMP / appliance polling — a touch heavier, still frequent.
        ["snmp"] = TimeSpan.FromSeconds(60),
        ["snmp-interface"] = TimeSpan.FromSeconds(60),
        ["ups-snmp"] = TimeSpan.FromSeconds(60),

        // Authenticated/remote health checks that pull a lot of data or shell out over WinRM/SSH
        // are comparatively expensive, so they default to a calmer cadence.
        ["mssql"] = TimeSpan.FromMinutes(5),
        ["proxmox"] = TimeSpan.FromMinutes(5),
        ["proxmox-health"] = TimeSpan.FromMinutes(5),
        ["proxmox-node-health"] = TimeSpan.FromMinutes(5),
        ["vmware-health"] = TimeSpan.FromMinutes(5),
        ["vmware-host-health"] = TimeSpan.FromMinutes(5),
        ["unifi-health"] = TimeSpan.FromMinutes(5),
        ["synology"] = TimeSpan.FromMinutes(5),
        ["synology-health"] = TimeSpan.FromMinutes(5),
        ["windows-health"] = TimeSpan.FromMinutes(5),
        ["linux-ssh-health"] = TimeSpan.FromMinutes(5),
        ["powershell"] = TimeSpan.FromMinutes(5),
        // Lighter status checks stay snappier.
        ["docker-container"] = TimeSpan.FromMinutes(1),
        ["windows-service"] = TimeSpan.FromMinutes(1),
        ["windows-process"] = TimeSpan.FromMinutes(1),
        ["local-script"] = TimeSpan.FromMinutes(2),
        ["local-program"] = TimeSpan.FromMinutes(2),

        // Slow / rarely-changing facts.
        ["disk-smart"] = TimeSpan.FromMinutes(15),
        ["synology-disk"] = TimeSpan.FromMinutes(15),
        ["windows-disk"] = TimeSpan.FromMinutes(15),
        ["linux-disk"] = TimeSpan.FromMinutes(15),
        ["proxmox-disk"] = TimeSpan.FromMinutes(15),
        ["backup-job"] = TimeSpan.FromMinutes(30),
        ["ssl-certificate"] = TimeSpan.FromHours(6),
        ["certificate-chain"] = TimeSpan.FromHours(6),

        // Probe infrastructure.
        ["probe-heartbeat"] = TimeSpan.FromSeconds(30),
        ["probe-health"] = TimeSpan.FromSeconds(60),
    };

    public static TimeSpan Resolve(string? sensorTypeKey)
    {
        if (!string.IsNullOrWhiteSpace(sensorTypeKey) && ByKey.TryGetValue(sensorTypeKey.Trim(), out var interval))
        {
            return interval;
        }

        return Default;
    }
}
