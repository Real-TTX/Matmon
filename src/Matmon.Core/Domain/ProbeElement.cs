namespace Matmon.Core.Domain;

public sealed class ProbeElement : MonitoringContainerElement
{
    /// <summary>What the root probe of a fresh installation is called - the same on every install.</summary>
    public const string DefaultRootName = "Primary Probe";

    /// <summary>What the trees say next to a host that has an agent on it.</summary>
    public const string AgentLabel = "Matmon agent";

    public ProbeElement(string name) : base(name)
    {
    }

    public string ProbeId { get; set; } = string.Empty;

    public string? EnrollmentToken { get; set; }

    /// <summary>
    /// When an agent enrolled as this probe (<see cref="AgentEnrollment"/>); null for a Docker probe or one
    /// configured by hand. It is what makes a probe show up on the Agents page - an agent IS a probe, the
    /// page is a view over these rather than a second model.
    /// </summary>
    public DateTimeOffset? AgentEnrolledUtc { get; set; }

    /// <summary>
    /// The host this agent sits on - the machine it measures. An agent is a probe, but a probe is not a machine:
    /// the machine is a <see cref="HostElement"/> under it, and this says which one (a probe can hold many hosts,
    /// e.g. the other devices an agent scans). Null for a Docker probe or one configured by hand. It only counts
    /// while that host is still inside this probe's own subtree - see <see cref="FindBoundHost"/>.
    /// </summary>
    public Guid? BoundHostId { get; set; }

    /// <summary>The bound host, or null when there is none: never bound, deleted, or moved out of this probe.</summary>
    public HostElement? FindBoundHost() =>
        BoundHostId is Guid id
            ? MonitoringTopology.EnumerateDescendants(this).OfType<HostElement>().FirstOrDefault(host => host.Id == id)
            : null;

    /// <summary>
    /// Admin-configured subnets (CIDR) this probe is responsible for scanning. Independent of the
    /// auto-detected interfaces a secondary reports in its heartbeat - a probe can reach (route to)
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
            AgentEnrolledUtc = AgentEnrolledUtc,
            BoundHostId = BoundHostId,
            Subnets = [.. Subnets]
        };
        CopyBaseTo(clone);
        CopyChildrenTo(clone);
        return clone;
    }
}
