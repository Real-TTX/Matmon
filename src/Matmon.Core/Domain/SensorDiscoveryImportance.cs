namespace Matmon.Core.Domain;

/// <summary>
/// Importance ranking for discovery suggestions. Drives the ORDER of a host's suggested sensors (Ping first,
/// then the compact platform-health sensor, then everything else by confidence) and which are "recommended"
/// (pre-selected + shown, not collapsed) in the import assistant.
///
/// This is deliberately separate from raw <c>Confidence</c>: a Linux SSH health check has only modest
/// detection confidence but is far more valuable to monitor than, say, a stray open TCP port. So importance
/// is about "how much you want this sensor", confidence is about "how sure we are it applies".
/// </summary>
public static class SensorDiscoveryImportance
{
    /// <summary>The Ping sensor's type key - the baseline availability check, always on top.</summary>
    public const string PingKey = "ping";

    /// <summary>Ping is the baseline availability check - always ranks highest.</summary>
    public const int PingRank = 100;

    /// <summary>Compact per-platform Health sensors (one sensor covers CPU/mem/disk/SMART) - the best next step.</summary>
    public const int PlatformHealthRank = 90;

    /// <summary>Everything else shares this rank, so it sorts among itself by confidence.</summary>
    public const int DefaultRank = 50;

    /// <summary>At/above this rank a suggestion is "recommended" (pre-selected, shown first, badged).</summary>
    public const int RecommendedRank = PlatformHealthRank;

    /// <summary>The compact per-platform Health sensor type keys (one sensor = the whole box's health).</summary>
    private static readonly HashSet<string> PlatformHealthKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "synology-health",
        "windows-health",
        "linux-ssh-health",
        "proxmox-health",
        "proxmox-node-health",
        "vmware-health",
        "vmware-host-health",
        "unifi-health",
    };

    public static int Rank(string? sensorTypeKey)
    {
        var key = (sensorTypeKey ?? string.Empty).Trim();
        if (string.Equals(key, PingKey, StringComparison.OrdinalIgnoreCase))
        {
            return PingRank;
        }

        return PlatformHealthKeys.Contains(key) ? PlatformHealthRank : DefaultRank;
    }

    /// <summary>True for the compact per-platform Health sensors (Synology/Windows/Linux/Proxmox/VMware/UniFi).</summary>
    public static bool IsPlatformHealth(string? sensorTypeKey) =>
        !string.IsNullOrWhiteSpace(sensorTypeKey) && PlatformHealthKeys.Contains(sensorTypeKey.Trim());

    /// <summary>Ping + any platform-health sensor are always recommended, independent of confidence.</summary>
    public static bool IsRecommended(string? sensorTypeKey) => Rank(sensorTypeKey) >= RecommendedRank;
}
