namespace Matmon.Core.Domain;

public sealed class ProbeElement : MonitoringContainerElement
{
    public ProbeElement(string name) : base(name)
    {
    }

    public string ProbeId { get; set; } = string.Empty;

    public string? EnrollmentToken { get; set; }

    /// <summary>
    /// Admin-configured subnets (CIDR) this probe is responsible for scanning. Independent of the
    /// auto-detected interfaces a secondary reports in its heartbeat — a probe can reach (route to)
    /// networks it isn't directly attached to, so these are set by hand and used as discovery scopes.
    /// </summary>
    public List<string> Subnets { get; set; } = [];

    public override MonitoringElementKind Kind => MonitoringElementKind.Probe;

    public override MonitoringElement Clone()
    {
        var clone = new ProbeElement(Name)
        {
            Id = Id,
            ProbeId = ProbeId,
            EnrollmentToken = EnrollmentToken,
            Subnets = [.. Subnets]
        };
        CopyBaseTo(clone);
        CopyChildrenTo(clone);
        return clone;
    }
}
