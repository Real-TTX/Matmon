using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Services.Reports;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Matmon.Host.Pages;

/// <summary>One line of the reports list.</summary>
public sealed record ReportRow(ReportDefinition Report, string Covers, string Period, string Schedule, string? NextSend, string? LastSend, bool LastFailed);

/// <summary>The report form. Dates are local dates in the workspace's time zone (a date input posts "2026-10-01").</summary>
public sealed class ReportInput
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? TargetToken { get; set; }

    public ReportPeriod Period { get; set; } = ReportPeriod.PreviousMonth;

    public string? CustomFrom { get; set; }

    public string? CustomTo { get; set; }

    public string? PreparedFor { get; set; }

    public string? Note { get; set; }

    public bool ShowSummary { get; set; } = true;

    public bool ShowSensors { get; set; } = true;

    public bool ShowOutages { get; set; } = true;

    public bool ShowAlerts { get; set; } = true;

    public bool ShowCharts { get; set; } = true;

    /// <summary>Text, so "99,9" (a German keyboard) is read as well as "99.9".</summary>
    public string? SlaTarget { get; set; }

    public ReportSchedule Schedule { get; set; } = ReportSchedule.None;

    public int HourOfDay { get; set; } = 7;

    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;

    public int DayOfMonth { get; set; } = 1;

    public Guid? SenderId { get; set; }

    public Guid? ReceiverId { get; set; }

    public string? Recipients { get; set; }

    public ReportAttachment Attachment { get; set; } = ReportAttachment.Pdf;
}

/// <summary>
/// Reports: availability reports for an element (with everything below it) or a tag over a period - as a PDF in the
/// managing partner's branding, or as CSV. A quick report straight from the page, and saved reports that can be e-mailed
/// on a schedule (daily / weekly / monthly). The list and the editor are one page (<c>?edit=new</c> / <c>?edit=&lt;id&gt;</c>),
/// like Maintenance. Everyone may make and download reports; saving, sending and deleting are an admin's (the page write
/// guard), because a scheduled report sends mail to whoever it names.
/// </summary>
public sealed class ReportsModel(IMonitoringWorkspaceStore store, ReportService reports) : PageModel
{
    private const string DateFormat = "yyyy-MM-dd";

    [BindProperty(SupportsGet = true)]
    public string? Edit { get; set; }

    /// <summary>Preselects the quick report's target (a sensor page's "Report" link).</summary>
    [BindProperty(SupportsGet = true, Name = "target")]
    public string? QuickTarget { get; set; }

    [BindProperty]
    public ReportInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string? FormError { get; private set; }

    public bool Editing => Edit is not null;

    public bool IsNew => Input.Id is null;

    public IReadOnlyList<ReportRow> Rows { get; private set; } = [];

    public IReadOnlyList<ElementPickerOption> PickerOptions { get; private set; } = [];

    public IReadOnlyList<SelectListItem> Senders { get; private set; } = [];

    public IReadOnlyList<SelectListItem> Receivers { get; private set; } = [];

    public string QuickFrom { get; private set; } = string.Empty;

    public string QuickTo { get; private set; } = string.Empty;

    public string ZoneName { get; private set; } = string.Empty;

    /// <summary>Whether a managing partner's branding goes onto the reports (shown as a hint).</summary>
    public string? BrandedAs { get; private set; }

    public static IReadOnlyList<(ReportSchedule Value, string Label)> Schedules { get; } =
    [
        (ReportSchedule.None, "Only on demand"),
        (ReportSchedule.Daily, "Daily"),
        (ReportSchedule.Weekly, "Weekly"),
        (ReportSchedule.Monthly, "Monthly")
    ];

    public static IReadOnlyList<(ReportAttachment Value, string Label)> Attachments { get; } =
    [
        (ReportAttachment.Pdf, "PDF"),
        (ReportAttachment.Csv, "CSV (sensors and outages)"),
        (ReportAttachment.PdfAndCsv, "PDF and CSV")
    ];

