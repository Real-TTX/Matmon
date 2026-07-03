namespace Matmon.Core.Domain;

public sealed class SensorElement : MonitoringElement
{
    public SensorElement(string name, string sensorTypeKey, string target) : base(name)
    {
        SensorTypeKey = sensorTypeKey;
        Target = target;
    }

    public string SensorTypeKey { get; set; }

    public string Target { get; set; }

    public bool IsPaused { get; set; }

    public override MonitoringElementKind Kind => MonitoringElementKind.Sensor;

    public override MonitoringElement Clone()
    {
        var clone = new SensorElement(Name, SensorTypeKey, Target) { Id = Id, IsPaused = IsPaused };
        CopyBaseTo(clone);
        return clone;
    }
}
