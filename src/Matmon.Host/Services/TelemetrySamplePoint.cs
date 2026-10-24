using Matmon.Core.Domain;

namespace Matmon.Host.Services;

// HasReading is false for a check that produced no value (a timeout, a refused login): its Value is a placeholder 0 and
// must not be drawn as a reading. A Critical check WITH a value (a disk at 97 %) has one.
public sealed record TelemetrySamplePoint(
    DateTimeOffset TimestampUtc,
    double Value,
    SensorState State,
    bool HasReading = true);
