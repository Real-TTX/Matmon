using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed record MonitoringAlertCandidate(
    Guid ElementId,
    MonitoringElementKind ElementKind,
    string ElementName,
    string ElementPath,
    SensorState State,
    string Message);
