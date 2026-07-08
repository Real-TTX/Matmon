namespace Matmon.Core.Domain;

public sealed class NotificationRule
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public Guid? SenderId { get; set; }

    public Guid? ReceiverId { get; set; }

    public NotificationChannelKind ChannelKind { get; set; } = NotificationChannelKind.Email;

    public string Recipient { get; set; } = string.Empty;

    public Guid? TargetElementId { get; set; }

    public bool IncludeDescendants { get; set; } = true;

    public List<SensorState> TriggerStates { get; set; } = [];

    /// <summary>
    /// Rolling suppression window in minutes. Together with <see cref="Threshold"/> this is a rate limit:
    /// at most <see cref="Threshold"/> "raised" mails per (rule, element) within this window. Null / ≤0 = no
    /// suppression.
    /// </summary>
    public int? CooldownMinutes { get; set; }

    /// <summary>
    /// Max number of "raised" mails allowed per <see cref="CooldownMinutes"/> window (per rule+element).
    /// Null / ≤0 is treated as 1 - i.e. the classic "one mail per window" cooldown (backward compatible).
    /// </summary>
    public int? Threshold { get; set; }

    public string SubjectTemplate { get; set; } = string.Empty;

    public string TextTemplate { get; set; } = string.Empty;

    public string HtmlTemplate { get; set; } = string.Empty;
}
