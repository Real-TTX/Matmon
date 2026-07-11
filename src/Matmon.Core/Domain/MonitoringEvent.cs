namespace Matmon.Core.Domain;

public enum MonitoringEventKind
{
    Info = 0,
    Created = 1,
    Updated = 2,
    Moved = 3,
    Deleted = 4,
    StateChanged = 5,
    AlertRaised = 6,
    AlertAcknowledged = 7,
    AlertResolved = 8,
    Paused = 9,
    Resumed = 10,
    AlertMuted = 11,
    AlertUnmuted = 12
}

public sealed class MonitoringEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset TimestampUtc { get; set; }

    public MonitoringEventKind Kind { get; set; } = MonitoringEventKind.Info;

    public Guid? ElementId { get; set; }

    public MonitoringElementKind? ElementKind { get; set; }

    public string ElementName { get; set; } = string.Empty;

    public string ElementPath { get; set; } = string.Empty;

    public SensorState? State { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? Details { get; set; }
}
