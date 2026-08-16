using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Matmon.Core.Domain;
using Matmon.Core.Sample;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Matmon.Host.Ui;

namespace Matmon.Host.Pages;

public sealed partial class WorkspaceModel
{
    private static List<DayOfWeek> NormalizeScheduleDays(IReadOnlyList<DayOfWeek>? days)
    {
        var list = (days ?? []).Distinct().OrderBy(day => (int)day).ToList();
        return list.Count > 0 ? list : [DayOfWeek.Monday];
    }

    private static void ApplyScheduleSettings(
        MonitoringSettings settings,
        string? scheduleMode,
        int? scheduleEveryValue,
        string? scheduleEveryUnit,
        IReadOnlyList<DayOfWeek>? scheduleDaysOfWeek,
        int? scheduleDayOfMonth,
        string? scheduleTime)
    {
        settings.PollingInterval = null;
        settings.PollingSchedule = null;

        var mode = string.IsNullOrWhiteSpace(scheduleMode)
            ? "inherit"
            : scheduleMode.Trim().ToLowerInvariant();

        if (mode == "inherit")
        {
            return;
        }

        var timeOfDay = ParseScheduleTime(scheduleTime);
        settings.PollingSchedule = mode switch
        {
            "every" or "custom" => new MonitoringSchedule
            {
                Mode = MonitoringScheduleMode.Every,
                EverySeconds = ResolveEverySeconds(scheduleEveryValue, scheduleEveryUnit)
            },
            "daily" => new MonitoringSchedule { Mode = MonitoringScheduleMode.Daily, TimeOfDay = timeOfDay },
            "weekly" => new MonitoringSchedule
            {
                Mode = MonitoringScheduleMode.Weekly,
                DaysOfWeek = NormalizeScheduleDays(scheduleDaysOfWeek),
                TimeOfDay = timeOfDay
            },
            "monthly" => new MonitoringSchedule
            {
                Mode = MonitoringScheduleMode.Monthly,
                DayOfMonth = Math.Clamp(scheduleDayOfMonth ?? 1, 1, 31),
                TimeOfDay = timeOfDay
            },
            // Backward-compat with older fixed presets that may still be posted.
            "every-30s" => new MonitoringSchedule { Mode = MonitoringScheduleMode.Every, EverySeconds = 30 },
            "every-5m" => new MonitoringSchedule { Mode = MonitoringScheduleMode.Every, EverySeconds = 300 },
            "hourly" => new MonitoringSchedule { Mode = MonitoringScheduleMode.Every, EverySeconds = 3600 },
            "every-2h" => new MonitoringSchedule { Mode = MonitoringScheduleMode.Every, EverySeconds = 7200 },
            _ => null
        };
    }

    private static int ResolveEverySeconds(int? value, string? unit)
    {
        var safeValue = Math.Max(value ?? 1, 1);
        var factor = (unit?.Trim().ToLowerInvariant()) switch
        {
            "second" or "seconds" or "s" => 1,
            "hour" or "hours" or "h" => 3600,
            "day" or "days" or "d" => 86400,
            _ => 60 // minutes
        };

        // Enforce a sane minimum so a misconfigured value cannot hammer a target.
        return Math.Max(safeValue * factor, 5);
    }

    private static TimeSpan ParseScheduleTime(string? scheduleTime)
    {
        if (TimeSpan.TryParse(scheduleTime, CultureInfo.InvariantCulture, out var time) &&
            time >= TimeSpan.Zero &&
            time < TimeSpan.FromDays(1))
        {
            return time;
        }

        return TimeSpan.Zero;
    }

    private static ScheduleEditorState BuildScheduleEditorState(MonitoringSettings localSettings, MonitoringSettings effectiveSettings, TimeSpan defaultInterval)
    {
        var (mode, everyValue, everyUnit, dayOfWeek, dayOfMonth, time) =
            ReadScheduleInput(localSettings.PollingSchedule, localSettings.PollingInterval);

        var daysOfWeek = localSettings.PollingSchedule is { Mode: MonitoringScheduleMode.Weekly } weekly
            ? weekly.ResolveDays().ToList()
            : new List<DayOfWeek>();

        return new ScheduleEditorState(
            mode,
            everyValue,
            everyUnit,
            dayOfWeek,
            daysOfWeek,
            dayOfMonth,
            time,
            MonitoringDisplay.FormatScheduleSummary(effectiveSettings, defaultInterval));
    }

    private static (string Mode, int? EveryValue, string EveryUnit, DayOfWeek? DayOfWeek, int? DayOfMonth, string? Time)
        ReadScheduleInput(MonitoringSchedule? schedule, TimeSpan? legacyInterval)
    {
        if (schedule is not null)
        {
            return schedule.Mode switch
            {
                MonitoringScheduleMode.Daily => (
                    "daily",
                    null,
                    "minutes",
                    DayOfWeek.Monday,
                    1,
                    FormatScheduleTime(schedule.TimeOfDay)),
                MonitoringScheduleMode.Weekly => (
                    "weekly",
                    null,
                    "minutes",
                    schedule.DayOfWeek ?? DayOfWeek.Monday,
                    1,
                    FormatScheduleTime(schedule.TimeOfDay)),
                MonitoringScheduleMode.Monthly => (
                    "monthly",
                    null,
                    "minutes",
                    DayOfWeek.Monday,
                    Math.Clamp(schedule.DayOfMonth ?? 1, 1, 31),
                    FormatScheduleTime(schedule.TimeOfDay)),
                _ => BuildEveryScheduleInput(schedule.EverySeconds ?? 300)
            };
        }

        if (legacyInterval is TimeSpan interval)
        {
            return BuildEveryScheduleInput((int)Math.Round(interval.TotalSeconds));
        }

        return ("inherit", null, "minutes", DayOfWeek.Monday, 1, "00:00");
    }

    private static (string Mode, int? EveryValue, string EveryUnit, DayOfWeek? DayOfWeek, int? DayOfMonth, string? Time)
        BuildEveryScheduleInput(int seconds)
    {
        var safeSeconds = Math.Max(seconds, 1);

        // Express the interval in the largest whole unit so the editor shows a clean value.
        if (safeSeconds % 86400 == 0)
        {
            return ("every", safeSeconds / 86400, "days", DayOfWeek.Monday, 1, "00:00");
        }

        if (safeSeconds % 3600 == 0)
        {
            return ("every", safeSeconds / 3600, "hours", DayOfWeek.Monday, 1, "00:00");
        }

        if (safeSeconds % 60 == 0)
        {
            return ("every", safeSeconds / 60, "minutes", DayOfWeek.Monday, 1, "00:00");
        }

        return ("every", safeSeconds, "seconds", DayOfWeek.Monday, 1, "00:00");
    }
    private static string FormatScheduleTime(TimeSpan? time)
    {
        return (time ?? TimeSpan.Zero).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }
}

internal sealed record ScheduleEditorState(
    string Preset,
    int? EveryValue,
    string EveryUnit,
    DayOfWeek? DayOfWeek,
    List<DayOfWeek> DaysOfWeek,
    int? DayOfMonth,
    string? Time,
    string InheritedLabel);
