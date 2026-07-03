namespace Matmon.Core.Domain;

public sealed class HostElement : MonitoringContainerElement
{
    public HostElement(string name) : base(name)
    {
    }

    public string Address { get; set; } = string.Empty;

    public override MonitoringElementKind Kind => MonitoringElementKind.Host;

    public override MonitoringElement Clone()
    {
        var clone = new HostElement(Name) { Id = Id, Address = Address };
        CopyBaseTo(clone);
        CopyChildrenTo(clone);
        return clone;
    }
}
