namespace Matmon.Host.Services;

/// <summary>One captured log line held in the in-memory ring buffer that backs the admin "Logs" viewer.</summary>
/// <param name="Seq">Monotonic sequence number (newest = highest); lets the viewer request "only newer than X".</param>
public sealed record LogEntry(
    long Seq,
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception);

/// <summary>
/// A bounded, thread-safe in-memory ring buffer of the most recent application log lines, fed by
/// <see cref="RingBufferLoggerProvider"/> and read by the admin Logs page. Dependency-free (just
/// Microsoft.Extensions.Logging.Abstractions) and process-local: it holds the newest ~N entries and is
/// lost on restart - it's a live debugging window (like an in-app <c>docker logs --tail</c>), not an audit trail.
/// </summary>
public sealed class InMemoryLogStore
{
    private readonly object _gate = new();
    private readonly LogEntry[] _ring;
    private int _next;   // index of the next slot to write
    private int _count;  // number of populated slots (<= capacity)
    private long _seq;

    public InMemoryLogStore(int capacity = 2000)
    {
        _ring = new LogEntry[Math.Max(64, capacity)];
    }

    public int Capacity => _ring.Length;

    /// <summary>Append a line, overwriting the oldest once full. Cheap + non-blocking (single short lock).</summary>
    public void Add(LogLevel level, string category, string message, string? exception)
    {
        lock (_gate)
        {
            _ring[_next] = new LogEntry(++_seq, DateTimeOffset.UtcNow, level, category ?? string.Empty, message ?? string.Empty, exception);
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length) { _count++; }
        }
    }

    /// <summary>Newest-first snapshot, filtered by minimum level and a case-insensitive text match on
    /// message/category. Capped at <paramref name="limit"/>. Pure read - safe to call from a request thread.</summary>
    public IReadOnlyList<LogEntry> Snapshot(LogLevel minLevel = LogLevel.Trace, string? search = null, int limit = 500)
    {
        limit = Math.Clamp(limit, 1, _ring.Length);
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var result = new List<LogEntry>(Math.Min(limit, _count));
        lock (_gate)
        {
            for (var i = 0; i < _count && result.Count < limit; i++)
            {
                var idx = (_next - 1 - i + _ring.Length) % _ring.Length;
                var e = _ring[idx];
                if (e is null || e.Level < minLevel)
                {
                    continue;
                }
                if (term is not null
                    && !e.Message.Contains(term, StringComparison.OrdinalIgnoreCase)
                    && !e.Category.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                result.Add(e);
            }
        }
        return result;
    }

    /// <summary>Drop all buffered lines (admin "Clear" action).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _next = 0;
            _count = 0;
        }
    }
}
