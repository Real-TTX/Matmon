using System.Globalization;

namespace Matmon.Core.Domain;

public enum MonitoringScheduleMode
{
    Every = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3
}

public sealed class MonitoringSchedule
{
    public MonitoringScheduleMode Mode { get; set; } = MonitoringScheduleMode.Every;

    public int? EverySeconds { get; set; }

    /// <summary>Legacy single weekday - kept for older data; superseded by <see cref="DaysOfWeek"/>.</summary>
    public DayOfWeek? DayOfWeek { get; set; }

    /// <summary>Weekdays a Weekly schedule fires on (e.g. Monday + Thursday). Empty falls back to <see cref="DayOfWeek"/>.</summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = [];

    public int? DayOfMonth { get; set; }

    public TimeSpan? TimeOfDay { get; set; }

    /// <summary>The effective set of weekdays (the new list, or the legacy single day, or Monday).</summary>
    public IReadOnlyList<DayOfWeek> ResolveDays()
    {
        if (DaysOfWeek.Count > 0)
        {
            return DaysOfWeek.Distinct().OrderBy(day => (int)day).ToList();
        }

        return [DayOfWeek ?? System.DayOfWeek.Monday];
    }

    public MonitoringSchedule Clone()
    {
        return new MonitoringSchedule
        {
            Mode = Mode,
            EverySeconds = EverySeconds,
            DayOfWeek = DayOfWeek,
            DaysOfWeek = [.. DaysOfWeek],
            DayOfMonth = DayOfMonth,
            TimeOfDay = TimeOfDay
        };
    }

    public bool ContentEquals(MonitoringSchedule? other)
    {
        return other is not null &&
            Mode == other.Mode &&
            EverySeconds == other.EverySeconds &&
            DayOfWeek == other.DayOfWeek &&
            ResolveDays().SequenceEqual(other.ResolveDays()) &&
            DayOfMonth == other.DayOfMonth &&
            TimeOfDay == other.TimeOfDay;
    }

    public string Summary()
    {
        return Mode switch
        {
            MonitoringScheduleMode.Every => $"every {FormatDuration(TimeSpan.FromSeconds(Math.Max(EverySeconds ?? 0, (int)SensorScheduleDefaults.Minimum.TotalSeconds)))}",
            MonitoringScheduleMode.Daily => $"daily {FormatTime(TimeOfDay)}",
            MonitoringScheduleMode.Weekly => $"weekly {string.Join(", ", ResolveDays())} {FormatTime(TimeOfDay)}",
            MonitoringScheduleMode.Monthly => $"monthly day {Math.Clamp(DayOfMonth ?? 1, 1, 31)} {FormatTime(TimeOfDay)}",
            _ => "schedule"
        };
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
        {
            return $"{duration.TotalSeconds:0}s";
        }

        if (duration.TotalMinutes < 60)
        {
            return $"{duration.TotalMinutes:0.#}m";
        }

        if (duration.TotalHours < 24)
        {
            return $"{duration.TotalHours:0.#}h";
        }

        return $"{duration.TotalDays:0.#}d";
    }

    private static string FormatTime(TimeSpan? time)
    {
        return (time ?? TimeSpan.Zero).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }
}

public static class MonitoringScheduleCalculator
{
    // No sensor ever polls faster than this, whatever the schedule/interval says.
    private static readonly int MinIntervalSeconds = (int)SensorScheduleDefaults.Minimum.TotalSeconds;

    private static TimeSpan ClampToMinimum(TimeSpan interval) =>
        interval < SensorScheduleDefaults.Minimum ? SensorScheduleDefaults.Minimum : interval;