    public static IReadOnlyList<DayOfWeek> Week { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public IActionResult OnGet()
    {
        if (Edit is null)
        {
            LoadList();
            return Page();
        }

        if (string.Equals(Edit, "new", StringComparison.OrdinalIgnoreCase))
        {
            Input = new ReportInput { TargetToken = QuickTarget };
        }
        else if (Guid.TryParse(Edit, out var id) && store.FindReport(id) is { } report)
        {
            Input = ToInput(report, store.ReportZone());
        }
        else
        {
            ErrorMessage = "That report no longer exists.";
            return RedirectToPage(new { edit = (string?)null });
        }

        LoadEditor();
        return Page();
    }

    /// <summary>A report as a file: a saved one (<paramref name="id"/>) or a quick one (target + period).</summary>
    /// <param name="format">pdf | csv (one row per sensor) | outages (one row per outage).</param>
    /// <param name="inline">Open the PDF in the browser instead of downloading it.</param>
    public IActionResult OnGetDownload(Guid? id, string? target, ReportPeriod? period, string? from, string? to, string? format, bool inline = false)
    {
        var now = DateTimeOffset.UtcNow;
        ReportRequest request;
        if (id is { } reportId)
        {
            if (store.FindReport(reportId) is not { } report)
            {
                ErrorMessage = "That report no longer exists.";
                return RedirectToPage(new { edit = (string?)null });
            }

            request = reports.RequestFor(report, now);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(target) || store.ResolveTargetSensors(target).Count == 0)
            {
                ErrorMessage = "Choose an element or a tag that has sensors.";
                return RedirectToPage(new { edit = (string?)null, target });
            }

            var zone = store.ReportZone();
            var chosen = period ?? ReportPeriod.Last7Days;
            DateTimeOffset? customFrom = null, customTo = null;
            if (chosen == ReportPeriod.Custom)
            {
                if (ParseDate(from, zone) is not { } start || ParseDate(to, zone) is not { } end)
                {
                    ErrorMessage = "A custom period needs a start and an end date.";
                    return RedirectToPage(new { edit = (string?)null, target });
                }

                customFrom = start;
                customTo = end.AddDays(1); // the end date is included
                if (customTo <= customFrom || customTo - customFrom > ReportRules.MaxPeriod)
                {
                    ErrorMessage = "The period must end after it starts and cover at most 400 days.";
                    return RedirectToPage(new { edit = (string?)null, target });
                }
            }

            var window = ReportRules.Resolve(chosen, now, zone, customFrom, customTo);
            request = new ReportRequest(target, window, "Availability report");
        }

        var data = reports.Collect(request, now);
        switch (format?.Trim().ToLowerInvariant())
        {
            case "csv":
                return File(ReportService.Csv(data, outages: false), "text/csv; charset=utf-8", ReportService.FileName(data, "sensors", "csv"));
            case "outages":
                return File(ReportService.Csv(data, outages: true), "text/csv; charset=utf-8", ReportService.FileName(data, "outages", "csv"));
            default:
                var pdf = reports.Pdf(data);
                var name = ReportService.FileName(data, string.Empty, "pdf");
                if (inline)
                {
                    Response.Headers.ContentDisposition = $"inline; filename=\"{name}\"";
                    return File(pdf, "application/pdf");
                }

                return File(pdf, "application/pdf", name);
        }
    }

