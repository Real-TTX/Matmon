namespace Matmon.Probe;

/// <summary>
/// What <c>matmon-agent enroll</c> posts to <c>POST /api/agents/enroll</c>: the one-time code plus enough
/// about the machine to name the probe it becomes when the admin did not.
/// </summary>
public sealed record AgentEnrollRequest(
    string Code,
    string? HostName = null,
    string? OperatingSystem = null,
    string? AgentVersion = null);

/// <summary>The identity the agent runs under from then on - written to its config file, never shown.</summary>
public sealed record AgentEnrollResponse(string ProbeId, string ProbeToken, string ProbeName);
