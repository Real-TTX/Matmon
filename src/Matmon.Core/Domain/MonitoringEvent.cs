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
    AlertUnmuted = 12,

    /// <summary>The condition behind an UNACKNOWLEDGED alert cleared - the alert stays open until someone acknowledges it
    /// (an acknowledged one is resolved instead, <see cref="AlertResolved"/>).</summary>
    AlertRecovered = 13,

    /// <summary>A recovered (not yet acknowledged) alert fired again - the same episode re-opened.</summary>
    AlertReopened = 14
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

    /// <summary>The user who did it (pause, resume, acknowledge, mute, un-mute) - null for what the monitor did by itself
    /// and for events recorded before the field existed.</summary>
    public string? Actor { get; set; }
}
