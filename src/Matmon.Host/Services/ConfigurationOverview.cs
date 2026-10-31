namespace Matmon.Host.Services;

public sealed record ConfigurationOverview(
    string Mode,
    string? ProbeId,
    string? ProbeName,
    string? MasterUrl,
    int HeartbeatIntervalSeconds,
    int PollingWorkers,
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
    int SensorCount,
    string? OperatingSystem = null,
    string? Host = null,
    IReadOnlyList<string>? Networks = null,
    string? Version = null,
    string? DuplicateWarning = null,
    bool IsAgent = false,
    string? UpdateStatus = null,
    Guid? BoundHostId = null,
    string? BoundHostName = null,
    // A Docker probe's self-update report from its heartbeat (null for the primary, an agent and an older probe).
    Matmon.Probe.ProbeSelfUpdateReport? SelfUpdate = null,
    // An agent: the probe it talks through (the nearest probe above its host - the primary or a remote probe that relays).
    Guid? ViaProbeElementId = null,
    string? ViaProbeName = null,
    // An agent: the remote probe its last heartbeat actually came through (null = straight to the primary).
    string? RelayedByName = null);
