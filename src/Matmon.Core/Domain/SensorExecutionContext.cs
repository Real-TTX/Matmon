namespace Matmon.Core.Domain;

public sealed class SensorExecutionContext
{
    public SensorExecutionContext(string sensorTypeKey, string target, MonitoringSettings settings)
    {
        SensorTypeKey = sensorTypeKey;
        Target = target;
        Settings = settings;
    }

    public string SensorTypeKey { get; }

    public string Target { get; }

    public MonitoringSettings Settings { get; }
}
