using System.Text.Json;

namespace Matmon.Probe;

/// <summary>
/// A secondary probe's store-and-forward buffer for sensor observations it produced while the primary was
/// unreachable. Append-ordered (so index 0 is the oldest); <see cref="Prune"/> enforces an age window and a
/// hard count cap, and the worker drains it oldest-first once the link returns. Not thread-safe - it is only
/// touched from the single <see cref="SlaveSensorWorker"/> loop.
/// <para>
/// With a <c>path</c> it is kept ON DISK as well, so an outage of the primary survives a restart of the probe too (an
/// agent updating itself, a reboot, a container recreated): every report is appended to a JSON-lines file as it is
/// buffered, a flush only advances a small "consumed" counter beside it, and the file is rewritten (compacted) only
/// once most of it has been sent or when pruning dropped something - so a long outage does not turn into rewriting
/// megabytes for every batch of 500. A torn last line (the process died mid-write) is skipped on load.
/// </para>
/// </summary>
public sealed class OfflineObservationBuffer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<ProbeSensorObservationReport> _items = [];
    private readonly string? _path;
    private readonly Action<string>? _warn;
    private int _consumed;

    public OfflineObservationBuffer(string? path = null, Action<string>? warn = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        _warn = warn;
        Load();
    }

    public int Count => _items.Count;

    /// <summary>Where the buffer lives on disk (null = memory only).</summary>
    public string? FilePath => _path;

    public void Add(ProbeSensorObservationReport report) => AddRange([report]);

    public void AddRange(IEnumerable<ProbeSensorObservationReport> reports)
    {
        var added = reports.ToList();
        if (added.Count == 0)
        {
            return;
        }

        _items.AddRange(added);
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var writer = new StreamWriter(_path, append: true);
            foreach (var report in added)
            {
                writer.WriteLine(JsonSerializer.Serialize(report, Json));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The memory copy still holds them - a full or read-only disk costs the restart safety, not the data.
            _warn?.Invoke($"Could not write the offline buffer to {_path}: {exception.Message}");
        }
    }

    /// <summary>Drops observations older than <paramref name="retention"/> (by their execution timestamp), then
    /// caps the buffer to <paramref name="maxCount"/> by dropping the oldest. Returns how many were dropped.
    /// A non-positive <paramref name="retention"/> means "keep no history" - the whole buffer is cleared.</summary>
    public int Prune(DateTimeOffset now, TimeSpan retention, int maxCount)
    {
        var before = _items.Count;

        if (retention <= TimeSpan.Zero)
        {
            _items.Clear();
        }
        else
        {
            var cutoff = now - retention;
            _items.RemoveAll(report => report.TimestampUtc < cutoff);

            if (maxCount > 0 && _items.Count > maxCount)
            {
                _items.RemoveRange(0, _items.Count - maxCount);
            }
        }

        var dropped = before - _items.Count;
        if (dropped > 0)
        {
            Compact();
        }

        return dropped;
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

        var removed = Math.Min(count, _items.Count);
        _items.RemoveRange(0, removed);
        if (_path is null)
        {
            return;
        }

        _consumed += removed;
        if (_items.Count == 0 || (_consumed >= 1000 && _consumed > _items.Count))
        {
            Compact();
        }
        else
        {
            WriteConsumed();
        }
    }

    private string ConsumedPath => _path + ".consumed";

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var skip = File.Exists(ConsumedPath) && int.TryParse(File.ReadAllText(ConsumedPath).Trim(), out var consumed) ? Math.Max(0, consumed) : 0;
            var index = 0;
            foreach (var line in File.ReadLines(_path))
            {
                if (index++ < skip || string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<ProbeSensorObservationReport>(line, Json) is { } report)
                    {
                        _items.Add(report);
                    }
                }
                catch (JsonException)
                {
                    // A torn line from a crash mid-write - the rest of the file is still good.
                }
            }

            // Start from a clean file: the consumed prefix and any torn line are gone after this.
            Compact();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"Could not read the offline buffer {_path}: {exception.Message}");
        }
    }

    private void Compact()
    {
        _consumed = 0;
        if (_path is null)
        {
            return;
        }

        try
        {
            if (_items.Count == 0)
            {
                File.Delete(_path);
                File.Delete(ConsumedPath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            using (var writer = new StreamWriter(temporary, append: false))
            {
                foreach (var report in _items)
                {
                    writer.WriteLine(JsonSerializer.Serialize(report, Json));
                }
            }

            File.Move(temporary, _path, overwrite: true);
            File.Delete(ConsumedPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"Could not compact the offline buffer {_path}: {exception.Message}");
        }
    }

    private void WriteConsumed()
    {
        try
        {
            File.WriteAllText(ConsumedPath, _consumed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"Could not record the flushed part of the offline buffer: {exception.Message}");
        }
    }
}
