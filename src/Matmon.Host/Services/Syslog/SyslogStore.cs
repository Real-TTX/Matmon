using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Matmon.Core.Syslog;
using Microsoft.Data.Sqlite;

namespace Matmon.Host.Services;

public sealed record SyslogStorageInfo(
    bool Available,
    string? Error,
    long Messages,
    long DatabaseBytes,
    DateTimeOffset? OldestUtc,
    DateTimeOffset? NewestUtc,
    long Dropped);

/// <summary>
/// Where received syslog messages live. Kept apart from the telemetry database on purpose: it grows and is pruned on
/// its own terms (a switch that reboots in a loop can write thousands of lines a minute), and clearing or losing it must
/// never touch sensor history.
/// </summary>
public interface ISyslogStore
{
    /// <summary>Queues a message for storage. False when the queue is full - the message is dropped and counted, so a
    /// flood costs log lines rather than the monitor.</summary>
    bool TryEnqueue(SyslogMessage message);

    /// <summary>Newest first, at most <paramref name="limit"/>.</summary>
    IReadOnlyList<SyslogMessage> Search(SyslogQuery query, int limit);

    /// <summary>Messages per severity among those matching the query.</summary>
    SyslogCounts Count(SyslogQuery query);

    SyslogMessage? Latest(SyslogQuery query);

    /// <summary>When the newest retained message from any of these senders arrived - the "has it gone quiet?" signal.</summary>
    DateTimeOffset? LastReceived(IReadOnlyList<string> sources);

    IReadOnlyList<SyslogSourceSummary> GetSources(DateTimeOffset sinceUtc);

    SyslogStorageInfo GetStorageInfo();

    /// <summary>Deletes by age, then by count (oldest first). Returns how many messages went.</summary>
    int Prune(DateTimeOffset olderThanUtc, int maxMessages);

    void Clear();

    /// <summary>Waits until everything queued so far has been written.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// SQLite behind <see cref="ISyslogStore"/>. One writer (a background loop that inserts in batches inside one
/// transaction - a burst becomes a handful of commits instead of thousands) and a fresh read connection per query, so a
/// slow search never stalls ingestion; WAL lets the two run side by side.
///
/// It never throws at its callers for a storage problem: the sensors that read it are resolved by the same container
/// as every other sensor executor, and a missing or unwritable database must not take sensor execution down with it. A
/// problem is reported through <see cref="GetStorageInfo"/> and retried later.
/// </summary>
public sealed class SyslogStore : ISyslogStore, IDisposable
{
    public const int QueueCapacity = 100_000;
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly string _databasePath;
    private readonly ILogger<SyslogStore> _logger;
    private readonly Channel<SyslogMessage> _queue;
    private readonly object _dbLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private SqliteConnection? _writer;
    private Task? _writerTask;
    private bool _initialized;
    private string? _error;
    private DateTimeOffset _retryAtUtc = DateTimeOffset.MinValue;
    private long _enqueued;
    private long _written;
    private long _discarded; // queued, then lost because the database could not take the batch
    private long _dropped;   // everything that was lost: refused at the door, or discarded
    private bool _disposed;

