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
    string? UpdateStatus = null);
