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
