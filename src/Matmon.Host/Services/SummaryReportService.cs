using System.Net.Http;
using System.Net.Http.Json;
using Matmon.Core;
using Matmon.Core.Domain;

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

            // Dedupe buckets by window start (statistics are per channel; the state distribution is the
            // same across a sensor's channels, so one bucket per window avoids double counting).
            var buckets = _workspaceStore.GetSensorStatistics(sensor.Id)
                .Where(bucket => bucket.BucketStartUtc >= fromUtc)
                .GroupBy(bucket => bucket.BucketStartUtc)
                .Select(group => group.First())
                .ToArray();

            var healthy = buckets.Sum(bucket => bucket.HealthyCount);
            var warning = buckets.Sum(bucket => bucket.WarningCount);
            var critical = buckets.Sum(bucket => bucket.CriticalCount);
            var stateSamples = healthy + warning + critical;
            double? uptime = stateSamples > 0 ? (double)(healthy + warning) / stateSamples * 100 : null;

            latest.TryGetValue(sensor.Id, out var observation);
            var unit = ResolveUnit(observation);
            lines.Add(new SummaryReportSensorLine(
                BuildPath(sensor.Id, elementsById),
                observation?.State ?? SensorState.Unknown,
                uptime,
                observation?.Value,
                unit,
                buckets.Sum(bucket => bucket.SampleCount)));
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

        var partner = _workspaceStore.GetServicePartnerInfo();
        var branding = partner is { HasPartner: true }
            ? new SummaryReportBranding(partner.Name, partner.LogoPng, partner.LogoContentType, partner.BrandColor, partner.ContactUrl)
            : null;

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

/// <summary>Builds and sends the summary report. Reusable by the scheduler and an on-demand "send now".</summary>
public sealed class SummaryReportSender
{
    private const int PdfSensorLines = 200;

    private readonly SummaryReportDataCollector _collector;
    private readonly INotificationEmailSender _emailSender;
    private readonly AuditReportPdfBuilder _pdfBuilder;
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly ILogger<SummaryReportSender> _logger;

    public SummaryReportSender(
        SummaryReportDataCollector collector,
        INotificationEmailSender emailSender,
        AuditReportPdfBuilder pdfBuilder,
        IMonitoringWorkspaceStore workspaceStore,
        ILogger<SummaryReportSender> logger)
    {
        _collector = collector;
        _emailSender = emailSender;
        _pdfBuilder = pdfBuilder;
        _workspaceStore = workspaceStore;
        _logger = logger;
    }

    public async Task<bool> SendAsync(SummaryReportSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var recipients = ResolveRecipients(settings);
        if (string.IsNullOrWhiteSpace(recipients))
        {
            _logger.LogWarning("Summary report not sent: no recipients configured");
            return false;
        }

        // A chosen "Cloud" sender routes the report through the cloud gateway instead of local SMTP - so an
        // instance with no SMTP but a connected cloud link can still e-mail its summary + PDF.
        var chosenSender = settings.SenderId is { } senderId
            ? _workspaceStore.Workspace.NotificationSenders.FirstOrDefault(s => s.Id == senderId)
            : null;
        var viaCloud = chosenSender is { Kind: NotificationEndpointKind.Cloud };

        var now = DateTimeOffset.UtcNow;
        var window = WindowFor(settings.Cadence);
        var report = SummaryReportBuilder.Build(_collector.Collect(now, window));
        var subject = string.IsNullOrWhiteSpace(settings.Subject) ? report.Subject : settings.Subject;

        // Collect a fuller sensor list for the PDF than the (short) e-mail body.
        var pdf = settings.AttachPdf ? _pdfBuilder.Build(_collector.Collect(now, window, PdfSensorLines)) : null;

        if (viaCloud)
        {
            return await SendViaCloudAsync(recipients, subject, report.TextBody, report.HtmlBody, pdf, now, cancellationToken);
        }

        var smtp = ResolveSmtp(settings);
        if (smtp is null)
        {
            _logger.LogWarning("Summary report not sent: no SMTP sender configured");
            return false;
        }

        IReadOnlyList<EmailAttachment>? attachments = pdf is null
            ? null
            : [new EmailAttachment($"matmon-audit-{now:yyyyMMdd}.pdf", pdf, "application/pdf")];

        await _emailSender.SendAsync(smtp, recipients, subject, report.TextBody, report.HtmlBody, cancellationToken, attachments);
        _logger.LogInformation("Summary report sent to {Recipients}", recipients);
        return true;
    }

