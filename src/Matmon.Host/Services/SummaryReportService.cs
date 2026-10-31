using System.Net.Http;
using System.Net.Http.Json;
using Matmon.Core;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;

namespace Matmon.Host.Services;

/// <summary>Gathers the data for the summary report from the store + telemetry (uptime, counts, events).</summary>
public sealed class SummaryReportDataCollector
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public SummaryReportDataCollector(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    public SummaryReportData Collect(DateTimeOffset nowUtc, TimeSpan window, int maxSensorLines = 10)
    {
        var fromUtc = nowUtc - window;
        var elements = _workspaceStore.GetAllElements();
        var elementsById = elements.ToDictionary(element => element.Id);
        var sensors = elements.OfType<SensorElement>().ToArray();
        var latest = _workspaceStore.GetLatestSensorObservations();

        var workspaceName = elements.OfType<ProbeElement>().FirstOrDefault(probe => probe.ParentId is null)?.Name ?? "Matmon";
        var probeCount = elements.OfType<ProbeElement>().Count();
        var pausedIds = sensors.Where(sensor => sensor.IsPaused).Select(sensor => sensor.Id).ToHashSet();

        var errorCount = 0;
        var warningCount = 0;
        foreach (var (sensorId, observation) in latest)
        {
            if (pausedIds.Contains(sensorId))
            {
                continue;
            }

            switch (observation.State)
            {
                case SensorState.Critical:
                    errorCount++;
                    break;
                case SensorState.Warning:
                    warningCount++;
                    break;
            }
        }

        var (openAlerts, acknowledgedAlerts, _, _) = _workspaceStore.GetActiveAlertCounts();

        var lines = new List<SummaryReportSensorLine>();
        foreach (var sensor in sensors)
        {
            if (sensor.IsPaused)
            {
                continue;
            }

            // Windowed in SQL, and the two easy-to-get-wrong parts - the per-channel dedupe and "a warning
            // still counts as up" - live in the shared SensorUptime, so this and the wallboard's SLA widget
            // cannot drift apart.
            var uptimeSummary = SensorUptime.FromBuckets(_workspaceStore.GetSensorStatistics(sensor.Id, fromUtc), fromUtc);
            var uptime = uptimeSummary.Percent;

            latest.TryGetValue(sensor.Id, out var observation);
            var unit = ResolveUnit(observation);
            lines.Add(new SummaryReportSensorLine(
                BuildPath(sensor.Id, elementsById),
                observation?.State ?? SensorState.Unknown,
                uptime,
                observation?.Value,
                unit,
                (int)Math.Min(int.MaxValue, uptimeSummary.Samples)));
        }

        var lowestUptime = lines
            .OrderBy(line => line.UptimePercent ?? double.MaxValue)
            .ThenByDescending(line => StateSeverity(line.State))
            .Take(Math.Max(1, maxSensorLines))
            .ToArray();

        var recentEvents = _workspaceStore.GetEvents(200)
            .Where(monitoringEvent => monitoringEvent.TimestampUtc >= fromUtc)
            .Take(25)
            .Select(monitoringEvent => new SummaryReportEventLine(
                monitoringEvent.TimestampUtc,
                monitoringEvent.Kind.ToString(),
                monitoringEvent.ElementPath,
                monitoringEvent.Message))
            .ToArray();

        var branding = Reports.ReportDataCollector.Branding(_workspaceStore);

        return new SummaryReportData(
            workspaceName,
            fromUtc,
            nowUtc,
            probeCount,
            sensors.Length,
            pausedIds.Count,
            openAlerts,
            acknowledgedAlerts,
            errorCount,
            warningCount,
            lowestUptime,
            recentEvents,
            branding);
    }

    private static string? ResolveUnit(SensorObservation? observation)
    {
        if (observation?.Channels is not { Count: > 0 } channels)
        {
            return null;
        }

        return channels.FirstOrDefault(channel => !channel.IsVirtual &&
                   !string.IsNullOrWhiteSpace(observation.DefaultChannelKey) &&
                   string.Equals(channel.Key, observation.DefaultChannelKey, StringComparison.OrdinalIgnoreCase))?.Unit
               ?? channels.FirstOrDefault(channel => channel.IsDefault && !channel.IsVirtual)?.Unit;
    }

    private static int StateSeverity(SensorState state) => state switch
    {
        SensorState.Critical => 3,
        SensorState.Warning => 2,
        SensorState.Unknown => 1,
        _ => 0
    };

    private static string BuildPath(Guid elementId, IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        var names = new List<string>();
        var current = elementId;
        var guard = 0;
        while (elementsById.TryGetValue(current, out var element) && guard++ < 256)
        {
            names.Add(element.Name);
            if (element.ParentId is not Guid parent)
            {
                break;
            }

            current = parent;
        }

        names.Reverse();
        return string.Join(" / ", names);
    }
}

