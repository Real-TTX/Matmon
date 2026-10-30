using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matmon.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Matmon.Core.Domain;

namespace Matmon.Probe;

public sealed class SlaveSensorWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IReadOnlyDictionary<string, ISensorExecutor> _executors;
    private readonly ProbeRuntimeOptions _runtimeOptions;
    private readonly SlaveProbeRuntimeState _runtimeState;
    private readonly NetworkDiscoveryService _discoveryService;
    private readonly ILogger<SlaveSensorWorker> _logger;
    private readonly Dictionary<Guid, DateTimeOffset> _lastExecutedUtc = new();
    private const int MaxParallelSensorTypes = 6;

    // The last result of each sensor on THIS probe. A sensor that works from its previous result (interface traffic as a
    // rate, the mail round-trip) needs the one before it - the copy the assignment carries is only as fresh as the
    // primary's, and while the primary is away it does not move at all.
    private readonly Dictionary<Guid, SensorObservation> _lastObservations = new();
    // Store-and-forward: the last assignments received from the primary (so the probe keeps executing on
    // schedule while the primary is unreachable) + the observations produced meanwhile (flushed on reconnect).
    private ProbeSensorAssignmentsResponse? _cachedAssignments;
    // The scan that runs in the background (StartDiscoveryJobsAsync); only touched by the worker loop.
    private Task? _discoveryTask;
    private readonly OfflineObservationBuffer _buffer;

    public SlaveSensorWorker(
        IHttpClientFactory httpClientFactory,
        IEnumerable<ISensorExecutor> executors,
        ProbeRuntimeOptions runtimeOptions,
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
        _buffer = new OfflineObservationBuffer(
            runtimeOptions.Mode == AppMode.Secondary ? runtimeOptions.OfflineBufferPath : null,
            message => logger.LogWarning("{Message}", message));
        _runtimeState.BufferedObservations = _buffer.Count;
        if (_buffer.Count > 0)
        {
            _logger.LogInformation("Loaded {Count} buffered observation(s) from {Path}; they are sent once the primary answers", _buffer.Count, _buffer.FilePath);
        }
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

        // Three stages, each on its own: they used to share ONE try in this order, so anything that threw in the sensor
        // pass (a check that ended in a stray cancellation, a failed post) skipped the run jobs and the discovery
        // jobs as well - and since the same sensor was due again on the next tick, it did so every tick: a scan
        // queued for the probe stayed "pending" for good. Discovery is looked at FIRST because it only hands a job
        // to the background (see StartDiscoveryJobsAsync) and must not wait for a slow sensor pass.
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunStageAsync("discovery jobs", probeId, () => StartDiscoveryJobsAsync(client, probeId, stoppingToken), stoppingToken);
            await RunStageAsync("sensor assignments", probeId, () => SyncAssignmentsAsync(client, probeId, stoppingToken), stoppingToken);
            await RunStageAsync("on-demand jobs", probeId, () => SyncRunJobsAsync(client, probeId, stoppingToken), stoppingToken);

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_discoveryTask is { } running)
        {
            try
            {
                await running;
            }
            catch (Exception ex) when (ex is OperationCanceledException || stoppingToken.IsCancellationRequested)
            {
                // Shutting down mid-scan: the primary fails the job once it stops hearing about it.
            }
        }
    }

    private async Task RunStageAsync(string stage, string probeId, Func<Task> run, CancellationToken stoppingToken)
    {
        try
        {
            await run();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            if (stage == "sensor assignments")
            {
                _runtimeState.RecordAssignmentSync(0, ex.Message, success: false);
            }

            _logger.LogWarning(ex, "Secondary sync of {Stage} failed for {ProbeId}", stage, probeId);
        }
    }

    private async Task SyncAssignmentsAsync(HttpClient client, string probeId, CancellationToken cancellationToken)
    {
        // Live assignments when the primary is reachable, else the last-known set - so the probe keeps
        // executing on schedule through a primary outage (store-and-forward).
        var assignments = await FetchAssignmentsOrCacheAsync(client, probeId, cancellationToken);
        if (assignments is null)
        {
            // Never received assignments (and offline now) - nothing to run yet.
            return;
        }

        var reports = new List<ProbeSensorObservationReport>();
        var pendingResults = new List<SlaveProbePendingResult>();
        var upcomingExecutions = new List<SlaveProbeUpcomingExecution>();
        var due = new List<ProbeSensorAssignment>();
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

            due.Add(assignment);
        }

        // The due sensors run side by side, one lane per sensor TYPE: a pass used to run strictly one after another and
        // post only at the end, so a few dead WinRM or SSH targets (each waiting out its timeout) made every other
        // sensor on the probe late - overdue, then Unknown. Sensors of the same type still run one after another,
        // because the probe holds ONE executor per type and an executor is not built to run twice at once.
        var executed = new ConcurrentDictionary<Guid, (SensorExecutionResult Result, DateTimeOffset ExecutedUtc)>();
        await Parallel.ForEachAsync(
            due.GroupBy(assignment => assignment.SensorTypeKey, StringComparer.OrdinalIgnoreCase),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSensorTypes, CancellationToken = cancellationToken },
            async (lane, token) =>
            {
                foreach (var assignment in lane)
                {
                    var laneResult = await ExecuteAssignmentAsync(assignment, token);
                    executed[assignment.SensorId] = (laneResult, DateTimeOffset.UtcNow);
                }
            });

        foreach (var assignment in due)
        {
            if (!executed.TryGetValue(assignment.SensorId, out var outcome))
            {
                continue;
            }

            var result = outcome.Result;
            var executedUtc = outcome.ExecutedUtc;
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
            _lastObservations[assignment.SensorId] = new SensorObservation
            {
                SensorId = assignment.SensorId,
                TimestampUtc = executedUtc,
                State = result.State,
                Value = result.Value,
                DefaultChannelKey = result.DefaultChannelKey,
                Channels = result.Channels.ToList(),
                Duration = result.Duration,
                Message = result.Message
            };
        }

        PruneStaleLastExecuted(assignments.Sensors);

        _runtimeState.UpdateUpcomingExecutions(upcomingExecutions);

        // Buffering disabled (retention <= 0): the pre-buffer behaviour - post this tick's results, drop on failure.
        if (_runtimeOptions.OfflineBufferRetentionDays <= 0)
        {
            if (reports.Count > 0)
            {
                await PostResultsAsync(client, probeId, reports, pendingResults, cancellationToken);
            }

            return;
        }

        // Store-and-forward: buffer this tick's results, trim to the retention window, then try to flush the
        // whole backlog (oldest first). Reports carry their real execution timestamp, so a reconnect backfills
        // the history at the times the checks actually ran - not "now".
        _buffer.AddRange(reports);
        var dropped = _buffer.Prune(
            now,
            TimeSpan.FromDays(_runtimeOptions.OfflineBufferRetentionDays),
            _runtimeOptions.OfflineBufferMaxObservations);
        _runtimeState.BufferedObservations = _buffer.Count;
        if (dropped > 0)
        {
            _logger.LogWarning(
                "Offline buffer dropped {Dropped} observation(s) past the {Days}-day retention on probe {ProbeId}",
                dropped, _runtimeOptions.OfflineBufferRetentionDays, probeId);
        }

        await FlushBufferAsync(client, probeId, pendingResults, cancellationToken);
    }

    /// <summary>Gets live assignments from the primary and refreshes the cache; on any failure falls back to the
    /// last cached set (so scheduling continues through an outage). Null only when we have never reached the primary.</summary>
    private async Task<ProbeSensorAssignmentsResponse?> FetchAssignmentsOrCacheAsync(HttpClient client, string probeId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/probes/{Uri.EscapeDataString(probeId)}/assignments");
            AddProbeToken(request);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return FallBackToCachedAssignments($"assignment sync returned {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var assignments = await response.Content.ReadFromJsonAsync<ProbeSensorAssignmentsResponse>(JsonOptions, cancellationToken);
            if (assignments is null)
            {
                return FallBackToCachedAssignments("assignment response was empty");
            }

            _cachedAssignments = assignments;
            // The instance's Local Program allow-list applies here too (an older primary sends none: keep what we had).
            if (assignments.AllowedProgramPaths is { } allowedPrograms)
            {
                LocalProgramSensorExecutor.SetConfiguredAllowList(allowedPrograms);
            }

            _runtimeState.RecordAssignmentSync(
                assignments.Sensors.Count,
                $"{assignments.Sensors.Count} sensor assignment{(assignments.Sensors.Count == 1 ? string.Empty : "s")} received",
                success: true);
            return assignments;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FallBackToCachedAssignments(ex.Message);
        }
    }

    private ProbeSensorAssignmentsResponse? FallBackToCachedAssignments(string reason)
    {
        if (_cachedAssignments is { } cached)
        {
            _runtimeState.RecordAssignmentSync(
                cached.Sensors.Count,
                $"primary unreachable ({reason}) - running {cached.Sensors.Count} cached assignment(s), buffering results",
                success: false);
            return cached;
        }

        _runtimeState.RecordAssignmentSync(0, reason, success: false);
        return null;
    }

    /// <summary>Drains the offline buffer to the primary oldest-first in chunks; stops at the first failed chunk
    /// (still offline) and keeps the remainder for the next tick. When connected the buffer is normally empty and
    /// this posts just the current tick's results.</summary>
    private async Task FlushBufferAsync(
        HttpClient client,
        string probeId,
        IReadOnlyList<SlaveProbePendingResult> tickResults,
        CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        const int chunkSize = 500;
        var flushed = 0;
        var offline = false;
        while (_buffer.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = _buffer.PeekOldest(chunkSize);
            if (!await TryPostReportsAsync(client, probeId, chunk, cancellationToken))
            {
                offline = true;
                break;
            }

            _buffer.RemoveOldest(chunk.Count);
            _runtimeState.BufferedObservations = _buffer.Count;
            flushed += chunk.Count;
        }

        if (offline)
        {
            _runtimeState.RecordResultPost(
                _buffer.Count,
                $"primary unreachable - {_buffer.Count} observation(s) buffered (retention {_runtimeOptions.OfflineBufferRetentionDays}d)",
                success: false,
                tickResults);
        }
        else if (flushed > 0)
        {
            var backfilled = flushed > tickResults.Count;
            _runtimeState.RecordResultPost(
                flushed,
                $"{flushed} result{(flushed == 1 ? string.Empty : "s")} posted{(backfilled ? " (incl. buffered offline data)" : string.Empty)}",
                success: true,
                tickResults);
        }
    }

    private async Task<bool> TryPostReportsAsync(
        HttpClient client,
        string probeId,
        IReadOnlyList<ProbeSensorObservationReport> reports,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/probes/{Uri.EscapeDataString(probeId)}/observations")
            {
                Content = JsonContent.Create(new ProbeSensorObservationBatch(reports, DateTimeOffset.UtcNow), options: JsonOptions)
            };
            AddProbeToken(request);

            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
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
            _lastObservations.Remove(staleId);
        }
    }

    /// <summary>This probe's own last result when it is newer than the one the assignment carries.</summary>
    private SensorObservation? PreviousObservation(ProbeSensorAssignment assignment) =>
        _lastObservations.TryGetValue(assignment.SensorId, out var local)
            && (assignment.LastObservation is null || local.TimestampUtc > assignment.LastObservation.TimestampUtc)
                ? local
                : assignment.LastObservation;

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
            var result = await SensorRetry.ExecuteAsync(
                executor,
                new SensorExecutionContext(
                    assignment.SensorTypeKey,
                    assignment.Target,
                    assignment.Settings,
                    assignment.SensorId,
                    PreviousObservation(assignment)),
                cancellationToken);
            result = SensorExecutionResultHelper.ApplyDefaultChannelSelection(assignment.Settings, result);

            _runtimeState.RecordExecution(
                assignment.Name,
                $"{MonitoringStatePresentation.Label(result.State)} - {result.Message ?? "ok"}",
                result.State is not SensorState.Critical);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A cancellation the probe did not ask for (a timeout inside the executor that escaped it) is a failed
            // check like any other. Let through, it used to abort the whole pass - and with it every other result.
            var message = FailureMessage(ex);
            _runtimeState.RecordExecution(assignment.Name, message, success: false);
            return SensorExecutionResult.Critical(TimeSpan.Zero, message);
        }
    }

    private static string FailureMessage(Exception ex) =>
        ex is OperationCanceledException ? "The check was cancelled before it finished (timed out)." : ex.Message;

    private async Task PostResultsAsync(
        HttpClient client,
        string probeId,
        IReadOnlyList<ProbeSensorObservationReport> reports,
        IReadOnlyList<SlaveProbePendingResult> pendingResults,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/probes/{Uri.EscapeDataString(probeId)}/observations")
        {
            Content = JsonContent.Create(new ProbeSensorObservationBatch(reports, DateTimeOffset.UtcNow), options: JsonOptions)
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
            // default-channel selection and the sensor's previous result on THIS probe - without it a Run now of an
            // interface sensor lost its traffic / utilisation channels (a rate needs the counter before it).
            // A recorded run job (Run now) honours Retries like a scheduled check; a test runs once.
            var previous = job.SensorId is Guid sensorId && _lastObservations.TryGetValue(sensorId, out var last) ? last : null;
            var context = new SensorExecutionContext(
                job.SensorTypeKey,
                job.Target,
                job.Settings,
                job.SensorId,
                previousObservation: previous);
            var result = job.RecordObservation
                ? await SensorRetry.ExecuteAsync(executor, context, cancellationToken)
                : await executor.ExecuteAsync(context, cancellationToken);
            result = SensorExecutionResultHelper.ApplyDefaultChannelSelection(job.Settings, result);
            return new ProbeRunJobResult(job.JobId, result, null, null, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var message = FailureMessage(ex);
            return job.Kind == ProbeRunJobKind.SnmpDiscover
                ? new ProbeRunJobResult(job.JobId, null, null, message, DateTimeOffset.UtcNow)
                : new ProbeRunJobResult(job.JobId, SensorExecutionResult.Critical(TimeSpan.Zero, message), null, null, DateTimeOffset.UtcNow);
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
            Content = JsonContent.Create(new ProbeRunJobResultBatch(results, DateTimeOffset.UtcNow), options: JsonOptions)
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

    /// <summary>Takes the discovery jobs the primary queued for this probe and runs them in the BACKGROUND, one scan at a
    /// time. A scan used to run inline in the worker loop: a subnet of a few thousand addresses (an agent suggests its
    /// own /16 or /20) stopped every sensor on the probe until it was done, and a slow sensor pass held a queued scan
    /// back. While a scan runs no further job is taken - it stays pending on the primary until this one ends.</summary>
    private async Task StartDiscoveryJobsAsync(HttpClient client, string probeId, CancellationToken cancellationToken)
    {
        if (_discoveryTask is { IsCompleted: false })
        {
            return;
        }

        var jobs = await FetchDiscoveryJobsAsync(client, probeId, cancellationToken);
        if (jobs.Count == 0)
        {
            return;
        }

        _discoveryTask = Task.Run(async () =>
        {
            foreach (var job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await RunDiscoveryJobAsync(client, probeId, job, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Closing the job out failed too (the primary went away mid-scan); it fails the job itself once
                    // it stops hearing about it. Never let one job stop the next.
                    _logger.LogWarning(ex, "Discovery job {JobId} on {ProbeId} could not be completed", job.JobId, probeId);
                }
            }
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<ProbeDiscoveryJobAssignment>> FetchDiscoveryJobsAsync(HttpClient client, string probeId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/probes/{Uri.EscapeDataString(probeId)}/discovery-jobs");
        AddProbeToken(request);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var assignments = await response.Content.ReadFromJsonAsync<ProbeDiscoveryJobAssignmentsResponse>(JsonOptions, cancellationToken);
        return assignments?.Jobs ?? [];
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
        catch (DiscoveryCancelledByPrimaryException)
        {
            _runtimeState.RecordExecution($"Discovery {job.Network}", "cancelled by primary", success: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await PostDiscoveryResultsAsync(
                client,
                probeId,
                [new ProbeDiscoveryJobResult(job.JobId, [], FailureMessage(ex), IsComplete: true)],
                cancellationToken);
            _runtimeState.RecordExecution($"Discovery {job.Network}", FailureMessage(ex), success: false);
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
            throw new DiscoveryCancelledByPrimaryException();
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

/// <summary>The primary answered a discovery post with "cancelled": the one cancellation that ends a scan quietly.
/// Any other cancellation the probe did not ask for is a failed scan and is reported as one.</summary>
internal sealed class DiscoveryCancelledByPrimaryException() : OperationCanceledException("Discovery job was cancelled by primary.");
