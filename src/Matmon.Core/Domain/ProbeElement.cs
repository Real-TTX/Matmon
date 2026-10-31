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
    /// The address agents in this probe's network use to reach it (e.g. <c>http://10.0.0.5:8099</c>) - a remote probe
    /// relays its agents' requests to the primary, so an agent placed under it is installed against this address. Empty
    /// for the primary (agents there use the instance's own address) and for a probe no agent goes through.
    /// </summary>
    public string? AgentAddress { get; set; }

    /// <summary>
    /// LEGACY (before migration 12): when an agent enrolled as this probe. An agent is a host now
    /// (<see cref="HostElement.Agent"/>); migration 12 turns every probe carrying this into its host and removes the probe.
    /// Kept only so an older workspace or backup can be read and migrated.
    /// </summary>
    public DateTimeOffset? AgentEnrolledUtc { get; set; }

    /// <summary>LEGACY (before migration 12): the host such an agent probe sat on - see <see cref="AgentEnrolledUtc"/>.</summary>
    public Guid? BoundHostId { get; set; }

    /// <summary>LEGACY: the bound host while it is still inside this probe's own subtree. Read by migration 12 only.</summary>
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
            AgentAddress = AgentAddress,
            AgentEnrolledUtc = AgentEnrolledUtc,
            BoundHostId = BoundHostId,
            Subnets = [.. Subnets]
        };
        CopyBaseTo(clone);
        CopyChildrenTo(clone);
        return clone;
    }
}
