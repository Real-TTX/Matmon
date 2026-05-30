using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed record TelemetrySamplePoint(
    DateTimeOffset TimestampUtc,
    double Value,
    SensorState State);
