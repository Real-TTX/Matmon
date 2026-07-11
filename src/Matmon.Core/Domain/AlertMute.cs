namespace Matmon.Core.Domain;

/// <summary>
/// Suppresses alerting for a single element. While a mute is active, incoming problem observations do NOT
/// raise or re-open an alert for that element (and so fire no notifications) - the operator "worked it off"
/// with <c>Mute</c> = acknowledge + stop re-opening. A null <see cref="MutedUntilUtc"/> is permanent (until
/// the operator un-mutes); otherwise the mute auto-expires and alerting resumes on its own.
/// </summary>
public sealed class AlertMute
{
    public Guid ElementId { get; set; }

    public DateTimeOffset MutedAtUtc { get; set; }

    /// <summary>When the mute lifts on its own. Null = permanent (only a manual un-mute lifts it).</summary>
    public DateTimeOffset? MutedUntilUtc { get; set; }

    public string? MutedBy { get; set; }

    public bool IsPermanent => MutedUntilUtc is null;

    public bool IsActiveAt(DateTimeOffset now) => MutedUntilUtc is null || MutedUntilUtc.Value > now;
}
