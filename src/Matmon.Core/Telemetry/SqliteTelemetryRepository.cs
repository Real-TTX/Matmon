using System.Text.Json;
using Matmon.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Matmon.Core.Telemetry;

/// <summary>
/// Embedded SQLite implementation of <see cref="ITelemetryRepository"/>. Keeps a
/// single connection open for the lifetime of the process (WAL mode) and
/// serializes access with a lock - appropriate for the single-process,
/// self-hosted Matmon primary. Timestamps are stored as Unix milliseconds (UTC)
/// and per-observation channels as a JSON blob.
/// </summary>
public sealed class SqliteTelemetryRepository : ITelemetryRepository, IDisposable
{
    private static readonly JsonSerializerOptions ChannelJson = new(JsonSerializerDefaults.Web);

    private readonly object _sync = new();
    private readonly SqliteConnection _connection;

    // Latest observation per sensor, kept in memory so the dashboard/polling hot
    // paths don't scan the whole observation table on every call.
    private readonly Dictionary<Guid, SensorObservation> _latestBySensor = new();
    private bool _disposed;

    public SqliteTelemetryRepository(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        if (!IsInMemory(databasePath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ConnectionString;

        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        Initialize();
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        LoadLatestObservationCache();
    }

    private static bool IsInMemory(string path)
        => path.Contains(":memory:", StringComparison.OrdinalIgnoreCase) ||
           path.Contains("mode=memory", StringComparison.OrdinalIgnoreCase);

    private void Initialize()
    {
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA wal_autocheckpoint=1000;");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS observation (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                sensor_id           TEXT    NOT NULL,
                ts                  INTEGER NOT NULL,
                state               INTEGER NOT NULL,
                value               REAL    NULL,
                default_channel_key TEXT    NULL,
                channels            TEXT    NULL,
                executed_probe_id   TEXT    NULL,
                executed_probe_name TEXT    NULL,
                duration_ticks      INTEGER NOT NULL,
                message             TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_obs_sensor_ts ON observation (sensor_id, ts);
            CREATE INDEX IF NOT EXISTS ix_obs_ts ON observation (ts);

            CREATE TABLE IF NOT EXISTS event (
                id           TEXT    PRIMARY KEY,
                ts           INTEGER NOT NULL,
                kind         INTEGER NOT NULL,
                element_id   TEXT    NULL,
                element_kind INTEGER NULL,
                element_name TEXT    NOT NULL,
                element_path TEXT    NOT NULL,
                state        INTEGER NULL,
                message      TEXT    NOT NULL,
                details      TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_event_ts ON event (ts);

            CREATE TABLE IF NOT EXISTS sensor_statistics (
                sensor_id           TEXT    NOT NULL,
                bucket_minutes      INTEGER NOT NULL,
                bucket_start        INTEGER NOT NULL,
                default_channel_key TEXT    NOT NULL,
                state               INTEGER NOT NULL,
                sample_count        INTEGER NOT NULL,
                average             REAL    NULL,
                minimum             REAL    NULL,
                maximum             REAL    NULL,
                last_value          REAL    NULL,
                unit                TEXT    NULL,
                message             TEXT    NULL,
                low_percentile      REAL    NULL,
                high_percentile     REAL    NULL,
                healthy_count       INTEGER NOT NULL DEFAULT 0,
                warning_count       INTEGER NOT NULL DEFAULT 0,
                critical_count      INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (sensor_id, bucket_minutes, bucket_start, default_channel_key)
            );
            """);

        EnsureStatisticsColumns();
        MigrateStatisticsToPerChannelKey();
    }

    // Adds the richer-statistics columns to a sensor_statistics table created by
    // an older Matmon build (SQLite has no ADD COLUMN IF NOT EXISTS).
    private void EnsureStatisticsColumns()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(sensor_statistics);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        var additions = new (string Column, string Definition)[]
        {
            ("low_percentile", "REAL NULL"),
            ("high_percentile", "REAL NULL"),
            ("healthy_count", "INTEGER NOT NULL DEFAULT 0"),
            ("warning_count", "INTEGER NOT NULL DEFAULT 0"),
            ("critical_count", "INTEGER NOT NULL DEFAULT 0"),
        };

        foreach (var (column, definition) in additions)
        {
            if (!existing.Contains(column))
            {
                ExecuteLocked($"ALTER TABLE sensor_statistics ADD COLUMN {column} {definition};");
            }
        }
    }

    // Statistics used to be stored per sensor (one bucket per window, the primary
    // channel only). They are now stored per channel, which needs default_channel_key
    // in the primary key. SQLite can't ALTER a primary key, so a table created by an
    // older build is rebuilt once. Existing rows keep their channel key, so prior
    // history stays intact (as the sensor's then-primary channel).
    private void MigrateStatisticsToPerChannelKey()
    {
        var channelInPrimaryKey = false;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(sensor_statistics);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // columns: cid, name, type, notnull, dflt_value, pk
                if (string.Equals(reader.GetString(1), "default_channel_key", StringComparison.OrdinalIgnoreCase) &&
                    reader.GetInt32(5) > 0)
                {
                    channelInPrimaryKey = true;
                }
            }
        }

        if (channelInPrimaryKey)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        ExecuteLocked("ALTER TABLE sensor_statistics RENAME TO sensor_statistics_legacy;");
        ExecuteLocked(
            """
            CREATE TABLE sensor_statistics (
                sensor_id           TEXT    NOT NULL,
                bucket_minutes      INTEGER NOT NULL,
                bucket_start        INTEGER NOT NULL,
                default_channel_key TEXT    NOT NULL,
                state               INTEGER NOT NULL,
                sample_count        INTEGER NOT NULL,
                average             REAL    NULL,
                minimum             REAL    NULL,
                maximum             REAL    NULL,
                last_value          REAL    NULL,
                unit                TEXT    NULL,
                message             TEXT    NULL,
                low_percentile      REAL    NULL,
                high_percentile     REAL    NULL,
                healthy_count       INTEGER NOT NULL DEFAULT 0,
                warning_count       INTEGER NOT NULL DEFAULT 0,
                critical_count      INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (sensor_id, bucket_minutes, bucket_start, default_channel_key)
            );
            """);
        ExecuteLocked(
            """
            INSERT INTO sensor_statistics
                (sensor_id, bucket_minutes, bucket_start, default_channel_key, state,
                 sample_count, average, minimum, maximum, last_value, unit, message,
                 low_percentile, high_percentile, healthy_count, warning_count, critical_count)
            SELECT
                sensor_id, bucket_minutes, bucket_start, default_channel_key, state,
                sample_count, average, minimum, maximum, last_value, unit, message,
                low_percentile, high_percentile, healthy_count, warning_count, critical_count
            FROM sensor_statistics_legacy;
            """);
        ExecuteLocked("DROP TABLE sensor_statistics_legacy;");
        transaction.Commit();
    }

    // --- Observations --------------------------------------------------------

    public void AppendObservation(SensorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_sync)
        {
            InsertObservationLocked(observation);
        }
    }

    private void InsertObservationLocked(SensorObservation observation)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO observation
                (sensor_id, ts, state, value, default_channel_key, channels,
                 executed_probe_id, executed_probe_name, duration_ticks, message)
            VALUES
                ($sensor, $ts, $state, $value, $channelKey, $channels,
                 $probeId, $probeName, $duration, $message);
            """;
        cmd.Parameters.AddWithValue("$sensor", observation.SensorId.ToString());
        cmd.Parameters.AddWithValue("$ts", observation.TimestampUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$state", (int)observation.State);
        cmd.Parameters.AddWithValue("$value", ToDb(observation.Value));
        cmd.Parameters.AddWithValue("$channelKey", ToDb(observation.DefaultChannelKey));
        cmd.Parameters.AddWithValue("$channels", ToDb(SerializeChannels(observation.Channels)));
        cmd.Parameters.AddWithValue("$probeId", ToDb(observation.ExecutedByProbeId));
        cmd.Parameters.AddWithValue("$probeName", ToDb(observation.ExecutedByProbeName));
        cmd.Parameters.AddWithValue("$duration", observation.Duration.Ticks);
        cmd.Parameters.AddWithValue("$message", ToDb(observation.Message));
        cmd.ExecuteNonQuery();

        if (!_latestBySensor.TryGetValue(observation.SensorId, out var current) ||
            observation.TimestampUtc >= current.TimestampUtc)
        {
            _latestBySensor[observation.SensorId] = observation;
        }
    }

    public IReadOnlyDictionary<Guid, SensorObservation> GetLatestObservations()
    {
        lock (_sync)
        {
            return new Dictionary<Guid, SensorObservation>(_latestBySensor);
        }
    }

    public SensorObservation? GetLatestObservation(Guid sensorId)
    {
        lock (_sync)
        {
            return _latestBySensor.TryGetValue(sensorId, out var observation) ? observation : null;
        }
    }

    // Populates the in-memory latest-per-sensor cache from the table. Runs once at
    // startup and after bulk operations (migration/restore/global delete).
    private void LoadLatestObservationCache()
    {
        lock (_sync)
        {
            _latestBySensor.Clear();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT sensor_id, ts, state, value, default_channel_key, channels,
                       executed_probe_id, executed_probe_name, duration_ticks, message
                FROM (
                    SELECT *, ROW_NUMBER() OVER (PARTITION BY sensor_id ORDER BY ts DESC, id DESC) AS rn
                    FROM observation
                )
                WHERE rn = 1;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var observation = ReadObservation(reader);
                _latestBySensor[observation.SensorId] = observation;
            }
        }
    }

    private SensorObservation? QueryLatestObservationLocked(Guid sensorId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT sensor_id, ts, state, value, default_channel_key, channels,
                   executed_probe_id, executed_probe_name, duration_ticks, message
            FROM observation
            WHERE sensor_id = $sensor
            ORDER BY ts DESC, id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$sensor", sensorId.ToString());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadObservation(reader) : null;
    }

    public IReadOnlyList<SensorObservation> GetObservations(Guid sensorId, DateTimeOffset fromUtc, int? maxCount)
    {
        if (maxCount is <= 0)
        {
            return [];
        }

        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            if (maxCount is int limit)
            {
                // Take the last <limit> within the window, then return ascending.
                cmd.CommandText =
                    """
                    SELECT sensor_id, ts, state, value, default_channel_key, channels,
                           executed_probe_id, executed_probe_name, duration_ticks, message
                    FROM observation
                    WHERE sensor_id = $sensor AND ts >= $from
                    ORDER BY ts DESC, id DESC
                    LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$limit", limit);
            }
            else
            {
                cmd.CommandText =
                    """
                    SELECT sensor_id, ts, state, value, default_channel_key, channels,
                           executed_probe_id, executed_probe_name, duration_ticks, message
                    FROM observation
                    WHERE sensor_id = $sensor AND ts >= $from
                    ORDER BY ts ASC, id ASC;
                    """;
            }

            cmd.Parameters.AddWithValue("$sensor", sensorId.ToString());
            cmd.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeMilliseconds());

            var rows = ReadObservations(cmd);
            if (maxCount is not null)
            {
                rows.Reverse();
            }

            return rows;
        }
    }

    public IReadOnlyDictionary<Guid, SensorObservation[]> GetRecentObservationsBySensor(DateTimeOffset fromUtc, int maxPerSensor)
    {
        var limit = Math.Max(maxPerSensor, 1);
        var latest = GetLatestObservations();

        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT sensor_id, ts, state, value, default_channel_key, channels,
                       executed_probe_id, executed_probe_name, duration_ticks, message
                FROM (
                    SELECT *, ROW_NUMBER() OVER (PARTITION BY sensor_id ORDER BY ts DESC, id DESC) AS rn
                    FROM observation
                    WHERE ts >= $from
                )
                WHERE rn <= $limit;
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$limit", limit);

            var recent = new Dictionary<Guid, List<SensorObservation>>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var observation = ReadObservation(reader);
                    if (!recent.TryGetValue(observation.SensorId, out var list))
                    {
                        list = [];
                        recent[observation.SensorId] = list;
                    }

                    list.Add(observation);
                }
            }

            var result = new Dictionary<Guid, SensorObservation[]>(latest.Count);
            foreach (var (sensorId, latestObservation) in latest)
            {
                var observations = recent.TryGetValue(sensorId, out var windowed)
                    ? windowed
                    : [];

                if (!observations.Any(observation => observation.TimestampUtc == latestObservation.TimestampUtc))
                {
                    observations.Add(latestObservation);
                }

                result[sensorId] = observations
                    .OrderBy(observation => observation.TimestampUtc)
                    .TakeLast(limit)
                    .ToArray();
            }

            return result;
        }
    }

    public IReadOnlyList<SensorObservation> GetAllObservations()
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT sensor_id, ts, state, value, default_channel_key, channels,
                       executed_probe_id, executed_probe_name, duration_ticks, message
                FROM observation
                ORDER BY ts ASC, id ASC;
                """;
            return ReadObservations(cmd);
        }
    }

    public int PruneObservations(Guid sensorId, DateTimeOffset cutoffUtc)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM observation WHERE sensor_id = $sensor AND ts < $cutoff;";
            cmd.Parameters.AddWithValue("$sensor", sensorId.ToString());
            cmd.Parameters.AddWithValue("$cutoff", cutoffUtc.ToUnixTimeMilliseconds());
            var removed = cmd.ExecuteNonQuery();

            if (removed > 0)
            {
                var latest = QueryLatestObservationLocked(sensorId);
                if (latest is null)
                {
                    _latestBySensor.Remove(sensorId);
                }
                else
                {
                    _latestBySensor[sensorId] = latest;
                }
            }

            return removed;
        }
    }

    // --- Events --------------------------------------------------------------

    public void AppendEvent(MonitoringEvent monitoringEvent)
    {
        ArgumentNullException.ThrowIfNull(monitoringEvent);
        lock (_sync)
        {
            InsertEventLocked(monitoringEvent);
        }
    }

    private void InsertEventLocked(MonitoringEvent monitoringEvent)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR REPLACE INTO event
                (id, ts, kind, element_id, element_kind, element_name, element_path, state, message, details)
            VALUES
                ($id, $ts, $kind, $elementId, $elementKind, $elementName, $elementPath, $state, $message, $details);
            """;
        cmd.Parameters.AddWithValue("$id", monitoringEvent.Id.ToString());
        cmd.Parameters.AddWithValue("$ts", monitoringEvent.TimestampUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$kind", (int)monitoringEvent.Kind);
        cmd.Parameters.AddWithValue("$elementId", ToDb(monitoringEvent.ElementId?.ToString()));
        cmd.Parameters.AddWithValue("$elementKind", ToDb(monitoringEvent.ElementKind is { } kind ? (int)kind : (int?)null));
        cmd.Parameters.AddWithValue("$elementName", monitoringEvent.ElementName);
        cmd.Parameters.AddWithValue("$elementPath", monitoringEvent.ElementPath);
        cmd.Parameters.AddWithValue("$state", ToDb(monitoringEvent.State is { } state ? (int)state : (int?)null));
        cmd.Parameters.AddWithValue("$message", monitoringEvent.Message);
        cmd.Parameters.AddWithValue("$details", ToDb(monitoringEvent.Details));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<MonitoringEvent> GetEvents(int take)
    {
        if (take <= 0)
        {
            return [];
        }

        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, ts, kind, element_id, element_kind, element_name, element_path, state, message, details
                FROM event
                ORDER BY ts DESC
                LIMIT $take;
                """;
            cmd.Parameters.AddWithValue("$take", take);
            return ReadEvents(cmd);
        }
    }

    public IReadOnlyList<MonitoringEvent> GetAllEvents()
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, ts, kind, element_id, element_kind, element_name, element_path, state, message, details
                FROM event
                ORDER BY ts ASC;
                """;
            return ReadEvents(cmd);
        }
    }

    public int PruneEvents(DateTimeOffset cutoffUtc)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM event WHERE ts < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoffUtc.ToUnixTimeMilliseconds());
            return cmd.ExecuteNonQuery();
        }
    }

    // --- Statistics ----------------------------------------------------------

    public SensorStatisticsBucket? GetStatisticsBucket(Guid sensorId, int bucketMinutes, DateTimeOffset bucketStartUtc)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT sensor_id, bucket_minutes, bucket_start, default_channel_key, state,
                       sample_count, average, minimum, maximum, last_value, unit, message,
                       low_percentile, high_percentile, healthy_count, warning_count, critical_count
                FROM sensor_statistics
                WHERE sensor_id = $sensor AND bucket_minutes = $minutes AND bucket_start = $start
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$sensor", sensorId.ToString());
            cmd.Parameters.AddWithValue("$minutes", bucketMinutes);
            cmd.Parameters.AddWithValue("$start", bucketStartUtc.ToUnixTimeMilliseconds());

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadStatisticsBucket(reader) : null;
        }
    }

    public void UpsertStatisticsBucket(SensorStatisticsBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        lock (_sync)
        {
            UpsertStatisticsBucketLocked(bucket);
        }
    }

    private void UpsertStatisticsBucketLocked(SensorStatisticsBucket bucket)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO sensor_statistics
                (sensor_id, bucket_minutes, bucket_start, default_channel_key, state,
                 sample_count, average, minimum, maximum, last_value, unit, message,
                 low_percentile, high_percentile, healthy_count, warning_count, critical_count)
            VALUES
                ($sensor, $minutes, $start, $channelKey, $state,
                 $count, $average, $minimum, $maximum, $lastValue, $unit, $message,
                 $lowPct, $highPct, $healthy, $warning, $critical)
            ON CONFLICT (sensor_id, bucket_minutes, bucket_start, default_channel_key) DO UPDATE SET
                state               = excluded.state,
                sample_count        = excluded.sample_count,
                average             = excluded.average,
                minimum             = excluded.minimum,
                maximum             = excluded.maximum,
                last_value          = excluded.last_value,
                unit                = excluded.unit,
                message             = excluded.message,
                low_percentile      = excluded.low_percentile,
                high_percentile     = excluded.high_percentile,
                healthy_count       = excluded.healthy_count,
                warning_count       = excluded.warning_count,
                critical_count      = excluded.critical_count;
            """;
        cmd.Parameters.AddWithValue("$sensor", bucket.SensorId.ToString());
        cmd.Parameters.AddWithValue("$minutes", bucket.BucketMinutes);
        cmd.Parameters.AddWithValue("$start", bucket.BucketStartUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$channelKey", bucket.DefaultChannelKey);
        cmd.Parameters.AddWithValue("$state", (int)bucket.State);
        cmd.Parameters.AddWithValue("$count", bucket.SampleCount);
        cmd.Parameters.AddWithValue("$average", ToDb(bucket.Average));
        cmd.Parameters.AddWithValue("$minimum", ToDb(bucket.Minimum));
        cmd.Parameters.AddWithValue("$maximum", ToDb(bucket.Maximum));
        cmd.Parameters.AddWithValue("$lastValue", ToDb(bucket.LastValue));
        cmd.Parameters.AddWithValue("$unit", ToDb(bucket.Unit));
        cmd.Parameters.AddWithValue("$message", ToDb(bucket.Message));
        cmd.Parameters.AddWithValue("$lowPct", ToDb(bucket.LowPercentile));
        cmd.Parameters.AddWithValue("$highPct", ToDb(bucket.HighPercentile));
        cmd.Parameters.AddWithValue("$healthy", bucket.HealthyCount);
        cmd.Parameters.AddWithValue("$warning", bucket.WarningCount);
        cmd.Parameters.AddWithValue("$critical", bucket.CriticalCount);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SensorStatisticsBucket> GetStatistics(Guid sensorId)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT sensor_id, bucket_minutes, bucket_start, default_channel_key, state,
                       sample_count, average, minimum, maximum, last_value, unit, message,
                       low_percentile, high_percentile, healthy_count, warning_count, critical_count
                FROM sensor_statistics
                WHERE sensor_id = $sensor
                ORDER BY bucket_start ASC;
                """;
            cmd.Parameters.AddWithValue("$sensor", sensorId.ToString());
            return ReadStatistics(cmd);
        }
    }

    public IReadOnlyList<SensorStatisticsBucket> GetAllStatistics()
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT sensor_id, bucket_minutes, bucket_start, default_channel_key, state,
                       sample_count, average, minimum, maximum, last_value, unit, message,
                       low_percentile, high_percentile, healthy_count, warning_count, critical_count
                FROM sensor_statistics
                ORDER BY sensor_id ASC, bucket_start ASC;
                """;
            return ReadStatistics(cmd);
        }
    }

    public int PruneStatistics(Guid sensorId, DateTimeOffset cutoffUtc)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sensor_statistics WHERE sensor_id = $sensor AND bucket_start < $cutoff;";
            cmd.Parameters.AddWithValue("$sensor", sensorId.ToString());
            cmd.Parameters.AddWithValue("$cutoff", cutoffUtc.ToUnixTimeMilliseconds());
            return cmd.ExecuteNonQuery();
        }
    }

    public int PurgeSensor(Guid sensorId)
    {
        var id = sensorId.ToString();
        lock (_sync)
        {
            using var transaction = _connection.BeginTransaction();
            var removed = 0;
            foreach (var sql in new[]
            {
                "DELETE FROM observation WHERE sensor_id = $sensor;",
                "DELETE FROM sensor_statistics WHERE sensor_id = $sensor;",
                "DELETE FROM event WHERE element_id = $sensor;"
            })
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$sensor", id);
                removed += cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            _latestBySensor.Remove(sensorId);
            return removed;
        }
    }

    // --- Bulk maintenance / migration / backup -------------------------------

    public TelemetryCounts GetCounts()
    {
        lock (_sync)
        {
            return new TelemetryCounts(
                CountLocked("observation"),
                CountLocked("event"),
                CountLocked("sensor_statistics"));
        }
    }

    public IReadOnlyDictionary<Guid, int> GetObservationCountsBySensor()
    {
        var counts = new Dictionary<Guid, int>();
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT sensor_id, COUNT(*) FROM observation GROUP BY sensor_id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(0), out var sensorId))
                {
                    counts[sensorId] = Convert.ToInt32(reader.GetInt64(1));
                }
            }
        }

        return counts;
    }

    public int DeleteObservations(DateTimeOffset? olderThanUtc)
    {
        var removed = DeleteByTimestamp("observation", "ts", olderThanUtc);
        if (removed > 0)
        {
            lock (_sync)
            {
                if (olderThanUtc is null)
                {
                    _latestBySensor.Clear();
                }
                else
                {
                    LoadLatestObservationCache();
                }
            }
        }

        return removed;
    }

    public int DeleteEvents(DateTimeOffset? olderThanUtc) => DeleteByTimestamp("event", "ts", olderThanUtc);

    public int DeleteStatistics(DateTimeOffset? olderThanUtc) => DeleteByTimestamp("sensor_statistics", "bucket_start", olderThanUtc);

    public void ReplaceAllObservations(IEnumerable<SensorObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        lock (_sync)
        {
            _latestBySensor.Clear();
            using (var transaction = _connection.BeginTransaction())
            {
                ExecuteLocked("DELETE FROM observation;");
                foreach (var observation in observations)
                {
                    InsertObservationLocked(observation);
                }

                transaction.Commit();
            }

            // Flush the (potentially large) WAL after a bulk import so it doesn't
            // linger un-checkpointed and slow subsequent reads.
            ExecuteLocked("PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }

    public void ReplaceAllEvents(IEnumerable<MonitoringEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (_sync)
        {
            using var transaction = _connection.BeginTransaction();
            ExecuteLocked("DELETE FROM event;");
            foreach (var monitoringEvent in events)
            {
                InsertEventLocked(monitoringEvent);
            }

            transaction.Commit();
        }
    }

    public void ReplaceAllStatistics(IEnumerable<SensorStatisticsBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        lock (_sync)
        {
            using var transaction = _connection.BeginTransaction();
            ExecuteLocked("DELETE FROM sensor_statistics;");
            foreach (var bucket in buckets)
            {
                UpsertStatisticsBucketLocked(bucket);
            }

            transaction.Commit();
        }
    }

    // --- Helpers -------------------------------------------------------------

    private int DeleteByTimestamp(string table, string column, DateTimeOffset? olderThanUtc)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            if (olderThanUtc is { } cutoff)
            {
                cmd.CommandText = $"DELETE FROM {table} WHERE {column} < $cutoff;";
                cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
            }
            else
            {
                cmd.CommandText = $"DELETE FROM {table};";
            }

            return cmd.ExecuteNonQuery();
        }
    }

    private long CountLocked(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void Execute(string sql)
    {
        lock (_sync)
        {
            ExecuteLocked(sql);
        }
    }

    private void ExecuteLocked(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<SensorObservation> ReadObservations(SqliteCommand cmd)
    {
        var rows = new List<SensorObservation>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadObservation(reader));
        }

        return rows;
    }

    private static SensorObservation ReadObservation(SqliteDataReader reader)
    {
        return new SensorObservation
        {
            SensorId = Guid.Parse(reader.GetString(0)),
            TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            State = (SensorState)reader.GetInt32(2),
            Value = reader.IsDBNull(3) ? null : reader.GetDouble(3),
            DefaultChannelKey = reader.IsDBNull(4) ? null : reader.GetString(4),
            Channels = DeserializeChannels(reader.IsDBNull(5) ? null : reader.GetString(5)),
            ExecutedByProbeId = reader.IsDBNull(6) ? null : reader.GetString(6),
            ExecutedByProbeName = reader.IsDBNull(7) ? null : reader.GetString(7),
            Duration = TimeSpan.FromTicks(reader.GetInt64(8)),
            Message = reader.IsDBNull(9) ? null : reader.GetString(9)
        };
    }

    private static List<MonitoringEvent> ReadEvents(SqliteCommand cmd)
    {
        var rows = new List<MonitoringEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new MonitoringEvent
            {
                Id = Guid.Parse(reader.GetString(0)),
                TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                Kind = (MonitoringEventKind)reader.GetInt32(2),
                ElementId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                ElementKind = reader.IsDBNull(4) ? null : (MonitoringElementKind)reader.GetInt32(4),
                ElementName = reader.GetString(5),
                ElementPath = reader.GetString(6),
                State = reader.IsDBNull(7) ? null : (SensorState)reader.GetInt32(7),
                Message = reader.GetString(8),
                Details = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }

        return rows;
    }

    private static List<SensorStatisticsBucket> ReadStatistics(SqliteCommand cmd)
    {
        var rows = new List<SensorStatisticsBucket>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadStatisticsBucket(reader));
        }

        return rows;
    }

    private static SensorStatisticsBucket ReadStatisticsBucket(SqliteDataReader reader)
    {
        return new SensorStatisticsBucket
        {
            SensorId = Guid.Parse(reader.GetString(0)),
            BucketMinutes = reader.GetInt32(1),
            BucketStartUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            DefaultChannelKey = reader.GetString(3),
            State = (SensorState)reader.GetInt32(4),
            SampleCount = reader.GetInt32(5),
            Average = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            Minimum = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            Maximum = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            LastValue = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            Unit = reader.IsDBNull(10) ? null : reader.GetString(10),
            Message = reader.IsDBNull(11) ? null : reader.GetString(11),
            LowPercentile = reader.IsDBNull(12) ? null : reader.GetDouble(12),
            HighPercentile = reader.IsDBNull(13) ? null : reader.GetDouble(13),
            HealthyCount = reader.GetInt32(14),
            WarningCount = reader.GetInt32(15),
            CriticalCount = reader.GetInt32(16)
        };
    }

    private static string? SerializeChannels(IReadOnlyList<SensorChannelValue> channels)
    {
        return channels.Count == 0 ? null : JsonSerializer.Serialize(channels, ChannelJson);
    }

    private static List<SensorChannelValue> DeserializeChannels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<SensorChannelValue>>(json, ChannelJson) ?? [];
    }

    private static object ToDb(object? value) => value ?? DBNull.Value;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
        SqliteConnection.ClearPool(_connection);
    }
}
