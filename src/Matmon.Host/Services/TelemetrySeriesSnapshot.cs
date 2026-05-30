using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed record TelemetrySeriesSnapshot(
    string Key,
    Guid SensorId,
    string Path,
    string SensorTypeLabel,
    string Target,
    string Title,
    string Description,
    string Unit,
    SensorState CurrentState,
    double? CurrentValue,
    string LineColor,
    bool IsHighlighted,
    IReadOnlyList<TelemetrySamplePoint> Points,
    string StateKey,
    string StateLabel,
    string StateColor);
