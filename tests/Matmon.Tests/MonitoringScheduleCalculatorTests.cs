using Matmon.Core.Domain;

namespace Matmon.Tests;

// These tests deliberately cover only the timezone-independent paths
// (interval fallback + "Every" schedule). Daily/Weekly/Monthly use the local
// time zone and would be non-deterministic across machines - covered later.
public class MonitoringScheduleCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsDue_interval_fallback_respects_elapsed_time()
    {
        var settings = new MonitoringSettings();
        var interval = TimeSpan.FromSeconds(60);

        Assert.True(MonitoringScheduleCalculator.IsDue(settings, null, Now, interval));
        Assert.False(MonitoringScheduleCalculator.IsDue(settings, Now - TimeSpan.FromSeconds(30), Now, interval));
        Assert.True(MonitoringScheduleCalculator.IsDue(settings, Now - TimeSpan.FromSeconds(90), Now, interval));
    }

    [Fact]
    public void IsDue_uses_settings_polling_interval_over_fallback()
    {
        var settings = new MonitoringSettings { PollingInterval = TimeSpan.FromSeconds(10) };
        var fallback = TimeSpan.FromHours(1);

        Assert.True(MonitoringScheduleCalculator.IsDue(settings, Now - TimeSpan.FromSeconds(15), Now, fallback));
    }

    [Fact]
    public void IsDue_every_schedule_respects_seconds()
    {
        var settings = new MonitoringSettings
        {
            PollingSchedule = new MonitoringSchedule
            {
                Mode = MonitoringScheduleMode.Every,
                EverySeconds = 60
            }
        };

        Assert.False(MonitoringScheduleCalculator.IsDue(settings, Now - TimeSpan.FromSeconds(30), Now, TimeSpan.Zero));
        Assert.True(MonitoringScheduleCalculator.IsDue(settings, Now - TimeSpan.FromSeconds(90), Now, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextDueUtc_interval_returns_now_when_never_run()
    {
        var settings = new MonitoringSettings();

        Assert.Equal(Now, MonitoringScheduleCalculator.GetNextDueUtc(settings, null, Now, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void GetNextDueUtc_interval_returns_last_plus_interval_when_future()
    {
        var settings = new MonitoringSettings();
        var lastRun = Now - TimeSpan.FromSeconds(10);

        var next = MonitoringScheduleCalculator.GetNextDueUtc(settings, lastRun, Now, TimeSpan.FromSeconds(60));

        Assert.Equal(lastRun + TimeSpan.FromSeconds(60), next);
    }

    [Fact]
    public void GetNextDueUtc_interval_clamps_to_now_when_overdue()
    {
        var settings = new MonitoringSettings();
        var lastRun = Now - TimeSpan.FromSeconds(120);

        var next = MonitoringScheduleCalculator.GetNextDueUtc(settings, lastRun, Now, TimeSpan.FromSeconds(60));

        Assert.Equal(Now, next);
    }

    [Fact]
    public void IsDue_every_schedule_never_polls_below_the_15s_minimum()
    {
        var settings = new MonitoringSettings
        {
            PollingSchedule = new MonitoringSchedule { Mode = MonitoringScheduleMode.Every, EverySeconds = 5 }
        };

        // 'every 5s' is clamped up to the 15s floor.
        Assert.False(MonitoringScheduleCalculator.IsDue(settings, Now - TimeSpan.FromSeconds(10), Now, TimeSpan.Zero));
        Assert.True(MonitoringScheduleCalculator.IsDue(settings, Now - TimeSpan.FromSeconds(20), Now, TimeSpan.Zero));
    }

    [Fact]
    public void IsDue_clamps_a_sub_minimum_polling_interval()
    {
        var settings = new MonitoringSettings { PollingInterval = TimeSpan.FromSeconds(2) };

        Assert.False(MonitoringScheduleCalculator.IsDue(settings, Now - TimeSpan.FromSeconds(10), Now, TimeSpan.Zero));
        Assert.True(MonitoringScheduleCalculator.IsDue(settings, Now - TimeSpan.FromSeconds(20), Now, TimeSpan.Zero));
    }
}
