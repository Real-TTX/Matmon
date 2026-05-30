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
        if (_runtimeOptions.Mode != AppMode.Slave)
        {
            return;
        }

        if (!Uri.TryCreate(_runtimeOptions.MasterUrl, UriKind.Absolute, out var masterUri))
        {
            _runtimeState.RecordAssignmentSync(0, "No valid master URL configured.", success: false);
            return;
        }

        var probeId = string.IsNullOrWhiteSpace(_runtimeOptions.ProbeId)
            ? Environment.MachineName
            : _runtimeOptions.ProbeId;
        var interval = TimeSpan.FromSeconds(5);
        var client = _httpClientFactory.CreateClient(nameof(SlaveSensorWorker));
        client.BaseAddress = masterUri;

        _logger.LogInformation("Slave sensor worker started for {ProbeId} against {MasterUrl}", probeId, masterUri);

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
                _logger.LogWarning(ex, "Slave sensor sync failed for {ProbeId}", probeId);
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
            if (!MonitoringScheduleCalculator.IsDue(assignment.Settings, lastExecutedUtc, now, TimeSpan.FromSeconds(15)))
            {
                continue;
            }

            var result = await ExecuteAssignmentAsync(assignment, cancellationToken);
            reports.Add(new ProbeSensorObservationReport(assignment.SensorId, result, DateTimeOffset.UtcNow));
            _lastExecutedUtc[assignment.SensorId] = DateTimeOffset.UtcNow;
        }

        if (reports.Count == 0)
        {
            return;
        }

        await PostResultsAsync(client, probeId, reports, cancellationToken);
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
            result = ApplyDefaultChannelSelection(assignment.Settings, result);

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
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/probes/{Uri.EscapeDataString(probeId)}/observations")
        {
            Content = JsonContent.Create(new ProbeSensorObservationBatch(reports), options: JsonOptions)
        };
        AddProbeToken(request);

        using var response = await client.SendAsync(request, cancellationToken);
        var message = $"{reports.Count} result{(reports.Count == 1 ? string.Empty : "s")} posted: {(int)response.StatusCode} {response.ReasonPhrase}";
        _runtimeState.RecordResultPost(reports.Count, message, response.IsSuccessStatusCode);
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
                    cancellationToken);
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
        _runtimeState.RecordResultPost(
            results.Count,
            $"{results.Count} discovery result{(results.Count == 1 ? string.Empty : "s")} posted: {(int)postResponse.StatusCode} {postResponse.ReasonPhrase}",
            postResponse.IsSuccessStatusCode);
    }

    private void AddProbeToken(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_runtimeOptions.ProbeToken))
        {
            request.Headers.TryAddWithoutValidation("X-Matmon-Probe-Token", _runtimeOptions.ProbeToken);
        }
    }

    private static SensorExecutionResult ApplyDefaultChannelSelection(
        MonitoringSettings settings,
        SensorExecutionResult result)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultChannelKey) || result.Channels.Count == 0)
        {
            return result;
        }

        var selectedChannel = result.Channels.FirstOrDefault(channel =>
            string.Equals(channel.Key, settings.DefaultChannelKey, StringComparison.OrdinalIgnoreCase));
        if (selectedChannel is null || !selectedChannel.Value.HasValue)
        {
            return result;
        }

        return result with
        {
            DefaultChannelKey = selectedChannel.Key,
            Value = selectedChannel.Value,
            Channels = result.Channels
                .Select(channel => channel with
                {
                    IsDefault = string.Equals(channel.Key, selectedChannel.Key, StringComparison.OrdinalIgnoreCase)
                })
                .ToArray()
        };
    }
}
