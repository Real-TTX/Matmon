namespace Matmon.Core.Domain;

public sealed class HostElement : MonitoringContainerElement
{
    public HostElement(string name) : base(name)
    {
    }

    public string Address { get; set; } = string.Empty;

    public override MonitoringElementKind Kind => MonitoringElementKind.Host;
}
