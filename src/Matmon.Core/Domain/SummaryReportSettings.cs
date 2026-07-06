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

    /// <summary>Optional notification sender (SMTP endpoint) to send through. When null, falls back to the
    /// workspace default SMTP / first enabled e-mail sender (legacy behaviour).</summary>
    public Guid? SenderId { get; set; }

    /// <summary>Optional notification receiver whose target is the recipient. When null, <see cref="Recipients"/>
    /// is used.</summary>
    public Guid? ReceiverId { get; set; }

    /// <summary>Comma/semicolon-separated recipient addresses (used when <see cref="ReceiverId"/> is not set).</summary>
    public string Recipients { get; set; } = string.Empty;

    public string Subject { get; set; } = "Matmon summary report";

    /// <summary>Attach a PDF audit report to the summary e-mail.</summary>
    public bool AttachPdf { get; set; }

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
            SenderId = SenderId,
            ReceiverId = ReceiverId,
            Recipients = Recipients,
            Subject = Subject,
            AttachPdf = AttachPdf,
            LastSentUtc = LastSentUtc
        };
    }
}
