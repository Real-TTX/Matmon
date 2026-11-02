namespace Matmon.Probe;

/// <summary>
/// What <c>matmon-agent enroll</c> posts to <c>POST /api/agents/enroll</c>: a single-use code or a rollout key (one
/// starting with <c>mmr_</c>, for many machines), plus enough about the machine to name the host it becomes and to
/// recognise it when it enrols again.
/// </summary>
public sealed record AgentEnrollRequest(
    string Code,
    string? HostName = null,
    string? OperatingSystem = null,
    string? AgentVersion = null,
    string? MachineId = null);

/// <summary>
/// The identity the agent runs under from then on - written to its config file, never shown. <see cref="Pending"/>: it
/// enrolled with a rollout key and an admin still has to approve it; the identity starts working then, nothing to do on
/// the machine.
/// </summary>
public sealed record AgentEnrollResponse(string ProbeId, string ProbeToken, string ProbeName, bool Pending = false);