    public IActionResult OnPostSave()
    {
        Edit ??= Input.Id?.ToString() ?? "new";
        try
        {
            var report = ToReport(Input, store.ReportZone());
            report.CreatedBy ??= User.Identity?.Name;
            var saved = store.SaveReport(report);
            var next = ReportRules.NextSlot(saved, DateTimeOffset.UtcNow, store.ReportZone());
            StatusMessage = $"Report '{saved.Name}' saved." + (next is { } at ? $" It is sent next on {at.ToDisplay():dd.MM.yyyy HH:mm}." : string.Empty);
            return RedirectToPage(new { edit = (string?)null });
        }
        catch (InvalidOperationException exception)
        {
            FormError = exception.Message;
            LoadEditor();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSendNowAsync(Guid id, CancellationToken cancellationToken)
    {
        if (store.FindReport(id) is not { } report)
        {
            ErrorMessage = "That report no longer exists.";
            return RedirectToPage(new { edit = (string?)null });
        }

        if (report.ReceiverId is null && string.IsNullOrWhiteSpace(report.Recipients))
        {
            ErrorMessage = $"'{report.Name}' has no recipient - edit it and choose a receiver or addresses.";
            return RedirectToPage(new { edit = (string?)null });
        }

        var result = await reports.SendAsync(report, cancellationToken);
        if (result.Sent)
        {
            StatusMessage = $"'{report.Name}': {result.Message}";
        }
        else
        {
            ErrorMessage = $"'{report.Name}': {result.Message}";
        }

        return RedirectToPage(new { edit = (string?)null });
    }

    public IActionResult OnPostDelete(Guid id)
    {
        var report = store.FindReport(id);
        if (store.DeleteReport(id))
        {
            StatusMessage = $"Report '{report?.Name}' deleted.";
        }

        return RedirectToPage(new { edit = (string?)null });
    }

    private void LoadList()
    {
        var now = DateTimeOffset.UtcNow;
        var zone = store.ReportZone();
        Rows = store.GetReports()
            .Select(report => new ReportRow(
                report,
                reports.DescribeTargetName(report.TargetToken),
                report.Period == ReportPeriod.Custom
                    ? ReportRules.Resolve(report.Period, now, zone, report.CustomFromUtc, report.CustomToUtc).Label
                    : ReportRules.PeriodLabel(report.Period),
                ReportRules.DescribeSchedule(report),
                ReportRules.NextSlot(report, now, zone) is { } next ? next.ToDisplay().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : null,
                report.LastSentUtc is { } sent ? $"{sent.ToDisplay():dd.MM.yyyy HH:mm} - {report.LastResult}" : null,
                report.LastSendFailed))
            .ToArray();

        PickerOptions = ElementPickerOptions.Build(store.GetWorkspaceClone().RootProbe);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        QuickFrom = today.AddDays(-7).ToString(DateFormat, CultureInfo.InvariantCulture);
        QuickTo = today.ToString(DateFormat, CultureInfo.InvariantCulture);
        ZoneName = zone.Id;
        var branding = ReportDataCollector.Branding(store);
        BrandedAs = branding is null ? null : branding.ProductName ?? branding.PartnerName;
    }

    private void LoadEditor()
    {
        PickerOptions = ElementPickerOptions.Build(store.GetWorkspaceClone().RootProbe);
        var workspace = store.Workspace;
        Senders = workspace.NotificationSenders
            .Where(sender => sender.Kind is NotificationEndpointKind.Email or NotificationEndpointKind.Cloud)
            .Select(sender => new SelectListItem(
                $"{sender.Name}{(sender.Kind == NotificationEndpointKind.Cloud ? " (Matmon Cloud)" : string.Empty)}{(sender.Enabled ? string.Empty : " (switched off)")}",
                sender.Id.ToString()))
            .ToList();
        Receivers =
        [
            .. NotificationReceiverDefaults.All.Select(builtIn => new SelectListItem(builtIn.Name, builtIn.Id.ToString())),
            .. workspace.NotificationReceivers
                .Where(receiver => receiver.Kind == NotificationEndpointKind.Email)
                .Select(receiver => new SelectListItem($"{receiver.Name} - {receiver.Target}", receiver.Id.ToString()))
        ];
        ZoneName = store.ReportZone().Id;
        var branding = ReportDataCollector.Branding(store);
        BrandedAs = branding is null ? null : branding.ProductName ?? branding.PartnerName;
    }

    private static ReportInput ToInput(ReportDefinition report, TimeZoneInfo zone)
    {
        string? Local(DateTimeOffset? utc, int addDays) => utc is { } value
            ? TimeZoneInfo.ConvertTime(value, zone).Date.AddDays(addDays).ToString(DateFormat, CultureInfo.InvariantCulture)
            : null;

        return new ReportInput
        {
            Id = report.Id,
            Name = report.Name,
            TargetToken = report.TargetToken,
            Period = report.Period,
            CustomFrom = Local(report.CustomFromUtc, 0),
            CustomTo = Local(report.CustomToUtc, -1),
            PreparedFor = report.PreparedFor,
            Note = report.Note,
            ShowSummary = report.Sections.HasFlag(ReportSections.Summary),
            ShowSensors = report.Sections.HasFlag(ReportSections.Sensors),
            ShowOutages = report.Sections.HasFlag(ReportSections.Outages),
            ShowAlerts = report.Sections.HasFlag(ReportSections.Alerts),
            ShowCharts = report.Sections.HasFlag(ReportSections.Charts),
            SlaTarget = report.SlaTargetPercent?.ToString("0.###", CultureInfo.InvariantCulture),
            Schedule = report.Schedule,
            HourOfDay = report.HourOfDay,
            DayOfWeek = report.DayOfWeek,
            DayOfMonth = report.DayOfMonth,
            SenderId = report.SenderId,
            ReceiverId = report.ReceiverId,
            Recipients = report.Recipients,
            Attachment = report.Attachment
        };
    }

    private static ReportDefinition ToReport(ReportInput input, TimeZoneInfo zone)
    {
        double? sla = null;
        if (!string.IsNullOrWhiteSpace(input.SlaTarget))
        {
            var text = input.SlaTarget.Trim().TrimEnd('%').Trim().Replace(',', '.');
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidOperationException("The agreed availability is a number, e.g. 99.9.");
            }

            sla = value;
        }

        DateTimeOffset? from = null, to = null;
        if (input.Period == ReportPeriod.Custom)
        {
            from = ParseDate(input.CustomFrom, zone) ?? throw new InvalidOperationException("When does the period start?");
            to = (ParseDate(input.CustomTo, zone) ?? throw new InvalidOperationException("When does the period end?")).AddDays(1);
        }

        var sections = ReportSections.None;
        if (input.ShowSummary) sections |= ReportSections.Summary;
        if (input.ShowSensors) sections |= ReportSections.Sensors;
        if (input.ShowOutages) sections |= ReportSections.Outages;
        if (input.ShowAlerts) sections |= ReportSections.Alerts;
        if (input.ShowCharts) sections |= ReportSections.Charts;

        return new ReportDefinition
        {
            Id = input.Id ?? Guid.NewGuid(),
            Name = input.Name ?? string.Empty,
            TargetToken = input.TargetToken,
            Period = input.Period,
            CustomFromUtc = from,
            CustomToUtc = to,
            PreparedFor = input.PreparedFor,
            Note = input.Note,
            Sections = sections,
            SlaTargetPercent = sla,
            Schedule = input.Schedule,
            HourOfDay = input.HourOfDay,
            DayOfWeek = input.DayOfWeek,
            DayOfMonth = input.DayOfMonth,
            SenderId = input.SenderId,
            ReceiverId = input.ReceiverId,
            Recipients = input.Recipients ?? string.Empty,
            Attachment = input.Attachment
        };
    }

    /// <summary>A local date (yyyy-MM-dd) as the UTC instant of its midnight in <paramref name="zone"/>.</summary>
    private static DateTimeOffset? ParseDate(string? text, TimeZoneInfo zone) =>
        DateTime.TryParseExact(text?.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? ReportRules.ToUtc(date, zone)
            : null;
}
