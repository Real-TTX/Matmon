namespace Matmon.Core.Domain;

public sealed class ProbeElement : MonitoringContainerElement
{
    public ProbeElement(string name) : base(name)
    {
    }

    public string ProbeId { get; set; } = string.Empty;

    public string? EnrollmentToken { get; set; }

    public override MonitoringElementKind Kind => MonitoringElementKind.Probe;
}
