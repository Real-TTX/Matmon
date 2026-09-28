namespace Matmon.Probe;

/// <summary>The discovery-job exchange a probe has with its primary: which networks to scan, and what
/// it found. Split out of the primary-only DiscoveryJobStore - the probe side needs the wire types, not the
/// store that keeps the jobs.</summary>
public sealed record ProbeDiscoveryJobAssignmentsResponse(
    IReadOnlyList<ProbeDiscoveryJobAssignment> Jobs);

public sealed record ProbeDiscoveryJobAssignment(
    Guid JobId,
    string Network,
    NetworkDiscoveryOptions Options);

public sealed record ProbeDiscoveryJobResultBatch(
    IReadOnlyList<ProbeDiscoveryJobResult> Results);

public sealed record ProbeDiscoveryJobResult(
    Guid JobId,
    IReadOnlyList<NetworkDiscoveryResult> Hosts,
    string? ErrorMessage,
    bool IsComplete,
    int? ScannedHosts = null,
    int? TotalHosts = null);

public sealed record ProbeDiscoveryJobResultPostResponse(
    int Recorded,
    bool Cancelled);
