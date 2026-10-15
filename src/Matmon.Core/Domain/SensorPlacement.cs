using Matmon.Core.Syslog;

namespace Matmon.Core.Domain;

/// <summary>
/// Where a sensor type runs. Most sensors run on the probe that owns their place in the tree - the nearest probe above
/// them - so a sensor under an agent is measured from the agent's machine. A few cannot: they read something only the
/// PRIMARY has, so they run there whatever probe they sit under, and are never handed to a remote probe (which would
/// answer "no executor"):
/// <list type="bullet">
/// <item>the probe heartbeat - it reads the primary's own picture of when a probe last reported;</item>
/// <item>the syslog sensors - they query the central log the primary's receiver fills.</item>
/// </list>
/// This is the one place that says so; the polling service, the probe assignments and the on-demand "run now" routing all
/// ask it, so they cannot disagree.
/// </summary>
public static class SensorPlacement
{
    private static readonly HashSet<string> PrimaryOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        ProbeHeartbeatSensorExecutor.Definition.Key,
        SyslogSensorTypes.Messages,
        SyslogSensorTypes.Match
    };

    public static bool RunsOnPrimaryOnly(string? sensorTypeKey) =>
        !string.IsNullOrWhiteSpace(sensorTypeKey) && PrimaryOnly.Contains(sensorTypeKey.Trim());
}
