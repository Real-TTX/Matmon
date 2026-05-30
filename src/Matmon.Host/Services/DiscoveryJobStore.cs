using System.Collections.Concurrent;

namespace Matmon.Host.Services;

public sealed class DiscoveryJobStore
{
    private readonly ConcurrentDictionary<Guid, DiscoveryJobSnapshot> _jobs = new();

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
            CreatedUtc = DateTimeOffset.UtcNow
        };

        _jobs[job.JobId] = job;
        return job;
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
            job.Message = $"{job.Results.Count} host{(job.Results.Count == 1 ? string.Empty : "s")} discovered so far.";
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
                job.Message = "Discovery is running on probe.";
            }
        }

        return new ProbeDiscoveryJobAssignmentsResponse(
            jobs.Select(job => new ProbeDiscoveryJobAssignment(job.JobId, job.Request.Network, job.Request.Options)).ToArray());
    }

    public bool Complete(Guid jobId, IReadOnlyList<NetworkDiscoveryResult> results, string? errorMessage)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        lock (job)
        {
            if (results.Count > 0)
            {
                AddResults(jobId, results);
            }

            job.CompletedUtc = DateTimeOffset.UtcNow;
            job.Status = string.IsNullOrWhiteSpace(errorMessage)
                ? DiscoveryJobStatus.Completed
                : DiscoveryJobStatus.Failed;
            job.Message = string.IsNullOrWhiteSpace(errorMessage)
                ? $"{job.Results.Count} host{(job.Results.Count == 1 ? string.Empty : "s")} discovered."
                : errorMessage.Trim();
        }

        return true;
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
}

public enum DiscoveryJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
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
    bool IsComplete);

public sealed record DiscoveryJobStatusResponse(
    Guid JobId,
    string ProbeName,
    string Network,
    string Status,
    string Message,
    bool IsComplete,
    IReadOnlyList<NetworkDiscoveryResult> Results);

public static class DiscoveryDefaults
{
    public static NetworkDiscoveryOptions Options { get; } = new(
        UsePing: true,
        UseTcpPorts: true,
        TcpPorts: [22, 80, 443, 1433, 3389, 5000, 5001, 5985, 5986, 8006, 8099],
        UseSnmp: false,
        SnmpCommunity: "public",
        SnmpVersion: "v2c",
        SnmpPort: 161,
        UseReverseDns: false,
        TimeoutMs: 650,
        MaxHosts: 256,
        Parallelism: 64);
}
