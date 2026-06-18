namespace Matmon.Host.Services;

public sealed record ProbeHeartbeatRequest(
    string ProbeId,
    string ProbeName,
    string? ProbeToken = null,
    string? Message = null,
    string? AgentVersion = null,
    string? OperatingSystem = null,
    string? Host = null,
    IReadOnlyList<string>? Networks = null);
