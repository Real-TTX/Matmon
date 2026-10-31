namespace Matmon.Core.Domain;

public sealed class HostElement : MonitoringContainerElement
{
    public HostElement(string name) : base(name)
    {
    }

    public string Address { get; set; } = string.Empty;

    /// <summary>Set when a Matmon agent runs on this machine: its sensors then run on the agent (see <see cref="AgentIdentity"/>).</summary>
    public AgentIdentity? Agent { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAgent => Agent is not null;

    public override MonitoringElementKind Kind => MonitoringElementKind.Host;

    public override MonitoringElement Clone()
    {
        var clone = new HostElement(Name) { Id = Id, Address = Address, Agent = Agent?.Clone() };
        CopyBaseTo(clone);
        CopyChildrenTo(clone);
        return clone;
    }
}
