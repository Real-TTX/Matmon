using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matmon.Core;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class SlaveSensorWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IReadOnlyDictionary<string, ISensorExecutor> _executors;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly SlaveProbeRuntimeState _runtimeState;
    private readonly NetworkDiscoveryService _discoveryService;
    private readonly ILogger<SlaveSensorWorker> _logger;
    private readonly Dictionary<Guid, DateTimeOffset> _lastExecutedUtc = new();

    public SlaveSensorWorker(
        IHttpClientFactory httpClientFactory,
        IEnumerable<ISensorExecutor> executors,
        MatmonRuntimeOptions runtimeOptions,
        SlaveProbeRuntimeState runtimeState,
        NetworkDiscoveryService discoveryService,
        ILogger<SlaveSensorWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _executors = executors.ToDictionary(executor => executor.SensorTypeKey, StringComparer.OrdinalIgnoreCase);
        _runtimeOptions = runtimeOptions;
        _runtimeState = runtimeState;
        _discoveryService = discoveryService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtimeOptions.Mode != AppMode.Secondary)
        {
            return;
        }

        if (!Uri.TryCreate(_runtimeOptions.PrimaryUrl, UriKind.Absolute, out var primaryUri))
        {
            _runtimeState.RecordAssignmentSync(0, "No valid primary URL configured.", success: false);
            return;
        }

        var probeId = string.IsNullOrWhiteSpace(_runtimeOptions.ProbeId)
            ? Environment.MachineName
            : _runtimeOptions.ProbeId;
        var interval = TimeSpan.FromSeconds(5);
        var client = _httpClientFactory.CreateClient("SecondarySensorWorker");
        client.BaseAddress = primaryUri;

        _logger.LogInformation("Secondary sensor worker started for {ProbeId} against {PrimaryUrl}", probeId, primaryUri);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAssignmentsAsync(client, probeId, stoppingToken);
                await SyncDiscoveryJobsAsync(client, probeId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _runtimeState.RecordAssignmentSync(0, ex.Message, success: false);
                _logger.LogWarning(ex, "Secondary sensor sync failed for {ProbeId}", probeId);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SyncAssignmentsAsync(HttpClient client, string probeId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/probes/{Uri.EscapeDataString(probeId)}/assignments");
        AddProbeToken(request);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = $"assignment sync returned {(int)response.StatusCode} {response.ReasonPhrase}";
            _runtimeState.RecordAssignmentSync(0, message, success: false);
            return;
        }

        var assignments = await response.Content.ReadFromJsonAsync<ProbeSensorAssignmentsResponse>(JsonOptions, cancellationToken);
        if (assignments is null)
        {
            _runtimeState.RecordAssignmentSync(0, "assignment response was empty", success: false);
            return;
        }

        _runtimeState.RecordAssignmentSync(
            assignments.Sensors.Count,
            $"{assignments.Sensors.Count} sensor assignment{(assignments.Sensors.Count == 1 ? string.Empty : "s")} received",
            success: true);

        var reports = new List<ProbeSensorObservationReport>();
        var pendingResults = new List<SlaveProbePendingResult>();
        var upcomingExecutions = new List<SlaveProbeUpcomingExecution>();
        var now = DateTimeOffset.UtcNow;
        foreach (var assignment in assignments.Sensors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (assignment.IsPaused || assignment.Settings.Enabled == false)
            {
                continue;
            }

            var lastExecutedUtc = _lastExecutedUtc.TryGetValue(assignment.SensorId, out var localLastExecuted)
                ? localLastExecuted
                : assignment.LastObservationUtc;
            upcomingExecutions.Add(new SlaveProbeUpcomingExecution(
                assignment.SensorId,
                assignment.Name,
                assignment.Path,
                assignment.SensorTypeKey,
                MonitoringScheduleCalculator.GetNextDueUtc(
                    assignment.Settings,
                    lastExecutedUtc,
                    now,
                    TimeSpan.FromSeconds(15)),
                lastExecutedUtc,
                BuildScheduleSummary(assignment.Settings)));

            if (!MonitoringScheduleCalculator.IsDue(assignment.Settings, lastExecutedUtc, now, TimeSpan.FromSeconds(15)))
            {
                continue;
            }

            var result = await ExecuteAssignmentAsync(assignment, cancellationToken);
            var executedUtc = DateTimeOffset.UtcNow;
            reports.Add(new ProbeSensorObservationReport(assignment.SensorId, result, executedUtc));
            pendingResults.Add(new SlaveProbePendingResult(
                assignment.SensorId,
                assignment.Name,
                assignment.Path,
                result.State,
                MonitoringStatePresentation.Key(result.State),
                MonitoringStatePresentation.Label(result.State),
                result.Message,
                executedUtc));
            _lastExecutedUtc[assignment.SensorId] = executedUtc;
        }

        _runtimeState.UpdateUpcomingExecutions(upcomingExecutions);

        if (reports.Count == 0)
        {
            return;
        }

        await PostResultsAsync(client, probeId, reports, pendingResults, cancellationToken);
    }

    private async ValueTask<SensorExecutionResult> ExecuteAssignmentAsync(
        ProbeSensorAssignment assignment,
        CancellationToken cancellationToken)
    {
        if (!_executors.TryGetValue(assignment.SensorTypeKey, out var executor))
        {
            var missingResult = SensorExecutionResult.Critical(TimeSpan.Zero, $"No executor is registered for sensor type '{assignment.SensorTypeKey}'.");
            _runtimeState.RecordExecution(assignment.Name, missingResult.Message ?? "executor missing", success: false);
            return missingResult;
        }

        try
        {
            var result = await executor.ExecuteAsync(
                new SensorExecutionContext(assignment.SensorTypeKey, assignment.Target, assignment.Settings),
                cancellationToken);
            result = SensorExecutionResultHelper.ApplyDefaultChannelSelection(assignment.Settings, result);

            _runtimeState.RecordExecution(
                assignment.Name,
                $"{MonitoringStatePresentation.Label(result.State)} - {result.Message ?? "ok"}",
                result.State is not SensorState.Critical);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runtimeState.RecordExecution(assignment.Name, ex.Message, success: false);
            return SensorExecutionResult.Critical(TimeSpan.Zero, ex.Message);
        }
    }

    private async Task PostResultsAsync(
        HttpClient client,
        string probeId,
        IReadOnlyList<ProbeSensorObservationReport> reports,
        IReadOnlyList<SlaveProbePendingResult> pendingResults,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/probes/{Uri.EscapeDataString(probeId)}/observations")
        {
            Content = JsonContent.Create(new ProbeSensorObservationBatch(reports), options: JsonOptions)
        };
        AddProbeToken(request);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var message = $"{reports.Count} result{(reports.Count == 1 ? string.Empty : "s")} posted: {(int)response.StatusCode} {response.ReasonPhrase}";
            _runtimeState.RecordResultPost(reports.Count, message, response.IsSuccessStatusCode, pendingResults);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"{reports.Count} result{(reports.Count == 1 ? string.Empty : "s")} transfer failed: {ex.Message}";
            _runtimeState.RecordResultPost(reports.Count, message, success: false, pendingResults);
        }
    }

    private async Task SyncDiscoveryJobsAsync(HttpClient client, string probeId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/probes/{Uri.EscapeDataString(probeId)}/discovery-jobs");
        AddProbeToken(request);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var assignments = await response.Content.ReadFromJsonAsync<ProbeDiscoveryJobAssignmentsResponse>(JsonOptions, cancellationToken);
        if (assignments is null || assignments.Jobs.Count == 0)
        {
            return;
        }

        foreach (var job in assignments.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var lastReportedScannedHosts = 0;
                var hosts = await _discoveryService.DiscoverAsync(
                    new NetworkDiscoveryRequest(job.JobId, job.Network, job.Options),
                    async (host, token) =>
                    {
                        await PostDiscoveryResultsAsync(
                            client,
                            probeId,
                            [new ProbeDiscoveryJobResult(job.JobId, [host], null, IsComplete: false)],
                            token);
                    },
                    cancellationToken,
                    async (progress, token) =>
                    {
                        var reportEvery = Math.Max(progress.TotalHosts / 100, 1);
                        var shouldReport =
                            progress.ScannedHosts >= progress.TotalHosts ||
                            progress.ScannedHosts - Volatile.Read(ref lastReportedScannedHosts) >= reportEvery;
                        if (!shouldReport)
                        {
                            return;
                        }

                        var previous = Interlocked.Exchange(ref lastReportedScannedHosts, progress.ScannedHosts);
                        if (previous >= progress.ScannedHosts)
                        {
                            return;
                        }

                        await PostDiscoveryResultsAsync(
                            client,
                            probeId,
                            [new ProbeDiscoveryJobResult(
                                job.JobId,
                                [],
                                null,
                                IsComplete: false,
                                progress.ScannedHosts,
                                progress.TotalHosts)],
                            token);
                    });
                await PostDiscoveryResultsAsync(
                    client,
                    probeId,
                    [new ProbeDiscoveryJobResult(job.JobId, [], null, IsComplete: true)],
                    cancellationToken);
                _runtimeState.RecordExecution(
                    $"Discovery {job.Network}",
                    $"{hosts.Count} host{(hosts.Count == 1 ? string.Empty : "s")} discovered",
                    success: true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _runtimeState.RecordExecution($"Discovery {job.Network}", "cancelled by primary", success: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await PostDiscoveryResultsAsync(
                    client,
                    probeId,
                    [new ProbeDiscoveryJobResult(job.JobId, [], ex.Message, IsComplete: true)],
                    cancellationToken);
                _runtimeState.RecordExecution($"Discovery {job.Network}", ex.Message, success: false);
            }
        }
    }

    private async Task PostDiscoveryResultsAsync(
        HttpClient client,
        string probeId,
        IReadOnlyList<ProbeDiscoveryJobResult> results,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }

        using var postRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/probes/{Uri.EscapeDataString(probeId)}/discovery-jobs/results")
        {
            Content = JsonContent.Create(new ProbeDiscoveryJobResultBatch(results), options: JsonOptions)
        };
        AddProbeToken(postRequest);

        using var postResponse = await client.SendAsync(postRequest, cancellationToken);
        var cancelled = false;
        if (postResponse.IsSuccessStatusCode)
        {
            var result = await postResponse.Content.ReadFromJsonAsync<ProbeDiscoveryJobResultPostResponse>(JsonOptions, cancellationToken);
            cancelled = result?.Cancelled == true;
        }

        _runtimeState.RecordResultPost(
            results.Count,
            cancelled
                ? "discovery job was cancelled by primary"
                : $"{results.Count} discovery result{(results.Count == 1 ? string.Empty : "s")} posted: {(int)postResponse.StatusCode} {postResponse.ReasonPhrase}",
            postResponse.IsSuccessStatusCode);

        if (cancelled)
        {
            throw new OperationCanceledException("Discovery job was cancelled by primary.");
        }
    }

    private void AddProbeToken(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_runtimeOptions.ProbeToken))
        {
            request.Headers.TryAddWithoutValidation("X-Matmon-Probe-Token", _runtimeOptions.ProbeToken);
        }
    }

    private static string BuildScheduleSummary(MonitoringSettings settings)
    {
        if (settings.PollingSchedule is { } schedule)
        {
            return schedule.Summary();
        }

        return $"every {MonitoringSchedule.FormatDuration(settings.PollingInterval ?? TimeSpan.FromSeconds(15))}";
    }

}
