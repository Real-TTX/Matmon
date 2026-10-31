using System.Collections.Concurrent;
using System.Net.Http.Json;
using Matmon.Core.Domain;

namespace Matmon.Host.Services.Reports;

/// <summary>A partner report the cloud asks for with a heartbeat answer (<c>buildReports</c>): this instance's part,
/// covering everything it monitors, for one period.</summary>
public sealed record CloudReportRequest(Guid Id, DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? Label, string? Title, string? PreparedFor, string? Note, int Sections, double? SlaTargetPercent);

/// <summary>
/// Makes the part of a partner availability report the cloud asked for - the same report as the Reports page, over the
/// whole instance, in the partner's branding the instance already holds - and uploads it with its figures to
/// <c>POST /api/instances/{id}/reports/{partId}</c>. In the background, one at a time; a request already handled is not
/// made again (the cloud repeats one it got no answer for after ten minutes - that one IS made again, it may have been
/// lost). The cloud cannot reach the instance, which is why this rides the heartbeat like the update commands.
/// </summary>
public sealed class CloudReportRelay(ReportService reports, IMonitoringWorkspaceStore store, ILogger<CloudReportRelay> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _handled = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public void Handle(IReadOnlyList<CloudReportRequest>? requests, string baseUrl, string instanceId, string token)
    {
        if (requests is not { Count: > 0 })
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var stale in _handled.Where(entry => now - entry.Value > TimeSpan.FromMinutes(8)).Select(entry => entry.Key).ToArray())
        {
            _handled.TryRemove(stale, out _);
        }

        foreach (var request in requests)
        {
            if (!_handled.TryAdd(request.Id, now))
            {
                continue;
            }

            _ = Task.Run(() => MakeAndUploadAsync(request, baseUrl, instanceId, token));
        }
    }

    private async Task MakeAndUploadAsync(CloudReportRequest request, string baseUrl, string instanceId, string token)
    {
        await _oneAtATime.WaitAsync();
        try
        {
            object body;
            try
            {
                var root = store.GetAllElements().OfType<ProbeElement>().FirstOrDefault(probe => probe.ParentId is null)
                           ?? throw new InvalidOperationException("This instance has no root probe.");
                var now = DateTimeOffset.UtcNow;
                var to = request.ToUtc > now ? now : request.ToUtc;
                var window = new ReportWindow(request.FromUtc, to, string.IsNullOrWhiteSpace(request.Label) ? "Report" : request.Label!);
                var sections = (ReportSections)request.Sections & ReportSections.All;
                var data = reports.Collect(new ReportRequest(
                    MonitoringTargetResolver.ForElement(root.Id),
                    window,
                    request.Title,
                    request.PreparedFor,
                    request.Note,
                    sections == ReportSections.None ? ReportSections.All : sections,
                    request.SlaTargetPercent), now);
                var pdf = reports.Pdf(data);
                body = new
                {
                    pdf = Convert.ToBase64String(pdf),
                    figures = new
                    {
                        availability = data.Overall.Percent,
                        healthy = data.Overall.Healthy,
                        warning = data.Overall.Warning,
                        critical = data.Overall.Critical,
                        sensors = data.SensorCount,
                        sensorsWithoutData = data.SensorsWithoutData,
                        outages = data.OutageCount,
                        affectedSensors = data.SensorsWithOutages,
                        longestOutageSeconds = data.LongestOutage.TotalSeconds,
                        alerts = data.Alerts.Count,
                        belowTarget = data.SensorsBelowTarget,
                        version = MatmonVersion.Current
                    }
                };
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Partner report part {Part} could not be made", request.Id);
                body = new { error = $"The instance could not make its report: {exception.Message}" };
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/instances/{instanceId}/reports/{request.Id}")
            {
                Content = JsonContent.Create(body)
            };
            message.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await Http.SendAsync(message);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 404 = the cloud no longer waits for it (finished without it, or deleted) - nothing to retry.
                logger.LogInformation("Partner report part {Part} uploaded ({Status})", request.Id, (int)response.StatusCode);
            }
            else
            {
                logger.LogWarning("Partner report part {Part}: Matmon.Cloud answered {Status}", request.Id, (int)response.StatusCode);
                _handled.TryRemove(request.Id, out _);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Partner report part {Part} could not be uploaded", request.Id);
            _handled.TryRemove(request.Id, out _);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
