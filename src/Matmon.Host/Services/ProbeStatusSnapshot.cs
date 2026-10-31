namespace Matmon.Host.Services;

public sealed record ProbeStatusSnapshot(
    string ProbeId,
    string ProbeName,
    DateTimeOffset LastSeenUtc,
    string State,
    string? Message = null,
    string? OperatingSystem = null,
    string? Host = null,
    IReadOnlyList<string>? Networks = null,
    string? Version = null,
    /// <summary>Set when two processes appear to share this probe id (see InMemoryProbeRegistry.DetectDuplicate).</summary>
    string? DuplicateWarning = null,
    string? UpdateStatus = null,
    /// <summary>The interval the probe reported it beats at (null for an older probe - then the primary's own).</summary>
    int? HeartbeatIntervalSeconds = null,
    /// <summary>A Docker probe's self-update report (null for an agent and an older probe).</summary>
    Matmon.Probe.ProbeSelfUpdateReport? SelfUpdate = null,
    /// <summary>An agent behind a remote probe: the probe id that relayed its last heartbeat (null = it reached the primary itself).</summary>
    string? RelayProbeId = null);
