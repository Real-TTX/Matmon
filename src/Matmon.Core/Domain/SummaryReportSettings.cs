namespace Matmon.Core.Domain;

public enum SummaryReportCadence
{
    Daily = 0,
    Weekly = 1
}

/// <summary>
/// Configuration for the scheduled e-mail summary report (uptime, worst sensors, alert counts, recent
/// events). Sent by <c>ReportSchedulerService</c> via the notification SMTP settings.
/// </summary>
public sealed class SummaryReportSettings
{
    public bool Enabled { get; set; }

    public SummaryReportCadence Cadence { get; set; } = SummaryReportCadence.Daily;

    /// <summary>Local hour of day (0-23) the report is sent.</summary>
    public int HourOfDay { get; set; } = 7;

    /// <summary>Weekday the weekly report is sent (ignored for daily).</summary>
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;

    /// <summary>Comma/semicolon-separated recipient addresses.</summary>
    public string Recipients { get; set; } = string.Empty;

    public string Subject { get; set; } = "Matmon summary report";

    /// <summary>When the last scheduled report was sent (runtime bookkeeping, not user-set).</summary>
    public DateTimeOffset? LastSentUtc { get; set; }

    public SummaryReportSettings Clone()
    {
        return new SummaryReportSettings
        {
            Enabled = Enabled,
            Cadence = Cadence,
            HourOfDay = HourOfDay,
            DayOfWeek = DayOfWeek,
            Recipients = Recipients,
            Subject = Subject,
            LastSentUtc = LastSentUtc
        };
    }
}
