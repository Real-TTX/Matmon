using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Matmon.Host.Services;

public sealed class DiscoveryJobStore
{
    private const int PersistedJobLimit = 50;
    private static readonly JsonSerializerOptions FileJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ConcurrentDictionary<Guid, DiscoveryJobSnapshot> _jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellationSources = new();
    private readonly object _persistGate = new();
    private readonly ILogger<DiscoveryJobStore> _logger;
    private readonly string _storePath;

    public DiscoveryJobStore(
        IHostEnvironment environment,
        MatmonRuntimeOptions runtimeOptions,
        ILogger<DiscoveryJobStore> logger)
    {
        _logger = logger;
        _storePath = ResolveStorePath(environment, runtimeOptions);
        LoadPersistedJobs();
    }

    public DiscoveryJobSnapshot Create(
        Guid probeElementId,
        string probeId,
        string probeName,
        NetworkDiscoveryRequest request)
    {
        var jobId = request.JobId == Guid.Empty ? Guid.NewGuid() : request.JobId;
        var job = new DiscoveryJobSnapshot
        {
            JobId = jobId,
            ProbeElementId = probeElementId,
            ProbeId = probeId,
            ProbeName = probeName,
            Request = request with { JobId = jobId },
            Status = DiscoveryJobStatus.Pending,
            CreatedUtc = DateTimeOffset.UtcNow,
            TotalHosts = CountTargets(request)
        };

        _jobs[job.JobId] = job;
        _cancellationSources[job.JobId] = new CancellationTokenSource();
        PruneOldJobs();
        PersistRecentJobs();
        return job;
    }

    // Bound in-memory growth: keep only the most recent jobs, disposing the cancellation sources of
    // the evicted (old, finished) ones. Without this both dictionaries grow for the process lifetime.
    private void PruneOldJobs()
    {
        var keep = _jobs.Values
            .OrderByDescending(job => job.CreatedUtc)
            .Take(PersistedJobLimit)
            .Select(job => job.JobId)
            .ToHashSet();

        foreach (var id in _jobs.Keys.ToArray())
        {
            if (keep.Contains(id))
            {
                continue;
            }

            _jobs.TryRemove(id, out _);
            if (_cancellationSources.TryRemove(id, out var source))
            {
                source.Dispose();
            }
        }
    }

    public DiscoveryJobSnapshot? Find(Guid jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public IReadOnlyList<DiscoveryJobSnapshot> GetRecent(int take = 20)
    {
        return _jobs.Values
            .OrderByDescending(job => job.CreatedUtc)
            .Take(Math.Clamp(take, 1, 100))
            .ToArray();
    }

    public bool Start(Guid jobId, string? message = null)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        lock (job)
        {
            job.Status = DiscoveryJobStatus.Running;
            job.StartedUtc ??= DateTimeOffset.UtcNow;
            job.Message = string.IsNullOrWhiteSpace(message) ? "Discovery is running." : message.Trim();
            if (job.TotalHosts <= 0)
            {
                job.TotalHosts = CountTargets(job.Request);
            }
        }

        return true;
    }

    public bool AddResult(Guid jobId, NetworkDiscoveryResult result)
    {
        return AddResults(jobId, [result]);
    }

    public bool AddResults(Guid jobId, IReadOnlyList<NetworkDiscoveryResult> results)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        lock (job)
        {
            if (job.Status is DiscoveryJobStatus.Completed or DiscoveryJobStatus.Failed or DiscoveryJobStatus.Cancelled)
            {
                return false;
            }

            if (job.Status == DiscoveryJobStatus.Pending)
            {
                job.Status = DiscoveryJobStatus.Running;
                job.StartedUtc ??= DateTimeOffset.UtcNow;
            }

            var merged = job.Results
                .Concat(results.Where(result => result.IsDiscovered))
                .GroupBy(result => result.Address, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(result => NetworkTargetParser.ToSortableAddress(result.Address))
                .ToArray();
            job.Results = merged;
            job.Message = BuildProgressMessage(job);
        }

        return true;
    }

    public bool UpdateProgress(Guid jobId, int scannedHosts, int totalHosts)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        lock (job)
        {
            if (job.Status is DiscoveryJobStatus.Completed or DiscoveryJobStatus.Failed or DiscoveryJobStatus.Cancelled)
            {
                return false;
            }

            if (job.Status == DiscoveryJobStatus.Pending)
            {
                job.Status = DiscoveryJobStatus.Running;
                job.StartedUtc ??= DateTimeOffset.UtcNow;
            }

            if (totalHosts > 0)
            {
                job.TotalHosts = Math.Max(job.TotalHosts, totalHosts);
            }

            if (job.TotalHosts > 0)
            {
                job.ScannedHosts = Math.Clamp(scannedHosts, 0, job.TotalHosts);
            }
            else
            {
                job.ScannedHosts = Math.Max(scannedHosts, 0);
            }

            job.Message = BuildProgressMessage(job);
        }

