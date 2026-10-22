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

    /// <summary>
    /// When the underlying condition cleared while the alert was still unacknowledged.
    /// The alert stays <see cref="IsActive"/> (Alerta-style: it persists until an
    /// operator acknowledges it), but this records that it has recovered. Cleared
    /// again if the condition re-alarms.
    /// </summary>
    public DateTimeOffset? RecoveredUtc { get; set; }

    /// <summary>The escalation steps already sent for this alert - persisted with the alert, so a restart neither forgets
    /// an escalation nor sends it twice.</summary>
    public List<AlertEscalationMark> Escalations { get; set; } = [];

    public bool IsActive => ResolvedUtc is null;

    public bool IsAcknowledged => AcknowledgedUtc is not null;

    public bool IsRecovered => RecoveredUtc is not null;
}

/// <summary>Escalation step <see cref="Step"/> (0-based) of rule <see cref="RuleId"/> went out at <see cref="SentUtc"/>.</summary>
public sealed class AlertEscalationMark
{
    public Guid RuleId { get; set; }

    public int Step { get; set; }

    public DateTimeOffset SentUtc { get; set; }
}
