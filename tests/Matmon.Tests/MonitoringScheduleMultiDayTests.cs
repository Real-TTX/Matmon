using Matmon.Core.Domain;

namespace Matmon.Tests;

// Weekly schedules can fire on several weekdays (e.g. Monday + Thursday at 05:00). These cover the
// timezone-independent model surface; the occurrence maths reuse the proven single-day helpers.
public class MonitoringScheduleMultiDayTests
{
    private static MonitoringSchedule Weekly(params DayOfWeek[] days) =>
        new() { Mode = MonitoringScheduleMode.Weekly, DaysOfWeek = [.. days], TimeOfDay = new TimeSpan(5, 0, 0) };

    [Fact]
    public void ResolveDays_returns_configured_days_sorted_and_deduped()
    {
        var schedule = Weekly(DayOfWeek.Thursday, DayOfWeek.Monday, DayOfWeek.Monday);

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Thursday], schedule.ResolveDays());
    }

    [Fact]
    public void ResolveDays_falls_back_to_the_legacy_single_day()
    {
        var schedule = new MonitoringSchedule { Mode = MonitoringScheduleMode.Weekly, DayOfWeek = DayOfWeek.Friday };

        Assert.Equal([DayOfWeek.Friday], schedule.ResolveDays());
    }

    [Fact]
    public void ResolveDays_defaults_to_monday_when_nothing_is_set()
    {
        var schedule = new MonitoringSchedule { Mode = MonitoringScheduleMode.Weekly };

        Assert.Equal([DayOfWeek.Monday], schedule.ResolveDays());
    }

    [Fact]
    public void ContentEquals_ignores_day_order()
    {
        Assert.True(Weekly(DayOfWeek.Monday, DayOfWeek.Thursday).ContentEquals(Weekly(DayOfWeek.Thursday, DayOfWeek.Monday)));
        Assert.False(Weekly(DayOfWeek.Monday, DayOfWeek.Thursday).ContentEquals(Weekly(DayOfWeek.Monday)));
    }

    [Fact]
    public void Summary_lists_all_weekdays()
    {
        var summary = Weekly(DayOfWeek.Monday, DayOfWeek.Thursday).Summary();

        Assert.Contains("Monday", summary);
        Assert.Contains("Thursday", summary);
    }

    [Fact]
    public void Clone_copies_the_days_independently()
    {
        var schedule = Weekly(DayOfWeek.Monday, DayOfWeek.Thursday);

        var clone = schedule.Clone();
        clone.DaysOfWeek.Add(DayOfWeek.Sunday);

        Assert.Equal(2, schedule.DaysOfWeek.Count);
        Assert.Equal(3, clone.DaysOfWeek.Count);
    }
}
