using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Matmon.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Matmon.Probe.Plugins;

// --- Wire: what a probe and its primary exchange about plugins -----------------------------------------------------

/// <summary>One plugin a probe is told to run (<c>GET /api/probes/{id}/plugins</c>).</summary>
public sealed record ProbePluginAssignment(Guid Id, string PluginId, bool Enabled, Dictionary<string, string> Settings);

public sealed record ProbePluginAssignmentsResponse(IReadOnlyList<ProbePluginAssignment> Assignments);

public sealed record ProbePluginStatusReport(Guid AssignmentId, string State, string? Detail);

/// <summary>What a probe sends (<c>POST /api/probes/{id}/plugins/events</c>): the oldest waiting events, the state of its
/// plugin instances, and which plugins it has at all (so the instance can say "this agent is too old for it").</summary>
public sealed record ProbePluginUpload(
    IReadOnlyList<PluginEvent> Events,
    IReadOnlyList<ProbePluginStatusReport> Statuses,
    IReadOnlyList<string> AvailablePlugins,
    int QueuedEvents = 0,
    long DroppedEvents = 0);

/// <summary>
/// The events of the plugins on a probe, on their way to the primary. <see cref="Publish"/> is called from a plugin's
/// own threads (a UDP listener), so it only puts the event into a bounded in-memory queue; the worker moves them in
/// batches into the on-disk <see cref="JsonLinesQueue{T}"/> - one append per second instead of one per log line.
/// </summary>
public sealed class ProbePluginEventSink : IPluginEventSink
{
    public const int MaxPending = 50_000;

    private readonly ConcurrentQueue<PluginEvent> _pending = new();
    private long _dropped;

    public long Dropped => Interlocked.Read(ref _dropped);

    public void Publish(PluginEvent pluginEvent)
    {
        if (_pending.Count >= MaxPending)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _pending.Enqueue(pluginEvent);
    }

    public List<PluginEvent> Drain()
    {
        var drained = new List<PluginEvent>();
        while (drained.Count < MaxPending && _pending.TryDequeue(out var pluginEvent))
        {
            drained.Add(pluginEvent);
        }

        return drained;
    }
}

/// <summary>
/// Runs the plugins a primary assigns to THIS probe or agent and carries their events home. Every few seconds: asks the
/// primary which plugins to run (keeps running the last answer while it is unreachable - collecting goes on), moves new
/// events into the on-disk queue (kept as long as the observation buffer, at most <see cref="MaxQueued"/>), and sends the
/// oldest 500 with the instances' states. A primary that does not know plugins yet (404) simply gets nothing.
/// </summary>
public sealed class ProbePluginWorker : BackgroundService
{
    public const int MaxQueued = 200_000;
    private const int BatchSize = 500;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AssignmentInterval = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProbeRuntimeOptions _options;
    private readonly PluginCatalog _catalog;
    private readonly ProbePluginEventSink _sink;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProbePluginWorker> _logger;

    public ProbePluginWorker(IHttpClientFactory httpClientFactory, ProbeRuntimeOptions options, PluginCatalog catalog,
        ProbePluginEventSink sink, ILoggerFactory loggerFactory, ILogger<ProbePluginWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _catalog = catalog;
        _sink = sink;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>Where a probe keeps its files: beside the offline buffer (null = memory only).</summary>
    public static string? DataDirectory(ProbeRuntimeOptions options) =>
        string.IsNullOrWhiteSpace(options.OfflineBufferPath) ? null : Path.GetDirectoryName(Path.GetFullPath(options.OfflineBufferPath));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Mode != AppMode.Secondary || !Uri.TryCreate(_options.PrimaryUrl, UriKind.Absolute, out var primary))
        {
            return;
        }

        var probeId = string.IsNullOrWhiteSpace(_options.ProbeId) ? Environment.MachineName : _options.ProbeId.Trim();
        var dataDirectory = DataDirectory(_options);
        var queue = new JsonLinesQueue<PluginEvent>(dataDirectory is null ? null : Path.Combine(dataDirectory, "plugin-events.jsonl"),
            message => _logger.LogWarning("{Message}", message));
        await using var runtime = new PluginRuntime(_catalog, _sink, _loggerFactory, dataDirectory ?? Path.GetTempPath(), probeId);

