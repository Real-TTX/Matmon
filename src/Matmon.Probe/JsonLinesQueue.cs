using System.Text.Json;

namespace Matmon.Probe;

/// <summary>
/// An append-ordered queue (index 0 = oldest) that is kept in memory and, with a <c>path</c>, ON DISK as JSON lines - what
/// a probe holds back while its primary is unreachable (sensor observations, plugin events) and must still have after a
/// restart. Every item is appended to the file as it is added; removing the oldest only advances a small "consumed"
/// counter beside it; the file is rewritten (compacted) once most of it was taken, when items were dropped and on load -
/// so a long outage does not turn into rewriting megabytes for every batch sent. A torn last line (the process died
/// mid-write) is skipped on load. A disk that cannot be written costs the restart safety, never the items in memory.
/// Not thread-safe.
/// </summary>
public sealed class JsonLinesQueue<T> where T : class
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<T> _items = [];
    private readonly string? _path;
    private readonly Action<string>? _warn;
    private int _consumed;

    public JsonLinesQueue(string? path = null, Action<string>? warn = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        _warn = warn;
        Load();
    }

    public int Count => _items.Count;

    /// <summary>Where the queue lives on disk (null = memory only).</summary>
    public string? FilePath => _path;

    public void AddRange(IEnumerable<T> items)
    {
        var added = items.ToList();
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
            foreach (var item in added)
            {
                writer.WriteLine(JsonSerializer.Serialize(item, Json));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"Could not write {_path}: {exception.Message}");
        }
    }

    /// <summary>The oldest up-to-<paramref name="max"/> items, without removing them.</summary>
    public IReadOnlyList<T> PeekOldest(int max) =>
        max <= 0 || _items.Count == 0 ? [] : _items.GetRange(0, Math.Min(max, _items.Count));

    /// <summary>Removes the oldest <paramref name="count"/> items (after they were sent).</summary>
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

    /// <summary>Drops every item <paramref name="expired"/> says is too old, then the oldest beyond <paramref name="maxCount"/>
    /// (≤0 = no cap). Returns how many went.</summary>
    public int Prune(Func<T, bool> expired, int maxCount)
    {
        var before = _items.Count;
        _items.RemoveAll(item => expired(item));
        if (maxCount > 0 && _items.Count > maxCount)
        {
            _items.RemoveRange(0, _items.Count - maxCount);
        }

        var dropped = before - _items.Count;
        if (dropped > 0)
        {
            Compact();
        }

        return dropped;
    }

    public void Clear()
    {
        if (_items.Count == 0)
        {
            return;
        }

        _items.Clear();
        Compact();
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
                    if (JsonSerializer.Deserialize<T>(line, Json) is { } item)
                    {
                        _items.Add(item);
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
            _warn?.Invoke($"Could not read {_path}: {exception.Message}");
        }
    }

    private void Compact()
    {
        if (_path is null)
        {
            _consumed = 0;
            return;
        }

        // The sent-lines counter is reset only once the file really was rewritten: a compaction that failed leaves the
        // file as it was, and a counter of 0 next to it would send those lines again after a restart.
        try
        {
            if (_items.Count == 0)
            {
                File.Delete(_path);
                File.Delete(ConsumedPath);
                _consumed = 0;
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            using (var writer = new StreamWriter(temporary, append: false))
            {
                foreach (var item in _items)
                {
                    writer.WriteLine(JsonSerializer.Serialize(item, Json));
                }
            }

            MoveWithRetry(temporary, _path);
            File.Delete(ConsumedPath);
            _consumed = 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"Could not compact {_path}: {exception.Message}");
            // The file is unchanged, so at least the counter beside it has to say how much of it was sent.
            WriteConsumed();
        }
    }

    // On Windows a virus scanner or the indexer may hold a file for a moment right after it was written: a replace that
    // fails with a sharing violation is tried again before it counts as failed.
    private static void MoveWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50 * attempt);
            }
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
            _warn?.Invoke($"Could not record the sent part of {_path}: {exception.Message}");
        }
    }
}
