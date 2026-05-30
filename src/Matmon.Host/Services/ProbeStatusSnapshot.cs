namespace Matmon.Host.Services;

public sealed record ProbeStatusSnapshot(
    string ProbeId,
    string ProbeName,
    DateTimeOffset LastSeenUtc,
    string State,
    string? Message = null);