/// <summary>Builds and sends the summary report. Reusable by the scheduler and an on-demand "send now"; the delivery
/// (sender, receiver, SMTP or the cloud gateway) is the shared <see cref="Reports.ReportMailer"/>.</summary>
public sealed class SummaryReportSender
{
    private const int PdfSensorLines = 200;

    private readonly SummaryReportDataCollector _collector;
    private readonly AuditReportPdfBuilder _pdfBuilder;
    private readonly Reports.ReportMailer _mailer;
    private readonly ILogger<SummaryReportSender> _logger;

    public SummaryReportSender(
        SummaryReportDataCollector collector,
        AuditReportPdfBuilder pdfBuilder,
        Reports.ReportMailer mailer,
        ILogger<SummaryReportSender> logger)
    {
        _collector = collector;
        _pdfBuilder = pdfBuilder;
        _mailer = mailer;
        _logger = logger;
    }

    public async Task<bool> SendAsync(SummaryReportSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var now = DateTimeOffset.UtcNow;
        var window = WindowFor(settings.Cadence);
        var report = SummaryReportBuilder.Build(_collector.Collect(now, window));
        var subject = string.IsNullOrWhiteSpace(settings.Subject) ? report.Subject : settings.Subject;

        // A fuller sensor list for the PDF than the (short) e-mail body.
        IReadOnlyList<EmailAttachment> attachments = settings.AttachPdf
            ? [new EmailAttachment($"matmon-audit-{now:yyyyMMdd}.pdf", _pdfBuilder.Build(_collector.Collect(now, window, PdfSensorLines)), "application/pdf")]
            : [];

        var result = await _mailer.SendAsync(settings.SenderId, settings.ReceiverId, settings.Recipients, subject, report.TextBody, report.HtmlBody, attachments, cancellationToken);
        if (result.Sent)
        {
            _logger.LogInformation("Summary report: {Result}", result.Message);
        }
        else
        {
            _logger.LogWarning("Summary report: {Result}", result.Message);
        }

        return result.Sent;
    }

    /// <summary>Renders the report for an on-screen preview (subject + HTML body), sending nothing - so the admin
    /// can see exactly what the scheduled e-mail will contain, from live data, without SMTP or the cloud.</summary>
    public (string Subject, string HtmlBody) BuildPreview(SummaryReportCadence cadence)
    {
        var report = SummaryReportBuilder.Build(_collector.Collect(DateTimeOffset.UtcNow, WindowFor(cadence)));
        return (report.Subject, report.HtmlBody);
    }

    /// <summary>Builds the standalone PDF audit report for download (independent of e-mail delivery).</summary>
    public byte[] BuildAuditPdf(SummaryReportCadence cadence)
    {
        var data = _collector.Collect(DateTimeOffset.UtcNow, WindowFor(cadence), PdfSensorLines);
        return _pdfBuilder.Build(data);
    }

    private static TimeSpan WindowFor(SummaryReportCadence cadence) =>
        cadence == SummaryReportCadence.Weekly ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
}

/// <summary>Primary-only scheduler: sends the summary report and every saved report whose scheduled slot has passed.</summary>
public sealed class ReportSchedulerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly SummaryReportSender _sender;
    private readonly Reports.ReportService _reports;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly ILogger<ReportSchedulerService> _logger;

    public ReportSchedulerService(
        IMonitoringWorkspaceStore workspaceStore,
        SummaryReportSender sender,
        Reports.ReportService reports,
        MatmonRuntimeOptions runtimeOptions,
        ILogger<ReportSchedulerService> logger)
    {
        _workspaceStore = workspaceStore;
        _sender = sender;
        _reports = reports;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtimeOptions.Mode != AppMode.Primary)
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var settings = _workspaceStore.GetSummaryReportSettings();
                if (SummaryReportSchedule.IsDue(settings, DateTimeOffset.Now))
                {
                    // Marked whatever the outcome, so a misconfigured report doesn't retry every minute; a fix + the
                    // next slot pick it back up.
                    await _sender.SendAsync(settings, stoppingToken);
                    _workspaceStore.MarkSummaryReportSent(DateTimeOffset.UtcNow);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Includes a network-send timeout (TaskCanceledException) - log + keep the scheduler alive.
                _logger.LogError(ex, "Summary report scheduler tick failed");
            }

            await SendSavedReportsAsync(stoppingToken);
        }
    }

    private async Task SendSavedReportsAsync(CancellationToken stoppingToken)
    {
        var zone = _workspaceStore.ReportZone();
        foreach (var report in _workspaceStore.GetReports())
        {
            if (stoppingToken.IsCancellationRequested || !Matmon.Core.Domain.ReportRules.IsDue(report, DateTimeOffset.UtcNow, zone))
            {
                continue;
            }

            try
            {
                var result = await _reports.SendAsync(report, stoppingToken);
                _workspaceStore.MarkReportSent(report.Id, DateTimeOffset.UtcNow, !result.Sent, result.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled report {Report} failed", report.Name);
                _workspaceStore.MarkReportSent(report.Id, DateTimeOffset.UtcNow, true, $"Not sent: {ex.Message}");
            }
        }
    }
}
