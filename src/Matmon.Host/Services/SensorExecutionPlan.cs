using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// A detached, race-free snapshot of everything needed to execute one sensor: the resolved effective
/// (inherited) settings, the resolved target, and the sensor facts. Built under the store lock so the
/// caller (the polling hot path) never walks the live element tree while a request mutates it.
/// </summary>
public sealed record SensorExecutionPlan(
    Guid SensorId,
    string SensorTypeKey,
    string Target,
    bool IsPaused,
    MonitoringSettings EffectiveSettings);
