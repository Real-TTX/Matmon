using System.Text;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;

namespace Matmon.Host.Services.Reports;

/// <summary>Makes reports - for a saved definition or a quick request - as PDF or CSV, and e-mails a saved one.</summary>
public sealed class ReportService(
    ReportDataCollector collector,
    ReportPdfBuilder pdfBuilder,
    ReportMailer mailer,
    IMonitoringWorkspaceStore store,
    ILogger<ReportService> logger)
{
    public ReportRequest RequestFor(ReportDefinition report, DateTimeOffset nowUtc) => new(
        report.TargetToken ?? string.Empty,
        ReportRules.Resolve(report.Period, nowUtc, store.ReportZone(), report.CustomFromUtc, report.CustomToUtc),
        report.Name,
        report.PreparedFor,
        report.Note,
        report.Sections,
        report.SlaTargetPercent);

    public AvailabilityReportData Collect(ReportRequest request, DateTimeOffset nowUtc) => collector.Collect(request, nowUtc);

    /// <summary>What a target token covers, in words ("Office / NAS", "Everything tagged #backup").</summary>
    public string DescribeTargetName(string? token) => collector.DescribeTarget(token);

    public byte[] Pdf(AvailabilityReportData data) => pdfBuilder.Build(data);

    public static byte[] Csv(AvailabilityReportData data, bool outages) => outages ? ReportCsv.Outages(data) : ReportCsv.Sensors(data);

    /// <summary>"availability-report-office-2026-10.pdf" - the title and the period's start, safe in every file system.</summary>
    public static string FileName(AvailabilityReportData data, string suffix, string extension)
    {
        var slug = Slug(data.Title);
        var stamp = TimeZoneInfo.ConvertTime(data.Window.FromUtc, data.Zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return $"{(slug.Length == 0 ? "report" : slug)}{(suffix.Length == 0 ? string.Empty : "-" + suffix)}-{stamp}.{extension}";
    }

    public async Task<ReportSendResult> SendAsync(ReportDefinition report, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        AvailabilityReportData data;
        try
        {
            data = collector.Collect(RequestFor(report, now), now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Report {Report} could not be made", report.Name);
            return new ReportSendResult(false, $"Not sent: the report could not be made ({exception.Message}).");
        }

        var attachments = new List<EmailAttachment>();
        if (report.Attachment is ReportAttachment.Pdf or ReportAttachment.PdfAndCsv)
        {
            attachments.Add(new EmailAttachment(FileName(data, string.Empty, "pdf"), pdfBuilder.Build(data), "application/pdf"));
        }

        if (report.Attachment is ReportAttachment.Csv or ReportAttachment.PdfAndCsv)
        {
            attachments.Add(new EmailAttachment(FileName(data, "sensors", "csv"), ReportCsv.Sensors(data), "text/csv"));
            if (data.Outages.Count > 0)
            {
                attachments.Add(new EmailAttachment(FileName(data, "outages", "csv"), ReportCsv.Outages(data), "text/csv"));
            }
        }

        var note = attachments.Count switch
        {
            0 => null,
            1 => $"The full report is attached ({attachments[0].FileName}).",
            _ => $"The full report is attached ({string.Join(", ", attachments.Select(attachment => attachment.FileName))})."
        };
        var mail = ReportEmail.Build(data, note);
        var result = await mailer.SendAsync(report.SenderId, report.ReceiverId, report.Recipients, mail.Subject, mail.TextBody, mail.HtmlBody, attachments, cancellationToken);
        logger.Log(result.Sent ? LogLevel.Information : LogLevel.Warning, "Report {Report}: {Result}", report.Name, result.Message);
        return result;
    }

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var character in text.Trim().ToLowerInvariant())
        {
            var mapped = character switch
            {
                'ä' => "ae",
                'ö' => "oe",
                'ü' => "ue",
                'ß' => "ss",
                _ when character is >= 'a' and <= 'z' or >= '0' and <= '9' => character.ToString(),
                _ => "-"
            };
            sb.Append(mapped);
        }

        var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }
}
