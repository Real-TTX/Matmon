using System.Globalization;

namespace Matmon.Core.Domain;

public enum MaintenanceRecurrence
{
    /// <summary>One stretch of time, start to end (patching tonight, a move next Saturday).</summary>
    Once = 0,
    Daily = 1,
    /// <summary>On the chosen weekdays (patch Tuesday 22:00-24:00).</summary>
    Weekly = 2,
    /// <summary>On one day of the month (the 31st means the month's last day).</summary>
    Monthly = 3
}

/// <summary>
/// A maintenance window: a time in which the elements it covers raise NO new alert - a server rebooting for its patches
/// is red for ten minutes and nobody is woken for it. The sensors keep running and show what they see; only the alert and
/// its notification wait. A problem still there when the window ends alarms at the next check. An alert that was already
/// open stays open, but sends nothing (no re-alarm, no escalation) until the window is over.
///
/// What it covers is a target like a notification rule's: an element and everything below it, or every element with a tag.
/// The times of a recurring window are wall-clock times in <see cref="TimeZoneId"/> (the workspace's display zone when it
/// was saved), so "22:00" stays 22:00 across a daylight-saving change.
/// </summary>
public sealed class MaintenanceWindow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>The element it covers, with everything below it. Exclusive with <see cref="TargetTag"/>.</summary>
    public Guid? TargetElementId { get; set; }

    /// <summary>Every element whose effective tags include this one. Exclusive with <see cref="TargetElementId"/>.</summary>
    public string? TargetTag { get; set; }

    public MaintenanceRecurrence Recurrence { get; set; } = MaintenanceRecurrence.Weekly;

    /// <summary>Once: the start.</summary>
    public DateTimeOffset? StartUtc { get; set; }

    /// <summary>Once: the end.</summary>
    public DateTimeOffset? EndUtc { get; set; }

    /// <summary>Weekly: the days.</summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = [];

    /// <summary>Monthly: the day (1-31; past the month's last day = the last day).</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Daily / weekly / monthly: when it starts, wall-clock time in <see cref="TimeZoneId"/>.</summary>
    public TimeOnly StartTime { get; set; } = new(22, 0);

    /// <summary>Daily / weekly / monthly: how long it lasts.</summary>
    public int DurationMinutes { get; set; } = 120;

    /// <summary>The zone the recurring times are in; null = the workspace's display zone.</summary>
    public string? TimeZoneId { get; set; }

    public string? Note { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public MaintenanceWindow Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        TargetElementId = TargetElementId,
        TargetTag = TargetTag,
        Recurrence = Recurrence,
        StartUtc = StartUtc,
        EndUtc = EndUtc,
        DaysOfWeek = [.. DaysOfWeek],
        DayOfMonth = DayOfMonth,
        StartTime = StartTime,
        DurationMinutes = DurationMinutes,
        TimeZoneId = TimeZoneId,
        Note = Note,
        CreatedBy = CreatedBy,
        CreatedUtc = CreatedUtc
    };
}

/// <summary>When a maintenance window is on - pure and tested, so "is it maintenance right now?" is the same answer for
/// the alert gate, the notification dispatcher and the page that lists the windows.</summary>
public static class MaintenanceSchedule
{
    /// <summary>The longest a recurring occurrence may last (a week). A longer stretch is a one-time window.</summary>
    public const int MaxDurationMinutes = 7 * 24 * 60;

    /// <summary>How far ahead <see cref="Next"/> looks.</summary>
    private const int LookAheadDays = 400;

