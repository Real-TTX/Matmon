namespace Matmon.Probe;

/// <summary>
/// A secondary probe's store-and-forward buffer for sensor observations it produced while the primary was
/// unreachable. Append-ordered (so index 0 is the oldest); <see cref="Prune"/> enforces an age window and a
/// hard count cap, and the worker drains it oldest-first once the link returns. Not thread-safe - it is only
/// touched from the single <see cref="SlaveSensorWorker"/> loop. With a <c>path</c> it is kept on disk as well
/// (<see cref="JsonLinesQueue{T}"/>), so an outage of the primary survives a restart of the probe too.
/// </summary>
public sealed class OfflineObservationBuffer
{
    private readonly JsonLinesQueue<ProbeSensorObservationReport> _queue;

    public OfflineObservationBuffer(string? path = null, Action<string>? warn = null)
    {
        _queue = new JsonLinesQueue<ProbeSensorObservationReport>(path, warn);
    }

    public int Count => _queue.Count;

    /// <summary>Where the buffer lives on disk (null = memory only).</summary>
    public string? FilePath => _queue.FilePath;

    public void Add(ProbeSensorObservationReport report) => _queue.AddRange([report]);

    public void AddRange(IEnumerable<ProbeSensorObservationReport> reports) => _queue.AddRange(reports);

    /// <summary>Drops observations older than <paramref name="retention"/> (by their execution timestamp), then
    /// caps the buffer to <paramref name="maxCount"/> by dropping the oldest. Returns how many were dropped.
    /// A non-positive <paramref name="retention"/> means "keep no history" - the whole buffer is cleared.</summary>
    public int Prune(DateTimeOffset now, TimeSpan retention, int maxCount)
    {
        if (retention <= TimeSpan.Zero)
        {
            var count = _queue.Count;
            _queue.Clear();
            return count;
        }

        var cutoff = now - retention;
        return _queue.Prune(report => report.TimestampUtc < cutoff, maxCount);
    }

    /// <summary>The oldest up-to-<paramref name="max"/> buffered reports (for a flush attempt), without removing them.</summary>
    public IReadOnlyList<ProbeSensorObservationReport> PeekOldest(int max) => _queue.PeekOldest(max);

    /// <summary>Removes the oldest <paramref name="count"/> reports (after they were flushed successfully).</summary>
    public void RemoveOldest(int count) => _queue.RemoveOldest(count);
}
