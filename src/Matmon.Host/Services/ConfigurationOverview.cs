namespace Matmon.Host.Services;

public sealed record ConfigurationOverview(
    string Mode,
    string? ProbeId,
    string? ProbeName,
    string? MasterUrl,
    int HeartbeatIntervalSeconds,
    string WorkspacePath,
    string AuthUsername,
    string AuthPassword,
    string DockerMasterSnippet,
    string DockerSlaveSnippet,
    string AppSettingsSnippet,
    StorageOverview Storage,
    IReadOnlyList<SystemProbeOverview> Probes,
    SlaveProbeRuntimeSnapshot SlaveRuntime);

public sealed record SystemProbeOverview(
    Guid ElementId,
    string ProbeId,
    string Name,
    string Role,
    string State,
    string Message,
    DateTimeOffset? LastSeenUtc,
    string? EnrollmentToken,
    int SensorCount);
