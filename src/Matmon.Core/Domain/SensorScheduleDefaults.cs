namespace Matmon.Core.Domain;

/// <summary>
/// Default poll interval per sensor type, used as the fallback when neither the sensor nor any
/// ancestor sets an explicit schedule. The rule is deliberately simple: <b>ping</b> polls every
/// 30 seconds, every other sensor every 5 minutes. An explicit
/// <see cref="MonitoringSettings.PollingInterval"/> on the sensor/its ancestors always wins; it is
/// only ever clamped to <see cref="Minimum"/>.
/// </summary>
public static class SensorScheduleDefaults
{
    /// <summary>Hard floor on any effective poll interval, regardless of configuration.</summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(15);

    /// <summary>Catch-all fallback: every sensor except ping polls every 5 minutes by default.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<string, TimeSpan> ByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ping is the one fast-by-default reachability check; everything else uses Default (5 min).
        ["ping"] = TimeSpan.FromSeconds(30),
    };

    public static TimeSpan Resolve(string? sensorTypeKey)
    {
        var interval = !string.IsNullOrWhiteSpace(sensorTypeKey) && ByKey.TryGetValue(sensorTypeKey.Trim(), out var mapped)
            ? mapped
            : Default;

        return interval < Minimum ? Minimum : interval;
    }
}