    public static bool IsDue(
        MonitoringSettings settings,
        DateTimeOffset? lastRunUtc,
        DateTimeOffset nowUtc,
        TimeSpan fallbackInterval)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.PollingSchedule is { } schedule)
        {
            return IsScheduleDue(schedule, lastRunUtc, nowUtc);
        }

        var interval = ClampToMinimum(settings.PollingInterval ?? fallbackInterval);
        return lastRunUtc is not DateTimeOffset lastRun || nowUtc - lastRun >= interval;
    }

    public static DateTimeOffset? GetNextDueUtc(
        MonitoringSettings settings,
        DateTimeOffset? lastRunUtc,
        DateTimeOffset nowUtc,
        TimeSpan fallbackInterval)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.PollingSchedule is not { } schedule)
        {
            var interval = ClampToMinimum(settings.PollingInterval ?? fallbackInterval);
            if (lastRunUtc is not DateTimeOffset lastRun)
            {
                return nowUtc;
            }

            var nextRun = lastRun + interval;
            return nextRun <= nowUtc ? nowUtc : nextRun;
        }

        if (schedule.Mode == MonitoringScheduleMode.Every)
        {
            var seconds = Math.Max(schedule.EverySeconds ?? 0, MinIntervalSeconds);
            if (lastRunUtc is not DateTimeOffset lastRun)
            {
                return nowUtc;
            }

            var nextRun = lastRun + TimeSpan.FromSeconds(seconds);
            return nextRun <= nowUtc ? nowUtc : nextRun;
        }

        var nowLocal = nowUtc.ToLocalTime();
        var lastLocal = lastRunUtc?.ToLocalTime();
        var latestOccurrence = GetLatestOccurrence(schedule, nowLocal);
        if (latestOccurrence is not null &&
            (lastLocal is null || lastLocal.Value < latestOccurrence.Value))
        {
            return nowUtc;
        }

        return GetNextOccurrence(schedule, nowLocal)?.ToUniversalTime();
    }

    private static bool IsScheduleDue(MonitoringSchedule schedule, DateTimeOffset? lastRunUtc, DateTimeOffset nowUtc)
    {
        if (schedule.Mode == MonitoringScheduleMode.Every)
        {
            var seconds = Math.Max(schedule.EverySeconds ?? 0, MinIntervalSeconds);
            return lastRunUtc is not DateTimeOffset lastRun || nowUtc - lastRun >= TimeSpan.FromSeconds(seconds);
        }

        var nowLocal = nowUtc.ToLocalTime();
        var lastLocal = lastRunUtc?.ToLocalTime();
        var latestOccurrence = GetLatestOccurrence(schedule, nowLocal);

        if (latestOccurrence is null)
        {
            return false;
        }

        return lastLocal is null || lastLocal.Value < latestOccurrence.Value;
    }

    private static DateTimeOffset? GetLatestOccurrence(MonitoringSchedule schedule, DateTimeOffset nowLocal)
    {
        var time = schedule.TimeOfDay ?? TimeSpan.Zero;
        return schedule.Mode switch
        {
            MonitoringScheduleMode.Daily => GetDailyOccurrence(nowLocal, time),
            MonitoringScheduleMode.Weekly => GetWeeklyOccurrence(nowLocal, schedule.ResolveDays(), time),
            MonitoringScheduleMode.Monthly => GetMonthlyOccurrence(nowLocal, Math.Clamp(schedule.DayOfMonth ?? 1, 1, 31), time),
            _ => null
        };
    }

    private static DateTimeOffset? GetNextOccurrence(MonitoringSchedule schedule, DateTimeOffset nowLocal)
    {
        var time = schedule.TimeOfDay ?? TimeSpan.Zero;
        return schedule.Mode switch
        {
            MonitoringScheduleMode.Daily => GetNextDailyOccurrence(nowLocal, time),
            MonitoringScheduleMode.Weekly => GetNextWeeklyOccurrence(nowLocal, schedule.ResolveDays(), time),
            MonitoringScheduleMode.Monthly => GetNextMonthlyOccurrence(nowLocal, Math.Clamp(schedule.DayOfMonth ?? 1, 1, 31), time),
            _ => null
        };
    }

    private static DateTimeOffset GetDailyOccurrence(DateTimeOffset nowLocal, TimeSpan time)
    {
        var occurrence = AtLocalTime(nowLocal.Date, time, nowLocal.Offset);
        return occurrence <= nowLocal ? occurrence : occurrence.AddDays(-1);
    }

    // Most recent past occurrence across all selected weekdays.
    private static DateTimeOffset GetWeeklyOccurrence(DateTimeOffset nowLocal, IReadOnlyList<DayOfWeek> days, TimeSpan time)
    {
        return days.Select(day => GetWeeklyOccurrenceForDay(nowLocal, day, time)).Max();
    }

    private static DateTimeOffset GetWeeklyOccurrenceForDay(DateTimeOffset nowLocal, DayOfWeek dayOfWeek, TimeSpan time)
    {
        var daysSince = ((int)nowLocal.DayOfWeek - (int)dayOfWeek + 7) % 7;
        var date = nowLocal.Date.AddDays(-daysSince);
        var occurrence = AtLocalTime(date, time, nowLocal.Offset);
        return occurrence <= nowLocal ? occurrence : occurrence.AddDays(-7);
    }

    private static DateTimeOffset GetMonthlyOccurrence(DateTimeOffset nowLocal, int dayOfMonth, TimeSpan time)
    {
        var occurrence = BuildMonthlyOccurrence(nowLocal.Year, nowLocal.Month, dayOfMonth, time, nowLocal.Offset);
        if (occurrence <= nowLocal)
        {
            return occurrence;
        }

        var previousMonth = nowLocal.AddMonths(-1);
        return BuildMonthlyOccurrence(previousMonth.Year, previousMonth.Month, dayOfMonth, time, nowLocal.Offset);
    }

    private static DateTimeOffset BuildMonthlyOccurrence(int year, int month, int dayOfMonth, TimeSpan time, TimeSpan offset)
    {
        var day = Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month));
        return AtLocalTime(new DateTime(year, month, day), time, offset);
    }

    private static DateTimeOffset GetNextDailyOccurrence(DateTimeOffset nowLocal, TimeSpan time)
    {
        var occurrence = AtLocalTime(nowLocal.Date, time, nowLocal.Offset);
        return occurrence > nowLocal ? occurrence : occurrence.AddDays(1);
    }

    // Soonest future occurrence across all selected weekdays.
    private static DateTimeOffset GetNextWeeklyOccurrence(DateTimeOffset nowLocal, IReadOnlyList<DayOfWeek> days, TimeSpan time)
    {
        return days.Select(day => GetNextWeeklyOccurrenceForDay(nowLocal, day, time)).Min();
    }

    private static DateTimeOffset GetNextWeeklyOccurrenceForDay(DateTimeOffset nowLocal, DayOfWeek dayOfWeek, TimeSpan time)
    {
        var daysUntil = ((int)dayOfWeek - (int)nowLocal.DayOfWeek + 7) % 7;
        var occurrence = AtLocalTime(nowLocal.Date.AddDays(daysUntil), time, nowLocal.Offset);
        return occurrence > nowLocal ? occurrence : occurrence.AddDays(7);
    }

    private static DateTimeOffset GetNextMonthlyOccurrence(DateTimeOffset nowLocal, int dayOfMonth, TimeSpan time)
    {
        var occurrence = BuildMonthlyOccurrence(nowLocal.Year, nowLocal.Month, dayOfMonth, time, nowLocal.Offset);
        if (occurrence > nowLocal)
        {
            return occurrence;
        }

        var nextMonth = nowLocal.AddMonths(1);
        return BuildMonthlyOccurrence(nextMonth.Year, nextMonth.Month, dayOfMonth, time, nowLocal.Offset);
    }

    private static DateTimeOffset AtLocalTime(DateTime date, TimeSpan time, TimeSpan offset)
    {
        return new DateTimeOffset(date.Add(time), offset);
    }
}
