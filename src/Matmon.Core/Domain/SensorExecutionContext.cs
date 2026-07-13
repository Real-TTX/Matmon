namespace Matmon.Core.Domain;

public sealed class SensorExecutionContext
{
    public SensorExecutionContext(
        string sensorTypeKey,
        string target,
        MonitoringSettings settings,
        Guid? sensorId = null,
        SensorObservation? previousObservation = null)
    {
        SensorTypeKey = sensorTypeKey;
        Target = target;
        Settings = settings;
        SensorId = sensorId;
        PreviousObservation = previousObservation;
    }

    public string SensorTypeKey { get; }

    public string Target { get; }

    public MonitoringSettings Settings { get; }

    /// <summary>
    /// The id of the sensor being executed, when known. Null on transient/preview and stateless
    /// (cloud Executor) runs. Sensors that need per-instance identity (e.g. Mail Health tags its
    /// probe mails with this) read it here.
    /// </summary>
    public Guid? SensorId { get; }

    /// <summary>
    /// The sensor's most recent stored observation, when available (Primary polling + Secondary
    /// probe paths). Null on the first ever run, on transient/preview runs and in stateless Executor
    /// mode. Lets a sensor carry small state across runs (e.g. Mail Health reads the previous probe's
    /// send timestamp from a channel) without a dedicated per-sensor store.
    /// </summary>
    public SensorObservation? PreviousObservation { get; }
}
