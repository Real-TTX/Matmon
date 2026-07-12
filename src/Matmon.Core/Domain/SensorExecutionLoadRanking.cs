namespace Matmon.Core.Domain;

/// <summary>
/// A rough per-sensor-type execution "cost" tier, used as a secondary sort so a catch-up burst (e.g. after a
/// paused folder is resumed) runs the cheap/fast checks first (ping, TCP, DNS) before the heavy ones (VMware,
/// Proxmox, SNMP walks, remote scripts). Purely an ordering hint - it never changes what runs, only the order.
/// </summary>
public static class SensorExecutionLoadRanking
{
    public const int Light = 0;
    public const int Moderate = 1;
    public const int Heavy = 2;

    private static readonly Dictionary<string, int> Tiers = new(StringComparer.OrdinalIgnoreCase)
    {
        // Light - tiny network probes, near-instant.
        ["ping"] = Light,
        ["tcp-port"] = Light,
        ["dns"] = Light,
        ["ntp"] = Light,
        ["http"] = Light,
        ["http-advanced"] = Light,
        ["probe-heartbeat"] = Light,
        ["probe-health"] = Light,
        ["matmon-update"] = Light,

        // Moderate - a single API/cert/query round-trip.
        ["ssl-certificate"] = Moderate,
        ["certificate-chain"] = Moderate,
        ["docker-container"] = Moderate,
        ["windows-service"] = Moderate,
        ["windows-process"] = Moderate,
        ["backup-job"] = Moderate,
        ["unifi-health"] = Moderate,
        ["windows-update"] = Moderate,
        ["linux-update"] = Moderate,
        ["synology-update"] = Moderate,

        // Heavy - SNMP walks, SSH/WinRM/script sessions, hypervisor/NAS SOAP+multi-call, disk/SMART scans.
        ["snmp"] = Heavy,
        ["snmp-interface"] = Heavy,
        ["ups-snmp"] = Heavy,
        ["mssql"] = Heavy,
        ["powershell"] = Heavy,
        ["local-script"] = Heavy,
        ["local-program"] = Heavy,
        ["windows-health"] = Heavy,
        ["linux-ssh-health"] = Heavy,
        ["proxmox"] = Heavy,
        ["proxmox-health"] = Heavy,
        ["proxmox-node-health"] = Heavy,
        ["proxmox-disk"] = Heavy,
        ["vmware-health"] = Heavy,
        ["vmware-host-health"] = Heavy,
        ["synology"] = Heavy,
        ["synology-health"] = Heavy,
        ["synology-disk"] = Heavy,
        ["windows-disk"] = Heavy,
        ["linux-disk"] = Heavy,
    };

    /// <summary>Tier for a sensor type key (Light/Moderate/Heavy). Unknown types default to Moderate.</summary>
    public static int GetTier(string? sensorTypeKey) =>
        !string.IsNullOrWhiteSpace(sensorTypeKey) && Tiers.TryGetValue(sensorTypeKey, out var tier) ? tier : Moderate;
}
