namespace Matmon.Core.Domain;

/// <summary>The time a report covers. The calendar periods (yesterday, the previous week / month) are whole days in the
/// workspace's display time zone; the rolling ones end at the moment the report is made.</summary>
public enum ReportPeriod
{
    Last24Hours = 0,
    Last7Days = 1,
    Last30Days = 2,
    Last90Days = 3,
    Yesterday = 4,
    PreviousWeek = 5,
    PreviousMonth = 6,
    ThisMonth = 7,
    Custom = 8
}

/// <summary>How often a saved report is e-mailed (None = only made on demand).</summary>
public enum ReportSchedule
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3
}

/// <summary>What a report contains.</summary>
[Flags]
public enum ReportSections
{
    None = 0,
    Summary = 1,
    Sensors = 2,
    Outages = 4,
    Alerts = 8,
    Charts = 16,
    All = Summary | Sensors | Outages | Alerts | Charts
}

/// <summary>What a scheduled report's e-mail carries.</summary>
public enum ReportAttachment
{
    Pdf = 0,
    Csv = 1,
    PdfAndCsv = 2
}

/// <summary>
/// A saved report (Reports): what it covers (an element with everything below it, or a tag), over which period, what it
/// shows, and - optionally - when it is e-mailed to whom. Made as a PDF (with the managing partner's branding) or a CSV.
/// Carried with the Notifications backup section, like the scheduled summary report.
/// </summary>
public sealed class ReportDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>An element id or "tag:&lt;name&gt;" (<see cref="MonitoringTargetResolver"/>).</summary>
    public string? TargetToken { get; set; }

    public ReportPeriod Period { get; set; } = ReportPeriod.PreviousMonth;

    /// <summary>The custom period (<see cref="ReportPeriod.Custom"/>).</summary>
    public DateTimeOffset? CustomFromUtc { get; set; }

    public DateTimeOffset? CustomToUtc { get; set; }

    /// <summary>Who the report is for ("Example GmbH") - printed under the title.</summary>
    public string? PreparedFor { get; set; }

    /// <summary>A short text printed at the top of the report.</summary>
    public string? Note { get; set; }

    public ReportSections Sections { get; set; } = ReportSections.All;

    /// <summary>The agreed availability in percent (99.9). Sensors below it are marked; null = none agreed.</summary>
    public double? SlaTargetPercent { get; set; }

    public ReportSchedule Schedule { get; set; } = ReportSchedule.None;

    /// <summary>Hour of day (0-23, display time zone) a scheduled report is sent.</summary>
    public int HourOfDay { get; set; } = 7;

    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;

    /// <summary>Day of the month (1-28) a monthly report is sent.</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Optional sender (SMTP endpoint or the Matmon Cloud sender); null = the workspace default SMTP.</summary>
    public Guid? SenderId { get; set; }

    /// <summary>Optional receiver whose target is the recipient (a built-in user group too); null = <see cref="Recipients"/>.</summary>
    public Guid? ReceiverId { get; set; }

    /// <summary>Comma/semicolon-separated addresses, used when no receiver is chosen.</summary>
    public string Recipients { get; set; } = string.Empty;

    public ReportAttachment Attachment { get; set; } = ReportAttachment.Pdf;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTimeOffset? UpdatedUtc { get; set; }

    /// <summary>When the schedule last sent it (or tried to - see <see cref="LastResult"/>).</summary>
    public DateTimeOffset? LastSentUtc { get; set; }

    /// <summary>How the last scheduled send ended, in words.</summary>
    public string? LastResult { get; set; }

    public bool LastSendFailed { get; set; }

    public ReportDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        TargetToken = TargetToken,
        Period = Period,
        CustomFromUtc = CustomFromUtc,
        CustomToUtc = CustomToUtc,
        PreparedFor = PreparedFor,
        Note = Note,
        Sections = Sections,
        SlaTargetPercent = SlaTargetPercent,
        Schedule = Schedule,
        HourOfDay = HourOfDay,
        DayOfWeek = DayOfWeek,
        DayOfMonth = DayOfMonth,
        SenderId = SenderId,
        ReceiverId = ReceiverId,
        Recipients = Recipients,
        Attachment = Attachment,
        CreatedUtc = CreatedUtc,
        CreatedBy = CreatedBy,
        UpdatedUtc = UpdatedUtc,
        LastSentUtc = LastSentUtc,
        LastResult = LastResult,
        LastSendFailed = LastSendFailed
    };
}

/// <summary>A resolved report period.</summary>
public readonly record struct ReportWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string Label)
{
    public TimeSpan Length => ToUtc - FromUtc;
}

/// <summary>Pure rules of reports: the periods, the schedule, the validation.</summary>
public static class ReportRules
{
    /// <summary>The longest period a report covers - the daily totals behind long reports are kept 400 days.</summary>
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(400);

