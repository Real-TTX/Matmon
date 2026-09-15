namespace Matmon.Host.Services;

/// <summary>
/// A secondary probe's store-and-forward buffer for sensor observations it produced while the primary was
/// unreachable. Append-ordered (so index 0 is the oldest); <see cref="Prune"/> enforces an age window and a
/// hard count cap, and the worker drains it oldest-first once the link returns. Not thread-safe - it is only
/// touched from the single <see cref="SlaveSensorWorker"/> loop.
/// </summary>
public sealed class OfflineObservationBuffer
{
    private readonly List<ProbeSensorObservationReport> _items = [];

    public int Count => _items.Count;

    public void Add(ProbeSensorObservationReport report) => _items.Add(report);

    public void AddRange(IEnumerable<ProbeSensorObservationReport> reports) => _items.AddRange(reports);

    /// <summary>Drops observations older than <paramref name="retention"/> (by their execution timestamp), then
    /// caps the buffer to <paramref name="maxCount"/> by dropping the oldest. Returns how many were dropped.
    /// A non-positive <paramref name="retention"/> means "keep no history" - the whole buffer is cleared.</summary>
    public int Prune(DateTimeOffset now, TimeSpan retention, int maxCount)
    {
        var before = _items.Count;

        if (retention <= TimeSpan.Zero)
        {
            _items.Clear();
            return before;
        }

        var cutoff = now - retention;
        _items.RemoveAll(report => report.TimestampUtc < cutoff);

        if (maxCount > 0 && _items.Count > maxCount)
        {
            _items.RemoveRange(0, _items.Count - maxCount);
        }

        return before - _items.Count;
    }

    /// <summary>The oldest up-to-<paramref name="max"/> buffered reports (for a flush attempt), without removing them.</summary>
    public IReadOnlyList<ProbeSensorObservationReport> PeekOldest(int max)
    {
        if (max <= 0 || _items.Count == 0)
        {
            return [];
        }

        return _items.GetRange(0, Math.Min(max, _items.Count));
    }

    /// <summary>Removes the oldest <paramref name="count"/> reports (after they were flushed successfully).</summary>
    public void RemoveOldest(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _items.RemoveRange(0, Math.Min(count, _items.Count));
    }
}
