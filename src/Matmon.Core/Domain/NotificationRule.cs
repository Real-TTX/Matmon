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

    public int? CooldownMinutes { get; set; }
}
