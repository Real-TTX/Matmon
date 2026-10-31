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

    public AgentIdentity Clone() => new()
    {
        ProbeId = ProbeId,
        Token = Token,
        EnrolledUtc = EnrolledUtc
    };
}
