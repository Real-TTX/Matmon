namespace Matmon.Core.Domain;

/// <summary>
/// Per-(rule, element) anti-spam / flap-suppression policy for notification dispatch. Not thread-safe by
/// design — it is owned and used only by the single-threaded dispatch loop. Check with
/// <see cref="IsWithinCooldown"/> / <see cref="IsEpisodeActive"/>, then commit with
/// <see cref="MarkRaised"/> / <see cref="MarkRecovered"/> only once a mail is actually queued (so a rule
/// with no sender/recipient doesn't consume the budget or open an episode).
///
/// The suppression is a rolling rate limit: at most <c>threshold</c> "raised" mails per (rule, element)
/// within the <c>cooldownMinutes</c> window. A null/≤0 threshold is treated as 1, which reduces exactly to
/// the classic "one mail per window" cooldown; a null/≤0 window disables suppression entirely.
/// </summary>
public sealed class NotificationThrottle
{
    // Cap the retained timestamp tail per key so a long-lived, always-re-raising alarm can't grow unbounded.
    // Thresholds are single/low-double digits in practice, so this is far above any real limit.
    private const int MaxRetainedStamps = 256;

    private readonly Dictionary<(Guid Rule, Guid Element), List<DateTimeOffset>> _raisedUtc = [];
    private readonly HashSet<(Guid Rule, Guid Element)> _active = [];

    /// <summary>
    /// True if sending a raised mail now would exceed the rate limit for this rule+element: at least
    /// <paramref name="threshold"/> raised mails were already sent within the last
    /// <paramref name="cooldownMinutes"/> minutes (→ suppress).
    /// </summary>
    public bool IsWithinCooldown(Guid ruleId, Guid elementId, int? cooldownMinutes, int? threshold, DateTimeOffset now)
    {
        if (cooldownMinutes is not > 0)
        {
            return false;
        }

        if (!_raisedUtc.TryGetValue((ruleId, elementId), out var stamps) || stamps.Count == 0)
        {
            return false;
        }

        var limit = threshold is > 0 ? threshold.Value : 1;
        var windowStart = now - TimeSpan.FromMinutes(cooldownMinutes.Value);

        var recent = 0;
        for (var i = 0; i < stamps.Count; i++)
        {
            if (stamps[i] > windowStart)
            {
                recent++;
            }
        }

        return recent >= limit;
    }

    /// <summary>Records that a raised mail was queued — counts toward the rate limit and starts/keeps the episode.</summary>
    public void MarkRaised(Guid ruleId, Guid elementId, DateTimeOffset now)
    {
        var key = (ruleId, elementId);
        if (!_raisedUtc.TryGetValue(key, out var stamps))
        {
            stamps = [];
            _raisedUtc[key] = stamps;
        }

        stamps.Add(now);
        if (stamps.Count > MaxRetainedStamps)
        {
            stamps.RemoveRange(0, stamps.Count - MaxRetainedStamps);
        }

        _active.Add(key);
    }

    /// <summary>True if this rule+element has an alarmed episode a raise was sent for (→ eligible for a recovery mail).</summary>
    public bool IsEpisodeActive(Guid ruleId, Guid elementId) => _active.Contains((ruleId, elementId));

    /// <summary>Ends the alarmed episode (call once a recovery mail is queued) so later flaps don't re-send it.</summary>
    public void MarkRecovered(Guid ruleId, Guid elementId) => _active.Remove((ruleId, elementId));
}
