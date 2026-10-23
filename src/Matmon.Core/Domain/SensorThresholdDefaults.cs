namespace Matmon.Core.Domain;

/// <summary>
/// Sensible per-sensor-type, per-channel default warning/critical thresholds ("conditions").
/// Two uses:
/// <list type="number">
/// <item>The Thresholds editor prefills the placeholder + comparison for a channel that has a
/// default (<see cref="TryResolve"/>), so the user sees what a reasonable value looks like.</item>
/// <item>On sensor creation <see cref="Apply"/> seeds these thresholds onto the sensor's own
/// <see cref="MonitoringSettings"/> (only where none is set yet), so a new sensor alarms sensibly
/// out of the box.</item>
/// </list>
/// An explicit threshold already present on the sensor (or inherited) always wins - <see cref="Apply"/>
/// never overwrites, and the editor reads the stored value first. Values are chosen for a typical
/// home / small-business network; the user can always override per channel.
/// </summary>
public static class SensorThresholdDefaults
{
    /// <summary>One default condition: a rule for a specific (sensor type, channel, severity).</summary>
    private readonly record struct Entry(string Type, string Channel, string Severity, ThresholdDirection Direction, double Value);

    // The single source of truth. Keep entries grouped by sensor type for readability. "warning"
    // is the softer bound, "critical" the harder one; omit a severity to leave it unset. Values are
    // tuned for a home / small-business network and deliberately avoid nagging defaults: no
    // thresholds on cumulative counters (interface errors, container restarts) that would latch on
    // forever, and one clear signal per condition rather than several overlapping ones.
    private static readonly Entry[] Entries =
    [
        // ── Reachability / latency (ms) ───────────────────────────────────────────────────────
        new("ping", "latency", "warning", ThresholdDirection.Above, 80),
        new("ping", "latency", "critical", ThresholdDirection.Above, 200),
        // Loss: one packet of three (33 %) is worth a look, most of them gone is not a network you can rely on. A check
        // where nothing comes back is Critical by itself.
        new("ping", "packetLoss", "warning", ThresholdDirection.Above, 30),
        new("ping", "packetLoss", "critical", ThresholdDirection.Above, 70),
        // An interface busy on average over a whole poll interval is a link that is too small.
        new("snmp-interface", "utilization_in", "warning", ThresholdDirection.Above, 80),
        new("snmp-interface", "utilization_in", "critical", ThresholdDirection.Above, 95),
        new("snmp-interface", "utilization_out", "warning", ThresholdDirection.Above, 80),
        new("snmp-interface", "utilization_out", "critical", ThresholdDirection.Above, 95),
        new("http", "latency", "warning", ThresholdDirection.Above, 250),
        new("http", "latency", "critical", ThresholdDirection.Above, 1000),
        new("http-advanced", "latency", "warning", ThresholdDirection.Above, 500),
        new("http-advanced", "latency", "critical", ThresholdDirection.Above, 2000),
        new("tcp-port", "connectMs", "warning", ThresholdDirection.Above, 250),
        new("tcp-port", "connectMs", "critical", ThresholdDirection.Above, 1000),
        new("dns", "resolveMs", "warning", ThresholdDirection.Above, 100),
        new("dns", "resolveMs", "critical", ThresholdDirection.Above, 500),
        new("ntp", "absoluteOffsetMs", "warning", ThresholdDirection.Above, 100),
        new("ntp", "absoluteOffsetMs", "critical", ThresholdDirection.Above, 1000),
        new("ntp", "delayMs", "warning", ThresholdDirection.Above, 150),
        new("ntp", "delayMs", "critical", ThresholdDirection.Above, 800),

        // ── UPS (SNMP UPS-MIB and Network UPS Tools - the same channels, see UpsChannels) ─────────────
        // A power cut is critical AT ONCE: on battery is what an admin wants to hear about, not the charge falling
        // below 50 % half an hour later. Overload is critical too - an overloaded UPS switches its output off.
        new("ups-snmp", UpsChannels.OnBattery, "critical", ThresholdDirection.Above, 0),
        new("ups-snmp", UpsChannels.LowBattery, "critical", ThresholdDirection.Above, 0),
        new("ups-snmp", UpsChannels.Overload, "critical", ThresholdDirection.Above, 0),
        new("ups-snmp", UpsChannels.OnBypass, "warning", ThresholdDirection.Above, 0),
        new("ups-snmp", UpsChannels.ReplaceBattery, "warning", ThresholdDirection.Above, 0),
        new("ups-snmp", UpsChannels.BatteryCharge, "warning", ThresholdDirection.Below, 50),
        new("ups-snmp", UpsChannels.BatteryCharge, "critical", ThresholdDirection.Below, 20),
        new("ups-snmp", UpsChannels.RuntimeMinutes, "warning", ThresholdDirection.Below, 15),
        new("ups-snmp", UpsChannels.RuntimeMinutes, "critical", ThresholdDirection.Below, 5),
        new("ups-snmp", UpsChannels.LoadPercent, "warning", ThresholdDirection.Above, 70),
        new("ups-snmp", UpsChannels.LoadPercent, "critical", ThresholdDirection.Above, 90),
        new("nut-ups", UpsChannels.OnBattery, "critical", ThresholdDirection.Above, 0),
        new("nut-ups", UpsChannels.LowBattery, "critical", ThresholdDirection.Above, 0),
        new("nut-ups", UpsChannels.Overload, "critical", ThresholdDirection.Above, 0),
        new("nut-ups", UpsChannels.OnBypass, "warning", ThresholdDirection.Above, 0),
        new("nut-ups", UpsChannels.ReplaceBattery, "warning", ThresholdDirection.Above, 0),
        new("nut-ups", UpsChannels.BatteryCharge, "warning", ThresholdDirection.Below, 50),
        new("nut-ups", UpsChannels.BatteryCharge, "critical", ThresholdDirection.Below, 20),
        new("nut-ups", UpsChannels.RuntimeMinutes, "warning", ThresholdDirection.Below, 15),
        new("nut-ups", UpsChannels.RuntimeMinutes, "critical", ThresholdDirection.Below, 5),
        new("nut-ups", UpsChannels.LoadPercent, "warning", ThresholdDirection.Above, 70),
        new("nut-ups", UpsChannels.LoadPercent, "critical", ThresholdDirection.Above, 90),

        // ── Windows health / disk / updates ───────────────────────────────────────────────────
        // ── The probe's own machine (read locally) ────────────────────────────────────────────────
        new("local-health", "cpu", "warning", ThresholdDirection.Above, 85),
        new("local-health", "cpu", "critical", ThresholdDirection.Above, 95),
        new("local-health", "memoryUsedPercent", "warning", ThresholdDirection.Above, 85),
        new("local-health", "memoryUsedPercent", "critical", ThresholdDirection.Above, 95),
        new("local-health", "diskUsedPercent", "warning", ThresholdDirection.Above, 85),
        new("local-health", "diskUsedPercent", "critical", ThresholdDirection.Above, 95),

        new("windows-health", "cpuLoad", "warning", ThresholdDirection.Above, 85),
        new("windows-health", "cpuLoad", "critical", ThresholdDirection.Above, 95),
        new("windows-health", "memoryUsedPercent", "warning", ThresholdDirection.Above, 85),
        new("windows-health", "memoryUsedPercent", "critical", ThresholdDirection.Above, 95),
        new("windows-health", "diskUsedPercent", "warning", ThresholdDirection.Above, 85),
        new("windows-health", "diskUsedPercent", "critical", ThresholdDirection.Above, 95),
        new("windows-health", "smartStatus", "warning", ThresholdDirection.Above, 0),
        new("windows-health", "smartStatus", "critical", ThresholdDirection.Above, 1),
        new("windows-disk", "smartStatus", "warning", ThresholdDirection.Above, 0),
        new("windows-disk", "smartStatus", "critical", ThresholdDirection.Above, 1),
        new("windows-disk", "maxTemperature", "warning", ThresholdDirection.Above, 55),
        new("windows-disk", "maxTemperature", "critical", ThresholdDirection.Above, 65),
        new("windows-update", "pendingUpdates", "warning", ThresholdDirection.Above, 0),
        new("windows-update", "securityUpdates", "warning", ThresholdDirection.Above, 0),

        // ── Linux health / disk / updates ─────────────────────────────────────────────────────
        new("linux-ssh-health", "memoryUsedPercent", "warning", ThresholdDirection.Above, 85),
        new("linux-ssh-health", "memoryUsedPercent", "critical", ThresholdDirection.Above, 95),
        new("linux-ssh-health", "rootUsedPercent", "warning", ThresholdDirection.Above, 85),
        new("linux-ssh-health", "rootUsedPercent", "critical", ThresholdDirection.Above, 95),
        new("linux-ssh-health", "smartStatus", "critical", ThresholdDirection.AboveOrEqual, 2),
        new("linux-disk", "smartStatus", "critical", ThresholdDirection.AboveOrEqual, 2),
        new("linux-disk", "maxTemperature", "warning", ThresholdDirection.Above, 50),
        new("linux-disk", "maxTemperature", "critical", ThresholdDirection.Above, 60),
        new("linux-update", "pendingUpdates", "warning", ThresholdDirection.Above, 0),
        new("linux-update", "securityUpdates", "warning", ThresholdDirection.Above, 0),
        new("linux-update", "securityUpdates", "critical", ThresholdDirection.Above, 4),

        // ── Synology health / disk / updates ──────────────────────────────────────────────────
        new("synology-health", "cpuUtilization", "warning", ThresholdDirection.Above, 85),
        new("synology-health", "cpuUtilization", "critical", ThresholdDirection.Above, 95),
        new("synology-health", "memoryUtilization", "warning", ThresholdDirection.Above, 90),
        new("synology-health", "memoryUtilization", "critical", ThresholdDirection.Above, 97),
        new("synology-health", "temperature", "warning", ThresholdDirection.Above, 60),
        new("synology-health", "temperature", "critical", ThresholdDirection.Above, 70),
        new("synology-health", "smartStatus", "warning", ThresholdDirection.AboveOrEqual, 1),
        new("synology-health", "smartStatus", "critical", ThresholdDirection.AboveOrEqual, 2),
        new("synology-health", "storageUsedPercent", "warning", ThresholdDirection.Above, 85),
        new("synology-health", "storageUsedPercent", "critical", ThresholdDirection.Above, 95),
        new("synology-disk", "smartStatus", "warning", ThresholdDirection.AboveOrEqual, 1),
        new("synology-disk", "smartStatus", "critical", ThresholdDirection.AboveOrEqual, 2),
        new("synology-disk", "maxTemperature", "warning", ThresholdDirection.Above, 50),
        new("synology-disk", "maxTemperature", "critical", ThresholdDirection.Above, 60),
        new("synology-update", "updatesAvailable", "warning", ThresholdDirection.Above, 0),

        // ── Proxmox cluster / node / disk ─────────────────────────────────────────────────────
        new("proxmox-health", "quorum", "critical", ThresholdDirection.Below, 1),
        new("proxmox-health", "nodeOnline", "critical", ThresholdDirection.Below, 1),
        new("proxmox-health", "offlineNodes", "warning", ThresholdDirection.Above, 0),
        new("proxmox-health", "offlineNodes", "critical", ThresholdDirection.AboveOrEqual, 2),
        new("proxmox-health", "storageOffline", "warning", ThresholdDirection.Above, 0),
        new("proxmox-health", "storageOffline", "critical", ThresholdDirection.AboveOrEqual, 2),
        new("proxmox-node-health", "cpu", "warning", ThresholdDirection.AboveOrEqual, 85),
        new("proxmox-node-health", "cpu", "critical", ThresholdDirection.AboveOrEqual, 95),
        new("proxmox-node-health", "memory", "warning", ThresholdDirection.AboveOrEqual, 85),
        new("proxmox-node-health", "memory", "critical", ThresholdDirection.AboveOrEqual, 95),
        new("proxmox-node-health", "swap", "warning", ThresholdDirection.AboveOrEqual, 25),
        new("proxmox-node-health", "swap", "critical", ThresholdDirection.AboveOrEqual, 75),
        new("proxmox-node-health", "rootfs", "warning", ThresholdDirection.AboveOrEqual, 85),
        new("proxmox-node-health", "rootfs", "critical", ThresholdDirection.AboveOrEqual, 95),
        new("proxmox-node-health", "nodeOnline", "critical", ThresholdDirection.Below, 1),
        new("proxmox-node-health", "offlineNodes", "warning", ThresholdDirection.Above, 0),
        new("proxmox-node-health", "offlineNodes", "critical", ThresholdDirection.AboveOrEqual, 2),
        new("proxmox-node-health", "storageOffline", "warning", ThresholdDirection.Above, 0),
        new("proxmox-node-health", "storageOffline", "critical", ThresholdDirection.AboveOrEqual, 2),
        new("proxmox-disk", "smartStatus", "warning", ThresholdDirection.AboveOrEqual, 1),
        new("proxmox-disk", "smartStatus", "critical", ThresholdDirection.AboveOrEqual, 2),

        // ── VMware cluster / host (percent) ───────────────────────────────────────────────────
        new("vmware-health", "datastoreUsedPercent", "warning", ThresholdDirection.Above, 80),
        new("vmware-health", "datastoreUsedPercent", "critical", ThresholdDirection.Above, 90),
        new("vmware-host-health", "cpu", "warning", ThresholdDirection.Above, 85),
        new("vmware-host-health", "cpu", "critical", ThresholdDirection.Above, 95),
        new("vmware-host-health", "memory", "warning", ThresholdDirection.Above, 85),
        new("vmware-host-health", "memory", "critical", ThresholdDirection.Above, 95),

        // ── UniFi (percent online) ────────────────────────────────────────────────────────────
        new("unifi-health", "onlineRatio", "warning", ThresholdDirection.Below, 100),
        new("unifi-health", "onlineRatio", "critical", ThresholdDirection.Below, 80),

        // ── Mail round-trip (seconds) ─────────────────────────────────────────────────────────
        // "Slow but delivered" warning; hard non-delivery is Critical from the sensor's own logic
        // (governed by the tolerance parameter), so only a warning threshold is seeded here.
        new("mail-health", "roundTripSeconds", "warning", ThresholdDirection.Above, 300),

        // ── Syslog ────────────────────────────────────────────────────────────────────────────────
        // A device's errors in the sensor's window (an hour unless changed): one is worth a look, ten is a problem.
        // Deliberately NOTHING on "since last message" - plenty of devices only log when something happens, so
        // silence means a problem only for the ones the user knows are chatty (they set that themselves) - and the
        // match sensor warns on the first hit, since looking for a phrase at all means its appearance matters.
        new("syslog-messages", "errors", "warning", ThresholdDirection.Above, 0),
        new("syslog-messages", "errors", "critical", ThresholdDirection.Above, 9),
        new("syslog-match", "matches", "warning", ThresholdDirection.Above, 0),

        // ── Backup job / disk SMART / probe heartbeat ─────────────────────────────────────────
        new("probe-heartbeat", "ageSeconds", "warning", ThresholdDirection.Above, 30),
        new("probe-heartbeat", "ageSeconds", "critical", ThresholdDirection.Above, 60),
    ];

