using Matmon.Core.Domain;
using Matmon.Host.Services;

namespace Matmon.Tests;

public sealed class MailHealthDecisionTests
{
    private const long Now = 1_750_000_000_000; // fixed "now" in epoch ms
    private const int Tolerance = 15;            // minutes

    [Fact]
    public void First_run_with_no_prior_probe_is_Unknown_and_sends_a_baseline()
    {
        var decision = MailHealthSensorExecutor.Decide(previousProbeEpochMs: null, delivered: false, Now, Tolerance);

        Assert.Equal(SensorState.Unknown, decision.State);
        Assert.True(decision.SendProbe);
        Assert.Null(decision.PendingAgeSeconds);
    }

    [Fact]
    public void First_run_ignores_a_stale_delivered_flag()
    {
        // With no outstanding probe there is nothing to have delivered - stays a baseline.
        var decision = MailHealthSensorExecutor.Decide(previousProbeEpochMs: null, delivered: true, Now, Tolerance);

        Assert.Equal(SensorState.Unknown, decision.State);
        Assert.True(decision.SendProbe);
    }

    [Fact]
    public void Previous_probe_delivered_is_Healthy_and_sends_the_next_probe()
    {
        var sentTwoMinutesAgo = Now - 2 * 60_000;

        var decision = MailHealthSensorExecutor.Decide(sentTwoMinutesAgo, delivered: true, Now, Tolerance);

        Assert.Equal(SensorState.Healthy, decision.State);
        Assert.True(decision.SendProbe);
    }

    [Fact]
    public void Not_yet_delivered_within_tolerance_waits_without_resending()
    {
        var sentFiveMinutesAgo = Now - 5 * 60_000;

        var decision = MailHealthSensorExecutor.Decide(sentFiveMinutesAgo, delivered: false, Now, Tolerance);

        Assert.Equal(SensorState.Warning, decision.State);
        Assert.False(decision.SendProbe); // no pile-up: keep waiting on the outstanding probe
        Assert.Equal(300, decision.PendingAgeSeconds);
    }

    [Fact]
    public void At_exactly_the_tolerance_boundary_still_waits()
    {
        var sentExactlyToleranceAgo = Now - Tolerance * 60_000;

        var decision = MailHealthSensorExecutor.Decide(sentExactlyToleranceAgo, delivered: false, Now, Tolerance);

        Assert.Equal(SensorState.Warning, decision.State);
        Assert.False(decision.SendProbe);
    }

    [Fact]
    public void Not_delivered_beyond_tolerance_is_Critical_and_resends()
    {
        var sentSixteenMinutesAgo = Now - 16 * 60_000;

        var decision = MailHealthSensorExecutor.Decide(sentSixteenMinutesAgo, delivered: false, Now, Tolerance);

        Assert.Equal(SensorState.Critical, decision.State);
        Assert.True(decision.SendProbe); // reset: give up on the lost probe and send a fresh one
        Assert.Equal(960, decision.PendingAgeSeconds);
    }

    [Fact]
    public void Tolerance_is_floored_so_a_zero_never_makes_everything_critical()
    {
        var sentThirtySecondsAgo = Now - 30_000;

        // A misconfigured 0-minute tolerance is clamped to 1 min, so a 30s-old probe still waits.
        var decision = MailHealthSensorExecutor.Decide(sentThirtySecondsAgo, delivered: false, Now, toleranceMinutes: 0);

        Assert.Equal(SensorState.Warning, decision.State);
        Assert.False(decision.SendProbe);
    }
}