    public const int MaxNameLength = 120;

    public const int MaxNoteLength = 1000;

    public static IReadOnlyList<(ReportPeriod Value, string Label)> Periods { get; } =
    [
        (ReportPeriod.Last24Hours, "Last 24 hours"),
        (ReportPeriod.Last7Days, "Last 7 days"),
        (ReportPeriod.Last30Days, "Last 30 days"),
        (ReportPeriod.Last90Days, "Last 90 days"),
        (ReportPeriod.Yesterday, "Yesterday"),
        (ReportPeriod.PreviousWeek, "Previous week (Mon-Sun)"),
        (ReportPeriod.PreviousMonth, "Previous month"),
        (ReportPeriod.ThisMonth, "This month so far"),
        (ReportPeriod.Custom, "Custom period")
    ];

    public static string PeriodLabel(ReportPeriod period) =>
        Periods.FirstOrDefault(entry => entry.Value == period).Label ?? period.ToString();

    /// <summary>The period as UTC instants. Calendar periods are whole days in <paramref name="zone"/>.</summary>
    public static ReportWindow Resolve(ReportPeriod period, DateTimeOffset nowUtc, TimeZoneInfo zone, DateTimeOffset? customFromUtc = null, DateTimeOffset? customToUtc = null)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime;
        var today = localNow.Date;

        switch (period)
        {
            case ReportPeriod.Last24Hours:
                return new ReportWindow(nowUtc.AddHours(-24), nowUtc, "Last 24 hours");
            case ReportPeriod.Last7Days:
                return new ReportWindow(nowUtc.AddDays(-7), nowUtc, "Last 7 days");
            case ReportPeriod.Last30Days:
                return new ReportWindow(nowUtc.AddDays(-30), nowUtc, "Last 30 days");
            case ReportPeriod.Last90Days:
                return new ReportWindow(nowUtc.AddDays(-90), nowUtc, "Last 90 days");
            case ReportPeriod.Yesterday:
            {
                var from = today.AddDays(-1);
                return new ReportWindow(ToUtc(from, zone), ToUtc(today, zone), from.ToString("dd.MM.yyyy"));
            }
            case ReportPeriod.PreviousWeek:
            {
                var sinceMonday = ((int)today.DayOfWeek + 6) % 7;
                var thisMonday = today.AddDays(-sinceMonday);
                var from = thisMonday.AddDays(-7);
                return new ReportWindow(ToUtc(from, zone), ToUtc(thisMonday, zone),
                    $"Week {System.Globalization.ISOWeek.GetWeekOfYear(from)}/{System.Globalization.ISOWeek.GetYear(from)} ({from:dd.MM.} - {thisMonday.AddDays(-1):dd.MM.yyyy})");
            }
            case ReportPeriod.PreviousMonth:
            {
                var thisMonth = new DateTime(today.Year, today.Month, 1);
                var from = thisMonth.AddMonths(-1);
                return new ReportWindow(ToUtc(from, zone), ToUtc(thisMonth, zone), from.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
            }
            case ReportPeriod.ThisMonth:
            {
                var from = new DateTime(today.Year, today.Month, 1);
                return new ReportWindow(ToUtc(from, zone), nowUtc, from.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + " (so far)");
            }
            case ReportPeriod.Custom:
            {
                var from = customFromUtc ?? nowUtc.AddDays(-7);
                var to = customToUtc ?? nowUtc;
                if (to > nowUtc)
                {
                    to = nowUtc;
                }

                var fromLocal = TimeZoneInfo.ConvertTime(from, zone);
                var toLocal = TimeZoneInfo.ConvertTime(to, zone);
                return new ReportWindow(from, to, $"{fromLocal:dd.MM.yyyy HH:mm} - {toLocal:dd.MM.yyyy HH:mm}");
            }
            default:
                return new ReportWindow(nowUtc.AddDays(-7), nowUtc, "Last 7 days");
        }
    }