    // (type, channel, severity) → rule, for O(1) editor lookups. All three key parts are
    // lower-cased so the lookup is case-insensitive without a custom comparer (which would be
    // brittle against static-field initialization order).
    private static readonly Dictionary<(string Type, string Channel, string Severity), ThresholdRule> ByKey =
        Entries.ToDictionary(
            e => (e.Type.ToLowerInvariant(), e.Channel.ToLowerInvariant(), e.Severity.ToLowerInvariant()),
            e => new ThresholdRule(e.Direction, e.Value));

    // type → its (channel, severity, rule) defaults, for Apply.
    private static readonly Dictionary<string, IReadOnlyList<Entry>> ByType =
        Entries
            .GroupBy(e => e.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Entry>)g.ToArray(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Looks up the default rule for a channel's severity, if one is defined.</summary>
    public static bool TryResolve(string? sensorTypeKey, string channelKey, string severity, out ThresholdRule rule)
    {
        if (!string.IsNullOrWhiteSpace(sensorTypeKey) && !string.IsNullOrWhiteSpace(channelKey) &&
            ByKey.TryGetValue(
                (sensorTypeKey.Trim().ToLowerInvariant(), channelKey.ToLowerInvariant(), severity.ToLowerInvariant()),
                out rule))
        {
            return true;
        }

        rule = default;
        return false;
    }

    /// <summary>
    /// Seeds the sensor-type's default thresholds onto <paramref name="settings"/>, skipping any
    /// channel/severity that already has an (own) threshold. Safe to call repeatedly.
    /// </summary>
    public static void Apply(string? sensorTypeKey, MonitoringSettings? settings)
    {
        if (settings is null || string.IsNullOrWhiteSpace(sensorTypeKey) ||
            !ByType.TryGetValue(sensorTypeKey.Trim(), out var defaults))
        {
            return;
        }

        foreach (var entry in defaults)
        {
            if (!MonitoringSettings.TryReadChannelThreshold(settings, entry.Channel, entry.Severity, out _))
            {
                MonitoringSettings.SetChannelThreshold(settings, entry.Channel, entry.Severity,
                    new ThresholdRule(entry.Direction, entry.Value));
            }
        }
    }
}
