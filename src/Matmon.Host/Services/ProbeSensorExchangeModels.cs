using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed record ProbeSensorAssignmentsResponse(
    string ProbeId,
    string ProbeName,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<ProbeSensorAssignment> Sensors);

public sealed record ProbeSensorAssignment(
    Guid SensorId,
    string Name,
    string Path,
    string SensorTypeKey,
    string Target,
    bool IsPaused,
    MonitoringSettings Settings,
    DateTimeOffset? LastObservationUtc,
    // The full previous observation, so stateful sensors (Mail Health) can correlate runs on a
    // remote probe. Nullable + defaulted so older probes/payloads stay compatible.
    SensorObservation? LastObservation = null);

public sealed record ProbeSensorObservationBatch(
    IReadOnlyList<ProbeSensorObservationReport> Observations);

public sealed record ProbeSensorObservationReport(
    Guid SensorId,
    SensorExecutionResult Result,
    DateTimeOffset TimestampUtc);

// --- On-demand run jobs -----------------------------------------------------
// A pull-based mechanism (mirroring the discovery-job flow) so that an on-demand run - "Run now",
// "Test", "Run subtree", "SNMP discover" - for a sensor owned by a REMOTE probe actually executes
// ON THAT PROBE, not in-process on the primary. The job carries everything the probe needs to run
// standalone (target + fully-resolved settings with credential values already inlined).

public enum ProbeRunJobKind
{
    // Run a single sensor (optionally recording the observation on the primary).
    Sensor,
    // Perform a live SNMP walk for the sensor-editor's OID discovery (never recorded).
    SnmpDiscover
}

public sealed record ProbeRunJobAssignment(
    Guid JobId,
    // Null for a not-yet-saved sensor (a "Test" of a new sensor or a new-sensor SNMP discover).
    Guid? SensorId,
    string SensorTypeKey,
    string Target,
    MonitoringSettings Settings,
    bool RecordObservation,
    ProbeRunJobKind Kind);

public sealed record ProbeRunJobAssignmentsResponse(
    IReadOnlyList<ProbeRunJobAssignment> Jobs);

public sealed record ProbeRunJobResult(
    Guid JobId,
    // Set for a Sensor run; null for an SnmpDiscover.
    SensorExecutionResult? Result,
    // Set for an SnmpDiscover run; null for a Sensor run.
    IReadOnlyList<SnmpDiscoveryItem>? DiscoveredOids,
    string? Error,
    DateTimeOffset TimestampUtc);

public sealed record ProbeRunJobResultBatch(
    IReadOnlyList<ProbeRunJobResult> Results);

// The UI poll shape for GET /api/run-jobs/{jobId} (a sensor test's result, or discovered OIDs).
public sealed record RunJobStatusResponse(
    Guid JobId,
    string Status,
    bool IsComplete,
    SensorExecutionResult? Result,
    IReadOnlyList<SnmpDiscoveryItem>? DiscoveredOids,
    string? Error);

public static class ProbeRunJobParameters
{
    // The DTO has no dedicated field for the SNMP walk root OID, so an SnmpDiscover job smuggles it
    // through Settings.Parameters. Kept off the persisted sensor parameters (transient job only).
    public const string SnmpDiscoverRootOid = "snmp.discoverRootOid";
}
