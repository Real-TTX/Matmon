using Matmon.Core.Domain;

namespace Matmon.Tests;

public class NotificationThrottleTests
{
    private static readonly Guid Rule = Guid.NewGuid();
    private static readonly Guid Element = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoCooldown_configured_never_suppresses()
    {
        var throttle = new NotificationThrottle();
        throttle.MarkRaised(Rule, Element, T0);

        Assert.False(throttle.IsWithinCooldown(Rule, Element, cooldownMinutes: null, threshold: 1, T0.AddSeconds(1)));
        Assert.False(throttle.IsWithinCooldown(Rule, Element, cooldownMinutes: 0, threshold: 1, T0.AddSeconds(1)));
    }

    [Fact]
    public void WithinCooldown_suppresses_until_window_elapses()
    {
        var throttle = new NotificationThrottle();
        throttle.MarkRaised(Rule, Element, T0);

        Assert.True(throttle.IsWithinCooldown(Rule, Element, cooldownMinutes: 15, threshold: 1, T0.AddMinutes(5)));   // inside
        Assert.True(throttle.IsWithinCooldown(Rule, Element, cooldownMinutes: 15, threshold: 1, T0.AddMinutes(14)));  // still inside
        Assert.False(throttle.IsWithinCooldown(Rule, Element, cooldownMinutes: 15, threshold: 1, T0.AddMinutes(15))); // window elapsed
    }

    [Fact]
    public void Cooldown_is_scoped_per_rule_and_element()
    {
        var throttle = new NotificationThrottle();
        var otherElement = Guid.NewGuid();
        var otherRule = Guid.NewGuid();
        throttle.MarkRaised(Rule, Element, T0);

        Assert.True(throttle.IsWithinCooldown(Rule, Element, 15, 1, T0.AddMinutes(1)));
        Assert.False(throttle.IsWithinCooldown(Rule, otherElement, 15, 1, T0.AddMinutes(1)));
        Assert.False(throttle.IsWithinCooldown(otherRule, Element, 15, 1, T0.AddMinutes(1)));
    }

    [Fact]
    public void NullOrZeroThreshold_behaves_as_one_per_window()
    {
        var throttle = new NotificationThrottle();
        throttle.MarkRaised(Rule, Element, T0);

        // A single send already fills a threshold of "1" → both null and 0 map to 1.
        Assert.True(throttle.IsWithinCooldown(Rule, Element, 15, threshold: null, T0.AddMinutes(5)));
        Assert.True(throttle.IsWithinCooldown(Rule, Element, 15, threshold: 0, T0.AddMinutes(5)));
    }

    [Fact]
    public void Threshold_allows_up_to_N_sends_per_window_then_suppresses()
    {
        var throttle = new NotificationThrottle();

        // Window 15m, threshold 3: the first three sends go through, the fourth is suppressed.
        Assert.False(throttle.IsWithinCooldown(Rule, Element, 15, 3, T0));            // 0 sent
        throttle.MarkRaised(Rule, Element, T0);
        Assert.False(throttle.IsWithinCooldown(Rule, Element, 15, 3, T0.AddMinutes(1))); // 1 sent
        throttle.MarkRaised(Rule, Element, T0.AddMinutes(1));
        Assert.False(throttle.IsWithinCooldown(Rule, Element, 15, 3, T0.AddMinutes(2))); // 2 sent
        throttle.MarkRaised(Rule, Element, T0.AddMinutes(2));
        Assert.True(throttle.IsWithinCooldown(Rule, Element, 15, 3, T0.AddMinutes(3)));   // 3 within window → suppress
    }

    [Fact]
    public void Threshold_window_rolls_off_old_sends_so_dispatch_resumes()
    {
        var throttle = new NotificationThrottle();

        // Window 15m, threshold 2: two sends fill the budget...
        throttle.MarkRaised(Rule, Element, T0);
        throttle.MarkRaised(Rule, Element, T0.AddMinutes(1));
        Assert.True(throttle.IsWithinCooldown(Rule, Element, 15, 2, T0.AddMinutes(2)));   // both inside window

        // ...but once both fall outside the 15m window, sending is allowed again.
        Assert.False(throttle.IsWithinCooldown(Rule, Element, 15, 2, T0.AddMinutes(16)));
    }

    [Fact]
    public void Recovery_only_eligible_after_a_raise_and_once_per_episode()
    {
        var throttle = new NotificationThrottle();

        // No raise yet → no recovery mail.
        Assert.False(throttle.IsEpisodeActive(Rule, Element));

        throttle.MarkRaised(Rule, Element, T0);
        Assert.True(throttle.IsEpisodeActive(Rule, Element));   // raise sent → recovery eligible

        throttle.MarkRecovered(Rule, Element);
        Assert.False(throttle.IsEpisodeActive(Rule, Element));  // episode closed → no duplicate recovery
    }

    [Fact]
    public void Flap_within_cooldown_suppresses_the_reraise_so_its_recovery_is_also_suppressed()
    {
        var throttle = new NotificationThrottle();

        // First down: raise is sent, episode opens.
        Assert.False(throttle.IsWithinCooldown(Rule, Element, 15, 1, T0));
        throttle.MarkRaised(Rule, Element, T0);

        // First up: recovery is sent, episode closes.
        Assert.True(throttle.IsEpisodeActive(Rule, Element));
        throttle.MarkRecovered(Rule, Element);

        // Second down 2 minutes later (flap): cooldown suppresses the re-raise, so no episode opens...
        Assert.True(throttle.IsWithinCooldown(Rule, Element, 15, 1, T0.AddMinutes(2)));

        // ...and therefore the following up produces no recovery mail either.
        Assert.False(throttle.IsEpisodeActive(Rule, Element));
    }
}