    private static readonly HttpClient CloudHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Relay the summary report (subject/body + optional PDF) through the connected Matmon.Cloud gateway
    /// (POST /api/instances/{id}/notify, instance-token auth) - the cloud spools + retries it like every cloud mail.</summary>
    private async Task<bool> SendViaCloudAsync(string recipients, string subject, string? text, string? html, byte[]? pdf, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cloud = _workspaceStore.GetCloudConnectionSettings();
        if (!cloud.Configured || string.IsNullOrWhiteSpace(cloud.Url) || string.IsNullOrWhiteSpace(cloud.InstanceId))
        {
            _logger.LogWarning("Summary report not sent: Cloud sender chosen but the cloud link isn't configured.");
            return false;
        }
        var token = _workspaceStore.GetCloudConnectionToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Summary report not sent: Cloud sender chosen but no cloud token is stored.");
            return false;
        }

        var url = $"{cloud.Url!.Trim().TrimEnd('/')}/api/instances/{cloud.InstanceId!.Trim()}/notify";
        object? attachment = pdf is null
            ? null
            : new { FileName = $"matmon-audit-{now:yyyyMMdd}.pdf", ContentType = "application/pdf", Content = pdf };
        var body = new { Channel = "email", To = recipients, Subject = subject, Text = text, Html = html, Attachment = attachment };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await CloudHttp.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Summary report relayed to Matmon.Cloud -> {Recipients}", recipients);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Summary report relay to Matmon.Cloud failed");
            return false;
        }
    }

    /// <summary>Recipient(s): the chosen receiver's target, else the free-text recipients list.</summary>
    private string ResolveRecipients(SummaryReportSettings settings)
    {
        if (settings.ReceiverId is { } receiverId)
        {
            if (NotificationReceiverDefaults.IsBuiltIn(receiverId))
            {
                return _workspaceStore.ResolveBuiltInRecipients(receiverId);
            }

            var receiver = _workspaceStore.Workspace.NotificationReceivers.FirstOrDefault(r => r.Id == receiverId);
            if (receiver is not null && !string.IsNullOrWhiteSpace(receiver.Target))
            {
                return receiver.Target;
            }
        }

        return settings.Recipients;
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

    private EmailNotificationSettings? ResolveSmtp(SummaryReportSettings settings)
    {
        var workspace = _workspaceStore.Workspace;

        // A chosen sender wins (lets the user pick a specific SMTP endpoint, like a trigger rule does).
        if (settings.SenderId is { } senderId)
        {
            var chosen = workspace.NotificationSenders.FirstOrDefault(s => s.Id == senderId);
            if (chosen is not null && chosen.Kind == NotificationEndpointKind.Email && !string.IsNullOrWhiteSpace(chosen.Email.SmtpHost))
            {
                return chosen.Email;
            }
        }

        var configured = workspace.NotificationConfiguration.Email;
        if (!string.IsNullOrWhiteSpace(configured.SmtpHost))
        {
            return configured;
        }

        var sender = workspace.NotificationSenders.FirstOrDefault(candidate =>
            candidate.Enabled && candidate.Kind == NotificationEndpointKind.Email && !string.IsNullOrWhiteSpace(candidate.Email.SmtpHost));
        return sender?.Email;
    }
}

/// <summary>Primary-only scheduler: sends the summary report when a scheduled slot has passed.</summary>
public sealed class ReportSchedulerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly SummaryReportSender _sender;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly ILogger<ReportSchedulerService> _logger;

    public ReportSchedulerService(
        IMonitoringWorkspaceStore workspaceStore,
        SummaryReportSender sender,
        MatmonRuntimeOptions runtimeOptions,
        ILogger<ReportSchedulerService> logger)
    {
        _workspaceStore = workspaceStore;
        _sender = sender;
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
                if (!SummaryReportSchedule.IsDue(settings, DateTimeOffset.Now))
                {
                    continue;
                }

                if (await _sender.SendAsync(settings, stoppingToken))
                {
                    _workspaceStore.MarkSummaryReportSent(DateTimeOffset.UtcNow);
                }
                else
                {
                    // Mark anyway so a misconfigured report doesn't retry every minute; a fix + the next
                    // slot will pick it back up.
                    _workspaceStore.MarkSummaryReportSent(DateTimeOffset.UtcNow);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Summary report scheduler tick failed");
            }
        }
    }
}