    public readonly record struct Occurrence(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    /// <summary>The occurrence that is on at <paramref name="nowUtc"/>, or null. A switched-off window is never on.</summary>
    public static Occurrence? ActiveAt(MaintenanceWindow window, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (!window.Enabled)
        {
            return null;
        }

        if (window.Recurrence == MaintenanceRecurrence.Once)
        {
            return window.StartUtc is { } start && window.EndUtc is { } end && start <= nowUtc && nowUtc < end
                ? new Occurrence(start, end)
                : null;
        }

        var duration = TimeSpan.FromMinutes(Math.Clamp(window.DurationMinutes, 1, MaxDurationMinutes));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        var lookBack = (int)Math.Ceiling(duration.TotalDays) + 1;
        for (var back = 0; back <= lookBack; back++)
        {
            var day = today.AddDays(-back);
            if (!Matches(window, day))
            {
                continue;
            }

            var start = StartOn(window, day, zone);
            if (start <= nowUtc && nowUtc < start + duration)
            {
                return new Occurrence(start, start + duration);
            }
        }

        return null;
    }

    /// <summary>The occurrence that is on now, else the next one to come (null when there is none - a one-time window
    /// that is over, or a switched-off one).</summary>
    public static Occurrence? Next(MaintenanceWindow window, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (!window.Enabled)
        {
            return null;
        }

        if (ActiveAt(window, nowUtc, zone) is { } active)
        {
            return active;
        }

        if (window.Recurrence == MaintenanceRecurrence.Once)
        {
            return window.StartUtc is { } start && window.EndUtc is { } end && nowUtc < start ? new Occurrence(start, end) : null;
        }

        var duration = TimeSpan.FromMinutes(Math.Clamp(window.DurationMinutes, 1, MaxDurationMinutes));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        for (var ahead = 0; ahead <= LookAheadDays; ahead++)
        {
            var day = today.AddDays(ahead);
            if (!Matches(window, day))
            {
                continue;
            }

            var start = StartOn(window, day, zone);
            if (start > nowUtc)
            {
                return new Occurrence(start, start + duration);
            }
        }

        return null;
    }

    /// <summary>Is a one-time window over (nothing will ever be on again)? Such windows are listed as "Ended".</summary>
    public static bool IsOver(MaintenanceWindow window, DateTimeOffset nowUtc) =>
        window.Recurrence == MaintenanceRecurrence.Once && window.EndUtc is { } end && end <= nowUtc;

    /// <summary>What is wrong with a window, in words - null when it can be saved.</summary>
    public static string? Validate(MaintenanceWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.Name))
        {
            return "Give the window a name.";
        }

        if (window.TargetElementId is null && string.IsNullOrWhiteSpace(window.TargetTag))
        {
            return "Choose what the window covers - an element (with everything below it) or a tag.";
        }

        switch (window.Recurrence)
        {
            case MaintenanceRecurrence.Once:
                if (window.StartUtc is not { } start || window.EndUtc is not { } end)
                {
                    return "A one-time window needs a start and an end.";
                }

                if (end <= start)
                {
                    return "The end must be after the start.";
                }

                break;
            case MaintenanceRecurrence.Weekly when window.DaysOfWeek.Count == 0:
                return "Choose at least one weekday.";
            case MaintenanceRecurrence.Monthly when window.DayOfMonth is < 1 or > 31:
                return "The day of the month is 1 to 31.";
        }

        if (window.Recurrence != MaintenanceRecurrence.Once && window.DurationMinutes is < 1 or > MaxDurationMinutes)
        {
            return "A recurring window lasts from one minute to seven days - longer is a one-time window.";
        }

        return null;
    }

    /// <summary>The schedule in one line: "Tue, Thu 22:00 for 2 h", "daily 03:00 for 30 min", "on day 1 04:00 for 1 h".</summary>
    public static string Describe(MaintenanceWindow window, TimeZoneInfo displayZone)
    {
        string Length(int minutes) => minutes % 60 == 0 ? $"{minutes / 60} h" : minutes > 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
        var time = window.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        return window.Recurrence switch
        {
            MaintenanceRecurrence.Once when window.StartUtc is { } start && window.EndUtc is { } end =>
                $"{Format(start, displayZone)} - {Format(end, displayZone)}",
            MaintenanceRecurrence.Once => "once (no time set)",
            MaintenanceRecurrence.Daily => $"daily {time} for {Length(window.DurationMinutes)}",
            MaintenanceRecurrence.Weekly =>
                $"{string.Join(", ", OrderedDays(window.DaysOfWeek).Select(day => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day)))} {time} for {Length(window.DurationMinutes)}",
            MaintenanceRecurrence.Monthly => $"on day {window.DayOfMonth} {time} for {Length(window.DurationMinutes)}",
            _ => string.Empty
        };
    }

    public static string Format(DateTimeOffset utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utc, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Monday first - the order a week is read in here.</summary>
    public static IEnumerable<DayOfWeek> OrderedDays(IEnumerable<DayOfWeek> days) =>
        days.Distinct().OrderBy(day => ((int)day + 6) % 7);

    private static bool Matches(MaintenanceWindow window, DateOnly day) => window.Recurrence switch
    {
        MaintenanceRecurrence.Daily => true,
        MaintenanceRecurrence.Weekly => window.DaysOfWeek.Contains(day.DayOfWeek),
        MaintenanceRecurrence.Monthly => day.Day == Math.Min(Math.Clamp(window.DayOfMonth, 1, 31), DateTime.DaysInMonth(day.Year, day.Month)),
        _ => false
    };

    /// <summary>The start on a local day, as UTC. A start that falls into the hour the clocks skip in spring moves to the
    /// first minute that exists; one that falls into the hour that repeats in autumn takes the first of the two.</summary>
    private static DateTimeOffset StartOn(MaintenanceWindow window, DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(window.StartTime, DateTimeKind.Unspecified);
        for (var guard = 0; zone.IsInvalidTime(local) && guard < 180; guard++)
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