    public SyslogStore(string databasePath, ILogger<SyslogStore> logger, int queueCapacity = QueueCapacity)
    {
        _databasePath = databasePath;
        _logger = logger;
        _queue = Channel.CreateBounded<SyslogMessage>(new BoundedChannelOptions(Math.Max(1, queueCapacity))
        {
            // Wait + TryWrite is the only combination where TryWrite reports "full" instead of quietly dropping.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    // ---- writing --------------------------------------------------------------------------------------------------

    public bool TryEnqueue(SyslogMessage message)
    {
        if (_disposed)
        {
            return false;
        }

        EnsureWriterStarted();
        if (_queue.Writer.TryWrite(message))
        {
            Interlocked.Increment(ref _enqueued);
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    private void EnsureWriterStarted()
    {
        if (_writerTask is not null)
        {
            return;
        }

        lock (_dbLock)
        {
            _writerTask ??= Task.Run(() => WriteLoopAsync(_stopping.Token));
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _queue.Reader;
        var batch = new List<SyslogMessage>(512);
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Drain(reader, batch, 5000);

                // A burst is still arriving: give it a moment, so it goes in as one transaction rather than many.
                if (batch.Count < 500)
                {
                    await Task.Delay(75, cancellationToken).ConfigureAwait(false);
                    Drain(reader, batch, 5000 - batch.Count);
                }

                WriteBatch(batch);
                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down: what is still queued gets one last write below
        }

        Drain(reader, batch, int.MaxValue);
        if (batch.Count > 0)
        {
            WriteBatch(batch);
        }
    }

    private static void Drain(ChannelReader<SyslogMessage> reader, List<SyslogMessage> batch, int max)
    {
        while (batch.Count < max && reader.TryRead(out var message))
        {
            batch.Add(message);
        }
    }

    private void WriteBatch(List<SyslogMessage> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        lock (_dbLock)
        {
            try
            {
                if (!EnsureInitializedLocked())
                {
                    Interlocked.Add(ref _dropped, batch.Count);
                    Interlocked.Add(ref _discarded, batch.Count);
                    return;
                }

                using var transaction = _writer!.BeginTransaction();
                using var command = _writer.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO syslog_message (received_ms, source, facility, severity, device_time, hostname, app, procid, msgid, format, message)
                    VALUES ($ts, $source, $facility, $severity, $deviceTime, $host, $app, $procid, $msgid, $format, $message)
                    """;
                var ts = command.Parameters.Add("$ts", SqliteType.Integer);
                var source = command.Parameters.Add("$source", SqliteType.Text);
                var facility = command.Parameters.Add("$facility", SqliteType.Integer);
                var severity = command.Parameters.Add("$severity", SqliteType.Integer);
                var deviceTime = command.Parameters.Add("$deviceTime", SqliteType.Text);
                var host = command.Parameters.Add("$host", SqliteType.Text);
                var app = command.Parameters.Add("$app", SqliteType.Text);
                var procId = command.Parameters.Add("$procid", SqliteType.Text);
                var msgId = command.Parameters.Add("$msgid", SqliteType.Text);
                var format = command.Parameters.Add("$format", SqliteType.Integer);
                var text = command.Parameters.Add("$message", SqliteType.Text);

                foreach (var message in batch)
                {
                    ts.Value = message.ReceivedUtc.ToUnixTimeMilliseconds();
                    source.Value = message.SourceAddress;
                    facility.Value = message.Facility;
                    severity.Value = message.Severity;
                    deviceTime.Value = (object?)message.DeviceTime ?? DBNull.Value;
                    host.Value = (object?)message.Hostname ?? DBNull.Value;
                    app.Value = (object?)message.AppName ?? DBNull.Value;
                    procId.Value = (object?)message.ProcId ?? DBNull.Value;
                    msgId.Value = (object?)message.MsgId ?? DBNull.Value;
                    format.Value = (int)message.Format;
                    text.Value = message.Message;
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
                Interlocked.Add(ref _written, batch.Count);
            }
            catch (Exception exception)
            {
                // Disk full, file locked, volume gone - or anything else: a writer loop that dies leaves the queue to fill
                // up silently, so say so once, drop this batch and try again later.
                Fail(exception);
                Interlocked.Add(ref _dropped, batch.Count);
                Interlocked.Add(ref _discarded, batch.Count);
            }
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var target = Interlocked.Read(ref _enqueued);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (Interlocked.Read(ref _written) + Interlocked.Read(ref _discarded) < target && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- reading --------------------------------------------------------------------------------------------------

    public IReadOnlyList<SyslogMessage> Search(SyslogQuery query, int limit)
    {
        limit = Math.Clamp(limit, 1, 5000);
        return Read(
            connection =>
            {
                using var command = connection.CreateCommand();
                var where = BuildWhere(query, command, connection);
                command.CommandText =
                    $"""
                    SELECT id, received_ms, source, facility, severity, device_time, hostname, app, procid, msgid, format, message
                    FROM syslog_message {where} ORDER BY id DESC LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$limit", limit);

                var result = new List<SyslogMessage>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(ReadMessage(reader));
                }

                return (IReadOnlyList<SyslogMessage>)result;
            },
            []);
    }

    public SyslogCounts Count(SyslogQuery query) =>
        Read(
            connection =>
            {
                using var command = connection.CreateCommand();
                var where = BuildWhere(query, command, connection);
                command.CommandText = $"SELECT severity, COUNT(*) FROM syslog_message {where} GROUP BY severity";

                var bySeverity = new long[8];
                long total = 0;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var severity = reader.GetInt32(0);
                    var count = reader.GetInt64(1);
                    if (severity is >= 0 and <= 7)
                    {
                        bySeverity[severity] += count;
                    }

                    total += count;
                }

                return new SyslogCounts(total, bySeverity);
            },
            SyslogCounts.Empty);

    public SyslogMessage? Latest(SyslogQuery query) =>
        Search(query, 1).FirstOrDefault();

    public DateTimeOffset? LastReceived(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
        {
            return null;
        }

        return Read(
            connection =>
            {
                using var command = connection.CreateCommand();
                var where = BuildWhere(new SyslogQuery { Sources = sources }, command, connection);
                command.CommandText = $"SELECT MAX(received_ms) FROM syslog_message {where}";
                return command.ExecuteScalar() is long milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : (DateTimeOffset?)null;
            },
            null);
    }

    public IReadOnlyList<SyslogSourceSummary> GetSources(DateTimeOffset sinceUtc) =>
        Read(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT source, COUNT(*), SUM(CASE WHEN severity <= 3 THEN 1 ELSE 0 END), MAX(received_ms), group_concat(DISTINCT hostname)
                    FROM syslog_message WHERE received_ms >= $since
                    GROUP BY source ORDER BY MAX(received_ms) DESC LIMIT 500
                    """;
                command.Parameters.AddWithValue("$since", sinceUtc.ToUnixTimeMilliseconds());

                var result = new List<SyslogSourceSummary>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var hostnames = reader.IsDBNull(4)
                        ? []
                        : reader.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    result.Add(new SyslogSourceSummary(
                        reader.GetString(0),
                        hostnames,
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
                }

                return (IReadOnlyList<SyslogSourceSummary>)result;
            },
            []);

    public SyslogStorageInfo GetStorageInfo()
    {
        var dropped = Interlocked.Read(ref _dropped);
        var info = Read(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*), MIN(received_ms), MAX(received_ms) FROM syslog_message";
                using var reader = command.ExecuteReader();
                reader.Read();
                var count = reader.GetInt64(0);
                return new SyslogStorageInfo(
                    true,
                    null,
                    count,
                    FileLength(_databasePath) + FileLength(_databasePath + "-wal"),
                    reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                    reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    dropped);
            },
            null);

        return info ?? new SyslogStorageInfo(false, _error ?? "The syslog database is not available.", 0, 0, null, null, dropped);
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // ---- housekeeping -----------------------------------------------------------------------------------------------

    public int Prune(DateTimeOffset olderThanUtc, int maxMessages)
    {
        lock (_dbLock)
        {
            if (!EnsureInitializedLocked())
            {
                return 0;
            }

            try
            {
                var deleted = Execute("DELETE FROM syslog_message WHERE received_ms < $cutoff", ("$cutoff", olderThanUtc.ToUnixTimeMilliseconds()));

                // The count cap: when more than the limit are left, everything at or below the id that sits "limit" rows
                // from the newest goes.
                if (ScalarLong("SELECT COUNT(*) FROM syslog_message") > maxMessages)
                {
                    var cutoffId = ScalarLong("SELECT id FROM syslog_message ORDER BY id DESC LIMIT 1 OFFSET $skip", ("$skip", maxMessages));
                    if (cutoffId > 0)
                    {
                        deleted += Execute("DELETE FROM syslog_message WHERE id <= $id", ("$id", cutoffId));
                    }
                }

                if (deleted > 0)
                {
                    // Hand freed pages back (auto_vacuum=INCREMENTAL on a database this code created); a bounded chunk
                    // per run keeps one prune from becoming a long lock.
                    Execute("PRAGMA incremental_vacuum(5000)");
                    Execute("PRAGMA wal_checkpoint(PASSIVE)");
                }

                return deleted;
            }
            catch (SqliteException exception)
            {
                Fail(exception);
                return 0;
            }
        }
    }

    public void Clear()
    {
        lock (_dbLock)
        {
            if (!EnsureInitializedLocked())
            {
                return;
            }

            try
            {
                Execute("DELETE FROM syslog_message");
                Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                Execute("VACUUM");
            }
            catch (SqliteException exception)
            {
                Fail(exception);
            }
        }
    }

    // ---- database ---------------------------------------------------------------------------------------------------

    /// <summary>Opens (and, the first time, creates) the database. Must hold <c>_dbLock</c>.</summary>
    private bool EnsureInitializedLocked()
    {
        if (_initialized)
        {
            return true;
        }

        if (DateTimeOffset.UtcNow < _retryAtUtc)
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer?.Dispose();
            _writer = new SqliteConnection(ConnectionString());
            _writer.Open();

            // Before the first table exists, or it has no effect: freed pages can then be handed back to the disk.
            Execute("PRAGMA auto_vacuum=INCREMENTAL");
            Execute("PRAGMA journal_mode=WAL");
            Execute("PRAGMA synchronous=NORMAL");
            Execute(
                """
                CREATE TABLE IF NOT EXISTS syslog_message (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    received_ms INTEGER NOT NULL,
                    source      TEXT    NOT NULL,
                    facility    INTEGER NOT NULL,
                    severity    INTEGER NOT NULL,
                    device_time TEXT    NULL,
                    hostname    TEXT    NULL,
                    app         TEXT    NULL,
                    procid      TEXT    NULL,
                    msgid       TEXT    NULL,
                    format      INTEGER NOT NULL,
                    message     TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_syslog_received ON syslog_message (received_ms);
                CREATE INDEX IF NOT EXISTS ix_syslog_source ON syslog_message (source COLLATE NOCASE, id);
                CREATE INDEX IF NOT EXISTS ix_syslog_host ON syslog_message (hostname COLLATE NOCASE, id);
                CREATE INDEX IF NOT EXISTS ix_syslog_severity ON syslog_message (severity, id);
                """);

            _initialized = true;
            _error = null;
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            Fail(exception);
            return false;
        }
    }

    private string ConnectionString() => new SqliteConnectionStringBuilder
    {
        DataSource = _databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        // No pool: the writer lives as long as the process and a reader lives for one query, and a pooled handle would
        // keep the file open after Dispose (which is what stops a folder from being deleted on Windows).
        Pooling = false
    }.ConnectionString;

    private void Fail(Exception exception)
    {
        _initialized = false;
        _error = exception.Message;
        _retryAtUtc = DateTimeOffset.UtcNow + RetryAfterFailure;
        _logger.LogWarning(exception, "The syslog database is not usable ({Message}); retrying in {Seconds}s", exception.Message, (int)RetryAfterFailure.TotalSeconds);
        try
        {
            _writer?.Dispose();
        }
        catch (Exception)
        {
            // already broken
        }

        _writer = null;
    }

    /// <summary>Runs a query on a connection of its own. A missing or broken database answers with the fallback.</summary>
    private T Read<T>(Func<SqliteConnection, T> query, T fallback)
    {
        lock (_dbLock)
        {
            if (!EnsureInitializedLocked())
            {
                return fallback;
            }
        }

        try
        {
            using var connection = new SqliteConnection(ConnectionString());
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA query_only=ON";
                pragma.ExecuteNonQuery();
            }

            return query(connection);
        }
        catch (SqliteException exception)
        {
            _logger.LogWarning(exception, "A syslog query failed: {Message}", exception.Message);
            return fallback;
        }
    }

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _writer!.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    private long ScalarLong(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _writer!.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteScalar() is long number ? number : 0;
    }

    // ---- the query language -----------------------------------------------------------------------------------------

    /// <summary>The WHERE clause for a query, with its parameters added to the command. The pieces combine with AND.</summary>
    private static string BuildWhere(SyslogQuery query, SqliteCommand command, SqliteConnection connection)
    {
        var conditions = new List<string>();

        if (query.FromUtc is { } from)
        {
            conditions.Add("received_ms >= $from");
            command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        }

        if (query.ToUtc is { } to)
        {
            conditions.Add("received_ms <= $to");
            command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        }

        var sources = query.Sources.Where(source => !string.IsNullOrWhiteSpace(source)).Select(source => source.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        if (sources.Length > 0)
        {
            var parts = new List<string>();
            for (var index = 0; index < sources.Length; index++)
            {
                parts.Add($"source = $s{index} COLLATE NOCASE OR hostname = $s{index} COLLATE NOCASE");
                command.Parameters.AddWithValue($"$s{index}", sources[index]);
            }

            conditions.Add("(" + string.Join(" OR ", parts) + ")");
        }

        if (query.MaxSeverity is { } severity)
        {
            conditions.Add("severity <= $severity");
            command.Parameters.AddWithValue("$severity", Math.Clamp(severity, 0, 7));
        }

        if (query.Facility is { } facility)
        {
            conditions.Add("facility = $facility");
            command.Parameters.AddWithValue("$facility", facility);
        }

        if (!string.IsNullOrWhiteSpace(query.Regex))
        {
            // SQLite's X REGEXP Y calls regexp(Y, X) - so the function is registered with the pattern first.
            var matcher = SyslogRegex.Create(query.Regex!);
            connection.CreateFunction("regexp", (string? pattern, string? value) => value is not null && matcher.IsMatch(value));
            conditions.Add("message REGEXP $regex");
            command.Parameters.AddWithValue("$regex", query.Regex!);
        }
        else if (!string.IsNullOrEmpty(query.Contains))
        {
            conditions.Add("message LIKE $contains ESCAPE '\\'");
            command.Parameters.AddWithValue("$contains", "%" + EscapeLike(query.Contains) + "%");
        }

        var excluded = query.Exclude.Where(phrase => !string.IsNullOrWhiteSpace(phrase)).Select(phrase => phrase.Trim()).Take(20).ToArray();
        for (var index = 0; index < excluded.Length; index++)
        {
            conditions.Add($"message NOT LIKE $x{index} ESCAPE '\\'");
            command.Parameters.AddWithValue($"$x{index}", "%" + EscapeLike(excluded[index]) + "%");
        }

        if (query.BeforeId is { } before)
        {
            conditions.Add("id < $before");
            command.Parameters.AddWithValue("$before", before);
        }

        return conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static SyslogMessage ReadMessage(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ReceivedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
        SourceAddress = reader.GetString(2),
        Facility = reader.GetInt32(3),
        Severity = reader.GetInt32(4),
        DeviceTime = reader.IsDBNull(5) ? null : reader.GetString(5),
        Hostname = reader.IsDBNull(6) ? null : reader.GetString(6),
        AppName = reader.IsDBNull(7) ? null : reader.GetString(7),
        ProcId = reader.IsDBNull(8) ? null : reader.GetString(8),
        MsgId = reader.IsDBNull(9) ? null : reader.GetString(9),
        Format = (SyslogFormat)reader.GetInt32(10),
        Message = reader.GetString(11)
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        _stopping.Cancel();
        try
        {
            _writerTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // the loop ends by cancellation
        }

        lock (_dbLock)
        {
            _writer?.Dispose();
            _writer = null;
        }

        _stopping.Dispose();
    }
}

/// <summary>
/// Regular expressions typed by a user and run against every candidate message. They get the linear-time engine
/// whenever the pattern allows it (a pattern cannot then be made to run forever), and a short timeout where it does not
/// (look-arounds and back-references need the backtracking one) - a bad pattern must cost a little time, not the monitor.
/// </summary>
public sealed class SyslogRegex
{
    private static readonly TimeSpan BacktrackingTimeout = TimeSpan.FromMilliseconds(50);

    private readonly Regex _regex;

    private SyslogRegex(Regex regex) => _regex = regex;

    /// <summary>A usable matcher, or the reason the pattern is not one. Case-insensitive, like the text search.</summary>
    public static bool TryCreate(string? pattern, out SyslogRegex? matcher, out string? error)
    {
        matcher = null;
        error = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "The pattern is empty.";
            return false;
        }

        if (pattern.Length > 500)
        {
            error = "The pattern is longer than 500 characters.";
            return false;
        }

        try
        {
            matcher = new SyslogRegex(Build(pattern));
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>For the query path, where the pattern was validated before: an invalid one matches nothing.</summary>
    internal static SyslogRegex Create(string pattern) =>
        TryCreate(pattern, out var matcher, out _) ? matcher! : new SyslogRegex(new Regex("(?!)"));

    public bool IsMatch(string text)
    {
        try
        {
            return _regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Regex Build(string pattern)
    {
        const RegexOptions common = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        try
        {
            return new Regex(pattern, common | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, common, BacktrackingTimeout);
        }
    }
}
