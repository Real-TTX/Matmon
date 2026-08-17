using System.Collections.Concurrent;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// Primary-only, in-memory store of pull-based on-demand run jobs. Mirrors <see cref="DiscoveryJobStore"/>
/// in spirit: the primary enqueues a job for a remote probe, the probe polls it via the run-jobs endpoint,
/// executes it and posts the result back, which completes the waiting request. Purely in-memory because an
/// on-demand run is inherently ephemeral - a restart mid-run just means the (already-returned) request
/// falls back to its "queued" message.
/// </summary>
public interface IOnDemandRunStore
{
    OnDemandRunJob Create(
        string probeId,
        Guid? sensorId,
        string sensorTypeKey,
        string target,
        MonitoringSettings settings,
        bool recordObservation,
        ProbeRunJobKind kind);

    IReadOnlyList<OnDemandRunJob> TakePending(string probeId, int max = 8);

    bool Complete(Guid jobId, SensorExecutionResult? result, IReadOnlyList<SnmpDiscoveryItem>? oids, string? error);

    OnDemandRunJob? TryGet(Guid jobId);

    Task<OnDemandRunJob?> WaitForCompletionAsync(Guid jobId, TimeSpan timeout, CancellationToken cancellationToken);
}

public static class OnDemandRunStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Complete = "Complete";
    public const string Failed = "Failed";

    public static bool IsFinished(string status) =>
        status is Complete or Failed;
}

public sealed class OnDemandRunJob
{
    public required Guid Id { get; init; }

    public required string ProbeId { get; init; }

    public Guid? SensorId { get; init; }

    public required string SensorTypeKey { get; init; }

    public required string Target { get; init; }

    public required MonitoringSettings Settings { get; init; }

    public bool RecordObservation { get; init; }

    public ProbeRunJobKind Kind { get; init; }

    public string Status { get; set; } = OnDemandRunStatus.Pending;

    public SensorExecutionResult? Result { get; set; }

    public IReadOnlyList<SnmpDiscoveryItem>? Oids { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; set; }

    // Completes when the probe reports back (or the job is pruned/failed), so a waiting request wakes
    // immediately instead of polling. RunContinuationsAsynchronously so the waiter never runs its
    // continuation on the thread that called Complete (which holds no lock, but keep it clean).
    internal TaskCompletionSource<OnDemandRunJob> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class OnDemandRunStore : IOnDemandRunStore
{
    // A run that the probe never picked up (e.g. an old build with no run-jobs endpoint, or a probe that
    // went offline) is failed after this window so the waiting request - and its TaskCompletionSource -
    // is unblocked instead of hanging until the request's own timeout.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    // Bounds memory: keep only the most recent jobs.
    private const int RetainLimit = 200;

    private readonly ConcurrentDictionary<Guid, OnDemandRunJob> _jobs = new();

    public OnDemandRunJob Create(
        string probeId,
        Guid? sensorId,
        string sensorTypeKey,
        string target,
        MonitoringSettings settings,
        bool recordObservation,
        ProbeRunJobKind kind)
    {
        Prune();

        var job = new OnDemandRunJob
        {
            Id = Guid.NewGuid(),
            ProbeId = probeId,
            SensorId = sensorId,
            SensorTypeKey = sensorTypeKey,
            Target = target,
            Settings = settings,
            RecordObservation = recordObservation,
            Kind = kind,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        _jobs[job.Id] = job;
        return job;
    }

    public IReadOnlyList<OnDemandRunJob> TakePending(string probeId, int max = 8)
    {
        var candidates = _jobs.Values
            .Where(job =>
                job.Status == OnDemandRunStatus.Pending &&
                string.Equals(job.ProbeId, probeId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(job => job.CreatedUtc)
            .Take(Math.Clamp(max, 1, 50))
            .ToArray();

        // Flip Pending -> Running under each job's lock and only hand back the ones we actually claimed,
        // so a second concurrent poll can't take the same job twice.
        var taken = new List<OnDemandRunJob>(candidates.Length);
        foreach (var job in candidates)
        {
            lock (job)
            {
                if (job.Status != OnDemandRunStatus.Pending)
                {
                    continue;
                }

                job.Status = OnDemandRunStatus.Running;
            }

            taken.Add(job);
        }

        return taken;
    }

    public bool Complete(Guid jobId, SensorExecutionResult? result, IReadOnlyList<SnmpDiscoveryItem>? oids, string? error)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        lock (job)
        {
            if (OnDemandRunStatus.IsFinished(job.Status))
            {
                return false;
            }

            job.Result = result;
            job.Oids = oids;
            job.Error = error;
            job.CompletedUtc = DateTimeOffset.UtcNow;
            job.Status = string.IsNullOrWhiteSpace(error)
                ? OnDemandRunStatus.Complete
                : OnDemandRunStatus.Failed;
        }

        // Wake any waiter. TrySetResult so a double-complete (probe retry) is harmless.
        job.Completion.TrySetResult(job);
        return true;
    }

    public OnDemandRunJob? TryGet(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;

    public async Task<OnDemandRunJob?> WaitForCompletionAsync(Guid jobId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return null;
        }

        if (OnDemandRunStatus.IsFinished(job.Status))
        {
            return job;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await job.Completion.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timed out (or the request was aborted) before the probe reported back - the caller falls
            // back to its "queued on probe" message; the result still lands on the next probe sync.
            return null;
        }
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var job in _jobs.Values)
        {
            if (OnDemandRunStatus.IsFinished(job.Status) || now - job.CreatedUtc <= StaleAfter)
            {
                continue;
            }

            // The probe never picked this up (or died mid-run). Fail it so the waiter unblocks.
            Complete(job.Id, null, null, "the probe did not pick up the run");
        }

        if (_jobs.Count <= RetainLimit)
        {
            return;
        }

        foreach (var id in _jobs.Values
            .OrderByDescending(job => job.CreatedUtc)
            .Skip(RetainLimit)
            .Select(job => job.Id)
            .ToArray())
        {
            _jobs.TryRemove(id, out _);
        }
    }
}