        var client = _httpClientFactory.CreateClient("matmon-probe-plugins");
        client.BaseAddress = new Uri(primary.ToString().TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Matmon-Probe-Token", _options.ProbeToken ?? string.Empty);

        // The last answer is kept on disk: a probe that restarts while its primary is down (an agent update, a reboot)
        // must go on collecting with what it was told last, not wait for the primary to tell it again.
        var cachePath = dataDirectory is null ? null : Path.Combine(dataDirectory, "plugin-assignments.json");
        IReadOnlyList<PluginAssignmentSpec> assignments = ReadCachedAssignments(cachePath);
        var nextAssignments = DateTimeOffset.MinValue;
        var supported = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            try
            {
                if (supported && now >= nextAssignments)
                {
                    nextAssignments = now + AssignmentInterval;
                    var fetched = await FetchAssignmentsAsync(client, probeId, stoppingToken);
                    if (fetched is null)
                    {
                        supported = false; // an older primary - nothing to run, nothing to send
                        assignments = [];
                    }
                    else if (fetched.Value.Answered)
                    {
                        assignments = fetched.Value.Assignments;
                        WriteCachedAssignments(cachePath, assignments);
                    }
                }

                await runtime.ReconcileAsync(assignments, now, stoppingToken);

                var fresh = _sink.Drain();
                if (fresh.Count > 0)
                {
                    queue.AddRange(fresh);
                }

                var retention = TimeSpan.FromDays(Math.Max(1, _options.OfflineBufferRetentionDays));
                queue.Prune(pluginEvent => pluginEvent.OccurredUtc < now - retention, MaxQueued);

                if (supported)
                {
                    await UploadAsync(client, probeId, queue, runtime, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Plugin sync with the primary failed; events stay queued");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions CacheJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static IReadOnlyList<PluginAssignmentSpec> ReadCachedAssignments(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path))
            {
                return [];
            }

            var cached = System.Text.Json.JsonSerializer.Deserialize<List<ProbePluginAssignment>>(File.ReadAllText(path), CacheJson) ?? [];
            return cached.Select(assignment => new PluginAssignmentSpec(assignment.Id, assignment.PluginId, assignment.Enabled,
                new Dictionary<string, string>(assignment.Settings ?? [], StringComparer.OrdinalIgnoreCase))).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public static void WriteCachedAssignments(string? path, IReadOnlyList<PluginAssignmentSpec> assignments)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            var payload = assignments.Select(spec => new ProbePluginAssignment(spec.Id, spec.PluginId, spec.Enabled, new Dictionary<string, string>(spec.Settings))).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(payload, CacheJson));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without the cache a restart during an outage waits for the primary - the running plugins are unaffected.
            _ = exception;
        }
    }

    // (Answered, Assignments): Answered=false = the primary did not answer (keep running what we have); null = no plugin support.
    private async Task<(bool Answered, IReadOnlyList<PluginAssignmentSpec> Assignments)?> FetchAssignmentsAsync(HttpClient client, string probeId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync($"api/probes/{Uri.EscapeDataString(probeId)}/plugins", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, []);
            }

            var body = await response.Content.ReadFromJsonAsync<ProbePluginAssignmentsResponse>(cancellationToken);
            return (true, (body?.Assignments ?? []).Select(assignment => new PluginAssignmentSpec(
                assignment.Id, assignment.PluginId, assignment.Enabled,
                new Dictionary<string, string>(assignment.Settings ?? [], StringComparer.OrdinalIgnoreCase))).ToList());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (false, []);
        }
    }

    private DateTimeOffset _lastUpload = DateTimeOffset.MinValue;

    private async Task UploadAsync(HttpClient client, string probeId, JsonLinesQueue<PluginEvent> queue, PluginRuntime runtime, CancellationToken cancellationToken)
    {
        var batch = queue.PeekOldest(BatchSize);
        var statuses = runtime.Statuses();
        // Nothing to send: only report the states now and then, not every two seconds.
        if (batch.Count == 0 && (statuses.Count == 0 || DateTimeOffset.UtcNow - _lastUpload < TimeSpan.FromSeconds(30)))
        {
            return;
        }

        _lastUpload = DateTimeOffset.UtcNow;
        var upload = new ProbePluginUpload(
            batch,
            statuses.Select(status => new ProbePluginStatusReport(status.AssignmentId, status.Status.State, status.Status.Detail)).ToList(),
            _catalog.Plugins.Select(plugin => plugin.Definition.Id).ToList(),
            queue.Count - batch.Count,
            _sink.Dropped);

        using var response = await client.PostAsJsonAsync($"api/probes/{Uri.EscapeDataString(probeId)}/plugins/events", upload, cancellationToken);
        if (response.IsSuccessStatusCode && batch.Count > 0)
        {
            queue.RemoveOldest(batch.Count);
        }
    }
}
