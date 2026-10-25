namespace Matmon.Probe;

public sealed record ProbeHeartbeatRequest(
    string ProbeId,
    string ProbeName,
    string? ProbeToken = null,
    string? Message = null,
    string? AgentVersion = null,
    string? OperatingSystem = null,
    string? Host = null,
    IReadOnlyList<string>? Networks = null,
    // What the agent's auto-update last did ("updated from X", "rolled back to X: reason"). Null for a
    // Docker probe, which is updated by replacing its container.
    string? UpdateStatus = null,
    // How often THIS probe beats. An agent beats every 30 s whatever the primary is set to, so the primary judges a
    // probe's heartbeat age and its duplicate check by this value (its own interval only for an older probe).
    int? HeartbeatIntervalSeconds = null);