    /// <summary>A local wall-clock time as UTC; a time that does not exist (spring forward) moves to the first that does.</summary>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var guard = 0;
        while (zone.IsInvalidTime(unspecified) && guard++ < 180)
        {
            unspecified = unspecified.AddMinutes(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
    }

    /// <summary>Why the report cannot be saved, or null.</summary>
    public static string? Validate(ReportDefinition report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(report.Name))
        {
            return "Give the report a name.";
        }

        if (report.Name.Length > MaxNameLength)
        {
            return $"The name is too long (at most {MaxNameLength} characters).";
        }

        if (string.IsNullOrWhiteSpace(report.TargetToken))
        {
            return "Choose what the report covers - an element or a tag.";
        }

        if (report.Note is { Length: > MaxNoteLength })
        {
            return $"The text is too long (at most {MaxNoteLength} characters).";
        }

        if (report.Sections == ReportSections.None)
        {
            return "Choose at least one thing the report shows.";
        }

        if (report.SlaTargetPercent is { } sla && (double.IsNaN(sla) || sla <= 0 || sla > 100))
        {
            return "The agreed availability is a percentage above 0 and at most 100.";
        }

        if (report.Period == ReportPeriod.Custom)
        {
            if (report.CustomFromUtc is not { } from || report.CustomToUtc is not { } to)
            {
                return "A custom period needs a start and an end.";
            }

            if (to <= from)
            {
                return "The period must end after it starts.";
            }

            if (to - from > MaxPeriod)
            {
                return "A report covers at most 400 days.";
            }

            if (report.Schedule != ReportSchedule.None)
            {
                return "A scheduled report needs a period that moves along - a custom period would send the same days every time.";
            }
        }

        if (report.Schedule != ReportSchedule.None)
        {
            if (report.HourOfDay is < 0 or > 23)
            {
                return "The hour is 0 to 23.";
            }

            if (report.Schedule == ReportSchedule.Monthly && report.DayOfMonth is < 1 or > 28)
            {
                return "The day of the month is 1 to 28.";
            }

            if (report.ReceiverId is null && string.IsNullOrWhiteSpace(report.Recipients))
            {
                return "A scheduled report needs a recipient - a receiver or addresses.";
            }
        }

        return null;
    }

    /// <summary>The most recent scheduled send at or before <paramref name="nowUtc"/>, or null when not scheduled.</summary>
    public static DateTimeOffset? MostRecentSlot(ReportDefinition report, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        if (report.Schedule == ReportSchedule.None)
        {
            return null;
        }

        var hour = Math.Clamp(report.HourOfDay, 0, 23);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime;
        var today = localNow.Date;

        DateTime candidate;
        switch (report.Schedule)
        {
            case ReportSchedule.Daily:
                candidate = today.AddHours(hour);
                if (candidate > localNow)
                {
                    candidate = candidate.AddDays(-1);
                }

                break;
            case ReportSchedule.Weekly:
            {
                var diff = ((int)localNow.DayOfWeek - (int)report.DayOfWeek + 7) % 7;
                candidate = today.AddDays(-diff).AddHours(hour);
                if (candidate > localNow)
                {
                    candidate = candidate.AddDays(-7);
                }

                break;
            }
            default:
            {
                var day = Math.Clamp(report.DayOfMonth, 1, 28);
                candidate = new DateTime(today.Year, today.Month, day).AddHours(hour);
                if (candidate > localNow)
                {
                    candidate = candidate.AddMonths(-1);
                }

                break;
            }
        }

        return ToUtc(candidate, zone);
    }

    /// <summary>The next scheduled send after <paramref name="nowUtc"/>, or null when not scheduled.</summary>
    public static DateTimeOffset? NextSlot(ReportDefinition report, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (MostRecentSlot(report, nowUtc, zone) is not { } last)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(last, zone).DateTime;
        var next = report.Schedule switch
        {
            ReportSchedule.Daily => local.AddDays(1),
            ReportSchedule.Weekly => local.AddDays(7),
            _ => local.AddMonths(1)
        };
        return ToUtc(next, zone);
    }

    /// <summary>Whether a scheduled send is due: a slot has passed since the report was last sent - or, never sent, since it
    /// was made (a report saved at 10:00 with a 07:00 schedule waits for tomorrow instead of going out at once).</summary>
    public static bool IsDue(ReportDefinition report, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (MostRecentSlot(report, nowUtc, zone) is not { } slot)
        {
            return false;
        }

        var since = report.LastSentUtc ?? report.CreatedUtc;
        return since < slot;
    }

    /// <summary>The schedule in words.</summary>
    public static string DescribeSchedule(ReportDefinition report) => report.Schedule switch
    {
        ReportSchedule.Daily => $"Daily at {report.HourOfDay:00}:00",
        ReportSchedule.Weekly => $"Every {report.DayOfWeek} at {report.HourOfDay:00}:00",
        ReportSchedule.Monthly => $"Monthly on day {report.DayOfMonth} at {report.HourOfDay:00}:00",
        _ => "On demand"
    };

    /// <summary>The period that suits a schedule (what the editor proposes when a schedule is picked).</summary>
    public static ReportPeriod SuggestedPeriod(ReportSchedule schedule) => schedule switch
    {
        ReportSchedule.Daily => ReportPeriod.Yesterday,
        ReportSchedule.Weekly => ReportPeriod.PreviousWeek,
        ReportSchedule.Monthly => ReportPeriod.PreviousMonth,
        _ => ReportPeriod.Last7Days
    };
}
