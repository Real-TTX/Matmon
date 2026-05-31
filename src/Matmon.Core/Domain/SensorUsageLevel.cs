namespace Matmon.Core.Domain;

public enum SensorUsageLevel
{
    Low = 0,
    Moderate = 1,
    High = 2
}

public static class SensorUsageCatalog
{
    private static readonly IReadOnlyDictionary<string, SensorUsageLevel> UsageLevels =
        new Dictionary<string, SensorUsageLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["ping"] = SensorUsageLevel.Low,
            ["tcp-port"] = SensorUsageLevel.Low,
            ["dns"] = SensorUsageLevel.Low,
            ["ntp"] = SensorUsageLevel.Low,
            ["probe-heartbeat"] = SensorUsageLevel.Low,

            ["http"] = SensorUsageLevel.Moderate,
            ["http-advanced"] = SensorUsageLevel.Moderate,
            ["snmp"] = SensorUsageLevel.Moderate,
            ["snmp-interface"] = SensorUsageLevel.Moderate,
            ["ups-snmp"] = SensorUsageLevel.Moderate,
            ["synology-nas"] = SensorUsageLevel.Moderate,
            ["ssl-certificate"] = SensorUsageLevel.Moderate,
            ["certificate-chain"] = SensorUsageLevel.Moderate,
            ["windows-service"] = SensorUsageLevel.Moderate,
            ["windows-process"] = SensorUsageLevel.Moderate,
            ["linux-ssh-health"] = SensorUsageLevel.Moderate,
            ["docker-container"] = SensorUsageLevel.Moderate,
            ["backup-job"] = SensorUsageLevel.Moderate,
            ["disk-smart"] = SensorUsageLevel.Moderate,
            ["probe-health"] = SensorUsageLevel.Moderate,

            ["mssql"] = SensorUsageLevel.High,
            ["proxmox-pve"] = SensorUsageLevel.High,
            ["powershell-remote"] = SensorUsageLevel.High
        };

    public static SensorUsageLevel Resolve(string? sensorTypeKey)
    {
        if (string.IsNullOrWhiteSpace(sensorTypeKey))
        {
            return SensorUsageLevel.Moderate;
        }

        return UsageLevels.TryGetValue(sensorTypeKey, out var usageLevel)
            ? usageLevel
            : SensorUsageLevel.Moderate;
    }

    public static string Label(SensorUsageLevel usageLevel)
    {
        return usageLevel switch
        {
            SensorUsageLevel.Low => "Low",
            SensorUsageLevel.High => "High",
            _ => "Moderate"
        };
    }

    public static string Key(SensorUsageLevel usageLevel)
    {
        return usageLevel switch
        {
            SensorUsageLevel.Low => "low",
            SensorUsageLevel.High => "high",
            _ => "moderate"
        };
    }

    public static int Weight(SensorUsageLevel usageLevel)
    {
        return usageLevel switch
        {
            SensorUsageLevel.Low => 1,
            SensorUsageLevel.High => 3,
            _ => 2
        };
    }
}
