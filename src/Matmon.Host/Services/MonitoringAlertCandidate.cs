using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>A problem the dashboard sees. <paramref name="ObservedUtc"/>: when the check that showed it ran - the alert's
/// first / last seen are that time, not the moment someone opened the dashboard (null = no check behind it, e.g. a
/// probe's connection, which is judged now).</summary>
public sealed record MonitoringAlertCandidate(
    Guid ElementId,
    MonitoringElementKind ElementKind,
    string ElementName,
    string ElementPath,
    SensorState State,
    string Message,
    DateTimeOffset? ObservedUtc = null);
