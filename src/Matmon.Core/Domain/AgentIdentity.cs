namespace Matmon.Core.Domain;

/// <summary>
/// What makes a <see cref="HostElement"/> a host with a Matmon agent on it: the identity the installed agent uses to talk
/// to the instance (an id and a token, the same pair a remote probe uses). An agent IS its host - it measures that machine
/// and nothing else, its sensors live under the host and run on the agent. Where the host sits says which probe the agent
/// reaches the instance through: under the primary it talks to the primary, under a remote probe that probe relays its
/// requests.
/// </summary>
public sealed class AgentIdentity
{
    /// <summary>The id the agent sends (header / URL) - unique among probes and agents.</summary>
    public string ProbeId { get; set; } = string.Empty;

    public string? Token { get; set; }

    public DateTimeOffset EnrolledUtc { get; set; }

    /// <summary>A hash of the machine's own id (<see cref="AgentMachineId"/>), when the agent reported one - recognises the machine when it
    /// enrols again with a rollout key.</summary>
    public string? MachineId { get; set; }

    /// <summary>The machine may be switched off, asleep or away (a notebook): while its agent is silent the host is "offline" -
    /// its connection sensor shows that instead of an error and raises no alert (<see cref="AgentOffline"/>).</summary>
    public bool MayGoOffline { get; set; }

    /// <summary>With <see cref="MayGoOffline"/>: warn once it has been offline this many days (0 = never).</summary>
    public int OfflineWarnDays { get; set; } = AgentOffline.DefaultWarnDays;

    public AgentIdentity Clone() => new()
    {
        ProbeId = ProbeId,
        Token = Token,
        EnrolledUtc = EnrolledUtc,
        MachineId = MachineId,
        MayGoOffline = MayGoOffline,
        OfflineWarnDays = OfflineWarnDays
    };
}
