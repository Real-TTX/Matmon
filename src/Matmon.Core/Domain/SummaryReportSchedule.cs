namespace Matmon.Core.Domain;

/// <summary>
/// Pure scheduling logic for the summary report: given the settings and the current local time, decides
/// whether a report is due (i.e. a scheduled slot has passed that we haven't sent for yet).
/// </summary>
public static class SummaryReportSchedule
{
    public static bool IsDue(SummaryReportSettings settings, DateTimeOffset nowLocal)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
        {
            return false;
        }

        var slot = MostRecentSlot(settings, nowLocal);
        // Compare the last-sent instant in the same offset as "now" (deterministic, not machine-tz bound).
        return settings.LastSentUtc is null || settings.LastSentUtc.Value.ToOffset(nowLocal.Offset) < slot;
    }

    /// <summary>The most recent scheduled send time at or before <paramref name="nowLocal"/> (local time).</summary>
    public static DateTimeOffset MostRecentSlot(SummaryReportSettings settings, DateTimeOffset nowLocal)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var hour = Math.Clamp(settings.HourOfDay, 0, 23);
        var todaySlot = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, hour, 0, 0, nowLocal.Offset);

        if (settings.Cadence == SummaryReportCadence.Weekly)
        {
            var diff = ((int)nowLocal.DayOfWeek - (int)settings.DayOfWeek + 7) % 7;
            var candidate = todaySlot.AddDays(-diff);
            return candidate > nowLocal ? candidate.AddDays(-7) : candidate;
        }

        return nowLocal >= todaySlot ? todaySlot : todaySlot.AddDays(-1);
    }
}
