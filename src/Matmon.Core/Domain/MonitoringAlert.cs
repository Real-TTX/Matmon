namespace Matmon.Core.Domain;

public sealed class MonitoringAlert
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid ElementId { get; set; }

    public MonitoringElementKind ElementKind { get; set; }

    public string ElementName { get; set; } = string.Empty;

    public string ElementPath { get; set; } = string.Empty;

    public SensorState State { get; set; } = SensorState.Unknown;

    public string Message { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    public DateTimeOffset? AcknowledgedUtc { get; set; }

    public string? AcknowledgedBy { get; set; }

    public SensorState? AcknowledgedState { get; set; }

    public DateTimeOffset? ResolvedUtc { get; set; }

    public bool IsActive => ResolvedUtc is null;

    public bool IsAcknowledged => AcknowledgedUtc is not null;
}