        return true;
    }

    public ProbeDiscoveryJobAssignmentsResponse TakePendingAssignments(string probeId, int take = 1)
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = _jobs.Values
            .Where(job =>
                job.Status == DiscoveryJobStatus.Pending &&
                string.Equals(job.ProbeId, probeId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(job => job.CreatedUtc)
            .Take(Math.Clamp(take, 1, 5))
            .ToArray();

        foreach (var job in jobs)
        {
            lock (job)
            {
                job.Status = DiscoveryJobStatus.Running;
                job.StartedUtc = now;
                if (job.TotalHosts <= 0)
                {
                    job.TotalHosts = CountTargets(job.Request);
                }

                job.Message = "Discovery is running on probe.";
            }
        }

        return new ProbeDiscoveryJobAssignmentsResponse(
            jobs.Select(job => new ProbeDiscoveryJobAssignment(job.JobId, job.Request.Network, job.Request.Options)).ToArray());
    }

    public CancellationToken GetCancellationToken(Guid jobId)
    {
        return _cancellationSources.TryGetValue(jobId, out var source)
            ? source.Token
            : CancellationToken.None;
    }

    public bool IsCancelled(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        lock (job)
        {
            return job.Status == DiscoveryJobStatus.Cancelled ||
                (_cancellationSources.TryGetValue(jobId, out var source) && source.IsCancellationRequested);
        }
    }

    public bool Cancel(Guid jobId, string? message = null)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        lock (job)
        {
            if (job.Status is DiscoveryJobStatus.Completed or DiscoveryJobStatus.Failed or DiscoveryJobStatus.Cancelled)
            {
                return false;
            }

            job.Status = DiscoveryJobStatus.Cancelled;
            job.CompletedUtc = DateTimeOffset.UtcNow;
            job.Message = string.IsNullOrWhiteSpace(message)
                ? $"Discovery cancelled after {job.ScannedHosts}/{job.TotalHosts} checked."
                : message.Trim();
        }

        if (_cancellationSources.TryGetValue(jobId, out var source))
        {
            source.Cancel();
        }

        PersistRecentJobs();
        return true;
    }

    public bool Complete(Guid jobId, IReadOnlyList<NetworkDiscoveryResult> results, string? errorMessage)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        lock (job)
        {
            if (job.Status == DiscoveryJobStatus.Cancelled)
            {
                return false;
            }

            if (results.Count > 0)
            {
                AddResults(jobId, results);
            }

            job.CompletedUtc = DateTimeOffset.UtcNow;
            if (job.TotalHosts <= 0)
            {
                job.TotalHosts = CountTargets(job.Request);
            }

            if (job.TotalHosts > 0 && string.IsNullOrWhiteSpace(errorMessage))
            {
                job.ScannedHosts = job.TotalHosts;
            }

            job.Status = string.IsNullOrWhiteSpace(errorMessage)
                ? DiscoveryJobStatus.Completed
                : DiscoveryJobStatus.Failed;
            job.Message = string.IsNullOrWhiteSpace(errorMessage)
                ? $"{job.Results.Count} host{(job.Results.Count == 1 ? string.Empty : "s")} discovered."
                : errorMessage.Trim();
        }

        PersistRecentJobs();
        return true;
    }

    private void LoadPersistedJobs()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return;
            }

            var document = JsonSerializer.Deserialize<DiscoveryJobStoreDocument>(
                File.ReadAllText(_storePath),
                FileJsonOptions);
            if (document?.Jobs is null)
            {
                return;
            }

            foreach (var job in document.Jobs
                .Where(job => job.JobId != Guid.Empty)
                .OrderByDescending(job => job.CreatedUtc)
                .Take(PersistedJobLimit))
            {
                if (job.Status is DiscoveryJobStatus.Pending or DiscoveryJobStatus.Running)
                {
                    job.Status = DiscoveryJobStatus.Cancelled;
                    job.CompletedUtc ??= DateTimeOffset.UtcNow;
                    job.Message = "Discovery was interrupted by an application restart.";
                }

                _jobs[job.JobId] = job;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted discovery jobs from {DiscoveryJobStorePath}", _storePath);
        }
    }

    private void PersistRecentJobs()
    {
        // Serialize writers: PersistRecentJobs is called from Create/Cancel/Complete on different
        // threads, and two concurrent File writes can corrupt the file or throw sharing violations.
        lock (_persistGate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var jobs = _jobs.Values
                    .OrderByDescending(job => job.CreatedUtc)
                    .Take(PersistedJobLimit)
                    .Select(CloneJob)
                    .ToArray();
                var document = new DiscoveryJobStoreDocument(jobs);

                // Write to a temp file then atomically replace, so a crash mid-write can't truncate it.
                var tempPath = _storePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(document, FileJsonOptions));
                File.Move(tempPath, _storePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist discovery jobs to {DiscoveryJobStorePath}", _storePath);
            }
        }
    }

    private static DiscoveryJobSnapshot CloneJob(DiscoveryJobSnapshot job)
    {
        lock (job)
        {
            return new DiscoveryJobSnapshot
            {
                JobId = job.JobId,
                ProbeElementId = job.ProbeElementId,
                ProbeId = job.ProbeId,
                ProbeName = job.ProbeName,
                Request = job.Request,
                Status = job.Status,
                CreatedUtc = job.CreatedUtc,
                StartedUtc = job.StartedUtc,
                CompletedUtc = job.CompletedUtc,
                Results = job.Results.ToArray(),
                Message = job.Message,
                TotalHosts = job.TotalHosts,
                ScannedHosts = job.ScannedHosts
            };
        }
    }

    private static string ResolveStorePath(IHostEnvironment environment, MatmonRuntimeOptions runtimeOptions)
    {
        var configuredPath = string.IsNullOrWhiteSpace(runtimeOptions.WorkspacePath)
            ? "data/workspace.json"
            : runtimeOptions.WorkspacePath;
        var workspacePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
        var dataDirectory = Path.GetDirectoryName(workspacePath)
            ?? Path.Combine(environment.ContentRootPath, "data");

        return Path.Combine(dataDirectory, "discovery-jobs.json");
    }

    private static int CountTargets(NetworkDiscoveryRequest request)
    {
        var options = request.Options.Normalized();
        return NetworkTargetParser.Parse(request.Network, options.MaxHosts).Count;
    }

    private static string BuildProgressMessage(DiscoveryJobSnapshot job)
    {
        var discoveredText = $"{job.Results.Count} host{(job.Results.Count == 1 ? string.Empty : "s")} discovered";
        if (job.TotalHosts <= 0)
        {
            return $"{discoveredText} so far.";
        }

        return $"{discoveredText}, {job.ProgressPercent}% checked ({job.ScannedHosts}/{job.TotalHosts}).";
    }
}

