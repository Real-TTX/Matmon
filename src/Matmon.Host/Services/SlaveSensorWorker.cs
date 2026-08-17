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
                await SyncRunJobsAsync(client, probeId, stoppingToken);
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
            // Same per-sensor-type fallback cadence the primary's SensorPollingService uses, instead of
            // a flat 15s, so a secondary polls slow-changing sensors (disks, updates) at a sane rate.
            var fallbackInterval = SensorScheduleDefaults.Resolve(assignment.SensorTypeKey);
            upcomingExecutions.Add(BuildUpcomingExecution(assignment, lastExecutedUtc, now, fallbackInterval));

            if (!MonitoringScheduleCalculator.IsDue(assignment.Settings, lastExecutedUtc, now, fallbackInterval))
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

        PruneStaleLastExecuted(assignments.Sensors);

        _runtimeState.UpdateUpcomingExecutions(upcomingExecutions);

        if (reports.Count == 0)
        {
            return;
        }

        await PostResultsAsync(client, probeId, reports, pendingResults, cancellationToken);
    }

    private static SlaveProbeUpcomingExecution BuildUpcomingExecution(
        ProbeSensorAssignment assignment,
        DateTimeOffset? lastExecutedUtc,
        DateTimeOffset now,
        TimeSpan fallbackInterval)
    {
        return new SlaveProbeUpcomingExecution(
            assignment.SensorId,
            assignment.Name,
            assignment.Path,
            assignment.SensorTypeKey,
            MonitoringScheduleCalculator.GetNextDueUtc(
                assignment.Settings,
                lastExecutedUtc,
                now,
                fallbackInterval),
            lastExecutedUtc,
            BuildScheduleSummary(assignment.Settings));
    }

    /// <summary>Drops last-run bookkeeping for sensors no longer assigned, so the dictionary can't grow
    /// unbounded as sensors are added and removed over the probe's lifetime.</summary>
    private void PruneStaleLastExecuted(IReadOnlyList<ProbeSensorAssignment> assignments)
    {
        if (_lastExecutedUtc.Count <= assignments.Count)
        {
            return;
        }

        var assignedIds = assignments.Select(sensor => sensor.SensorId).ToHashSet();
        foreach (var staleId in _lastExecutedUtc.Keys.Where(id => !assignedIds.Contains(id)).ToArray())
        {
            _lastExecutedUtc.Remove(staleId);
        }
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
                new SensorExecutionContext(
                    assignment.SensorTypeKey,
                    assignment.Target,
                    assignment.Settings,
                    assignment.SensorId,
                    assignment.LastObservation),
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

    /// <summary>Pulls on-demand run jobs the primary queued for this probe (a "Run now"/"Test"/"Run subtree"
    /// or an SNMP-discover on a sensor that lives under this remote probe), runs each locally and posts the
    /// results back. Tolerates a missing endpoint (older primary) silently so the loop keeps running.</summary>
    private async Task SyncRunJobsAsync(HttpClient client, string probeId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/probes/{Uri.EscapeDataString(probeId)}/run-jobs");
        AddProbeToken(request);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var assignments = await response.Content.ReadFromJsonAsync<ProbeRunJobAssignmentsResponse>(JsonOptions, cancellationToken);
        if (assignments is null || assignments.Jobs.Count == 0)
        {
            return;
        }

        var results = new List<ProbeRunJobResult>(assignments.Jobs.Count);
        foreach (var job in assignments.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunOnDemandJobAsync(job, cancellationToken));
        }

        await PostRunJobResultsAsync(client, probeId, results, cancellationToken);
    }

    private async Task<ProbeRunJobResult> RunOnDemandJobAsync(ProbeRunJobAssignment job, CancellationToken cancellationToken)
    {
        try
        {
            if (job.Kind == ProbeRunJobKind.SnmpDiscover)
            {
                // The DTO has no root-OID field, so the primary smuggled it through the settings.
                var rootOid = job.Settings.Parameters.TryGetValue(ProbeRunJobParameters.SnmpDiscoverRootOid, out var configuredRoot)
                    && !string.IsNullOrWhiteSpace(configuredRoot)
                    ? configuredRoot.Trim()
                    : "1.3.6.1.2.1";
                var oids = await SnmpSensorExecutor.DiscoverAsync(
                    job.Target,
                    job.Settings,
                    rootOid,
                    job.Settings.Timeout ?? TimeSpan.FromSeconds(5),
                    cancellationToken);
                return new ProbeRunJobResult(job.JobId, null, oids, null, DateTimeOffset.UtcNow);
            }

            if (!_executors.TryGetValue(job.SensorTypeKey, out var executor))
            {
                return new ProbeRunJobResult(
                    job.JobId,
                    SensorExecutionResult.Critical(TimeSpan.Zero, $"No executor is registered for sensor type '{job.SensorTypeKey}'."),
                    null,
                    null,
                    DateTimeOffset.UtcNow);
            }

            // Same execution path as a scheduled assignment (see ExecuteAssignmentAsync), including the
            // default-channel selection. No previous observation is threaded through for an on-demand run.
            var result = await executor.ExecuteAsync(
                new SensorExecutionContext(
                    job.SensorTypeKey,
                    job.Target,
                    job.Settings,
                    job.SensorId,
                    previousObservation: null),
                cancellationToken);
            result = SensorExecutionResultHelper.ApplyDefaultChannelSelection(job.Settings, result);
            return new ProbeRunJobResult(job.JobId, result, null, null, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return job.Kind == ProbeRunJobKind.SnmpDiscover
                ? new ProbeRunJobResult(job.JobId, null, null, ex.Message, DateTimeOffset.UtcNow)
                : new ProbeRunJobResult(job.JobId, SensorExecutionResult.Critical(TimeSpan.Zero, ex.Message), null, null, DateTimeOffset.UtcNow);
        }
    }

    private async Task PostRunJobResultsAsync(
        HttpClient client,
        string probeId,
        IReadOnlyList<ProbeRunJobResult> results,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/probes/{Uri.EscapeDataString(probeId)}/run-jobs/results")
        {
            Content = JsonContent.Create(new ProbeRunJobResultBatch(results), options: JsonOptions)
        };
        AddProbeToken(request);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            _runtimeState.RecordResultPost(
                results.Count,
                $"{results.Count} on-demand result{(results.Count == 1 ? string.Empty : "s")} posted: {(int)response.StatusCode} {response.ReasonPhrase}",
                response.IsSuccessStatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runtimeState.RecordResultPost(
                results.Count,
                $"{results.Count} on-demand result{(results.Count == 1 ? string.Empty : "s")} transfer failed: {ex.Message}",
                success: false);
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
            await RunDiscoveryJobAsync(client, probeId, job, cancellationToken);
        }
    }

    /// <summary>Runs one assigned discovery job: streams every found host to the primary as it appears, reports
    /// scan progress, and always closes the job out (complete, cancelled by the primary, or failed).</summary>
    private async Task RunDiscoveryJobAsync(
        HttpClient client,
        string probeId,
        ProbeDiscoveryJobAssignment job,
        CancellationToken cancellationToken)
    {
        try
        {
            var lastReportedScannedHosts = 0;

            async ValueTask ReportHostAsync(NetworkDiscoveryResult host, CancellationToken token)
            {
                await PostDiscoveryResultsAsync(
                    client,
                    probeId,
                    [new ProbeDiscoveryJobResult(job.JobId, [host], null, IsComplete: false)],
                    token);
            }

            // Progress is reported at most ~1% of the scan range at a time (and always on the final host), so a
            // large subnet doesn't flood the primary with progress posts.
            async ValueTask ReportProgressAsync(NetworkDiscoveryProgress progress, CancellationToken token)
            {
                var reportEvery = Math.Max(progress.TotalHosts / 100, 1);
                var shouldReport =
                    progress.ScannedHosts >= progress.TotalHosts ||
                    progress.ScannedHosts - Volatile.Read(ref lastReportedScannedHosts) >= reportEvery;
                if (!shouldReport)
                {
                    return;
                }

                // Discovery workers report concurrently, so drop an out-of-order (already superseded) count.
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
            }

            var hosts = await _discoveryService.DiscoverAsync(
                new NetworkDiscoveryRequest(job.JobId, job.Network, job.Options),
                ReportHostAsync,
                cancellationToken,
                ReportProgressAsync);
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
