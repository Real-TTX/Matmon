namespace Matmon.Core.Domain;

/// <summary>
/// Per-(rule, element) anti-spam / flap-suppression policy for notification dispatch. Not thread-safe by
/// design — it is owned and used only by the single-threaded dispatch loop. Check with
/// <see cref="IsWithinCooldown"/> / <see cref="IsEpisodeActive"/>, then commit with
/// <see cref="MarkRaised"/> / <see cref="MarkRecovered"/> only once a mail is actually queued (so a rule
/// with no sender/recipient doesn't consume the cooldown or open an episode).
/// </summary>
public sealed class NotificationThrottle
{
    private readonly Dictionary<(Guid Rule, Guid Element), DateTimeOffset> _lastRaisedUtc = [];
    private readonly HashSet<(Guid Rule, Guid Element)> _active = [];

    /// <summary>True if a raised mail for this rule+element was sent within the cooldown window (→ suppress).</summary>
    public bool IsWithinCooldown(Guid ruleId, Guid elementId, int? cooldownMinutes, DateTimeOffset now)
    {
        return cooldownMinutes is > 0
            && _lastRaisedUtc.TryGetValue((ruleId, elementId), out var last)
            && now - last < TimeSpan.FromMinutes(cooldownMinutes.Value);
    }

    /// <summary>Records that a raised mail was queued — starts the alarmed episode and the cooldown clock.</summary>
    public void MarkRaised(Guid ruleId, Guid elementId, DateTimeOffset now)
    {
        var key = (ruleId, elementId);
        _lastRaisedUtc[key] = now;
        _active.Add(key);
    }

    /// <summary>True if this rule+element has an alarmed episode a raise was sent for (→ eligible for a recovery mail).</summary>
    public bool IsEpisodeActive(Guid ruleId, Guid elementId) => _active.Contains((ruleId, elementId));

    /// <summary>Ends the alarmed episode (call once a recovery mail is queued) so later flaps don't re-send it.</summary>
    public void MarkRecovered(Guid ruleId, Guid elementId) => _active.Remove((ruleId, elementId));
}