public sealed class DiscoveryJobSnapshot
{
    public Guid JobId { get; set; }

    public Guid ProbeElementId { get; set; }

    public string ProbeId { get; set; } = string.Empty;

    public string ProbeName { get; set; } = string.Empty;

    public NetworkDiscoveryRequest Request { get; set; } = new(Guid.Empty, string.Empty, DiscoveryDefaults.Options);

    public DiscoveryJobStatus Status { get; set; } = DiscoveryJobStatus.Pending;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? StartedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public IReadOnlyList<NetworkDiscoveryResult> Results { get; set; } = [];

    public string? Message { get; set; }

    public int TotalHosts { get; set; }

    public int ScannedHosts { get; set; }

    public int ProgressPercent => TotalHosts <= 0
        ? Status is DiscoveryJobStatus.Completed ? 100 : 0
        : (int)Math.Clamp(Math.Floor(ScannedHosts * 100d / TotalHosts), 0d, 100d);
}

public enum DiscoveryJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

public sealed record ProbeDiscoveryJobAssignmentsResponse(
    IReadOnlyList<ProbeDiscoveryJobAssignment> Jobs);

public sealed record ProbeDiscoveryJobAssignment(
    Guid JobId,
    string Network,
    NetworkDiscoveryOptions Options);

public sealed record ProbeDiscoveryJobResultBatch(
    IReadOnlyList<ProbeDiscoveryJobResult> Results);

public sealed record ProbeDiscoveryJobResult(
    Guid JobId,
    IReadOnlyList<NetworkDiscoveryResult> Hosts,
    string? ErrorMessage,
    bool IsComplete,
    int? ScannedHosts = null,
    int? TotalHosts = null);

public sealed record ProbeDiscoveryJobResultPostResponse(
    int Recorded,
    bool Cancelled);

public sealed record DiscoveryJobStatusResponse(
    Guid JobId,
    string ProbeName,
    string Network,
    string Status,
    string Message,
    bool IsComplete,
    int ScannedHosts,
    int TotalHosts,
    int ProgressPercent,
    IReadOnlyList<NetworkDiscoveryResult> Results);

public sealed record DiscoveryJobStoreDocument(
    IReadOnlyList<DiscoveryJobSnapshot> Jobs);

public static class DiscoveryDefaults
{
    public static NetworkDiscoveryOptions Options { get; } = new(
        UsePing: true,
        PingFirst: false,
        UseTcpPorts: true,
        TcpPorts: [22, 80, 135, 139, 443, 445, 1433, 3389, 5000, 5001, 5985, 5986, 8006, 8080, 8099, 8443],
        UseSnmp: false,
        SnmpCommunity: "public",
        SnmpVersion: "v2c",
        SnmpPort: 161,
        UseReverseDns: false,
        TimeoutMs: 650,
        MaxHosts: 65_534,
        Parallelism: 64);
}
