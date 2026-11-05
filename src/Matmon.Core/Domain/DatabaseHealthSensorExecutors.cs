using System.Globalization;

namespace Matmon.Core.Domain;

/// <summary>
/// The database health sensors - SQL Server, PostgreSQL, MySQL / MariaDB - each one read-only query with a fixed set of channels,
/// run through the provider's own query sensor (<see cref="MssqlSensorExecutor"/>, <see cref="PostgreSqlSensorExecutor"/>,
/// <see cref="MySqlSensorExecutor"/>): the same connection settings, credential kinds and channel extraction, only the query is
/// built in. A column alias with spaces becomes a dotted channel key ("Blocked requests" -> <c>blocked.requests</c>), which is what
/// the default thresholds (<see cref="SensorThresholdDefaults"/>) are written against. Verified against SQL Server 2022, PostgreSQL 16,
/// MySQL 8.4 and MariaDB 11 (DatabaseHealthSensorTests, gated on MATMON_DB_HEALTH_*).
/// </summary>
internal static class DatabaseHealth
{
    /// <summary>The query sensor's parameters without its free query (and default-channel) field.</summary>
    public static SensorParameterDefinition[] ConnectionParameters(SensorDefinition queryDefinition) =>
        queryDefinition.Parameters
            .Where(parameter => !string.Equals(parameter.Key, "query", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(parameter.Key, "defaultChannelKey", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public static MonitoringSettings WithQuery(MonitoringSettings source, string query, string defaultChannel)
    {
        var settings = source.Clone();
        settings.Parameters["query"] = query;
        settings.Parameters["defaultChannelKey"] = defaultChannel;
        if (string.IsNullOrWhiteSpace(settings.DefaultChannelKey))
        {
            settings.DefaultChannelKey = defaultChannel;
        }

        return settings;
    }

    public static SensorDiscoverySuggestion Suggestion(string sensorTypeKey, string name, string reason, int port, string portKey,
        string defaultChannel, params (string Key, string Value)[] parameters)
    {
        var settings = new MonitoringSettings { Timeout = TimeSpan.FromSeconds(30), DefaultChannelKey = defaultChannel };
        settings.Parameters[portKey] = port.ToString(CultureInfo.InvariantCulture);
        foreach (var (key, value) in parameters)
        {
            settings.Parameters[key] = value;
        }

        return new SensorDiscoverySuggestion(sensorTypeKey, name, string.Empty, settings, reason, 86);
    }
}

/// <summary>SQL Server health: sessions, blocking, databases not online, missing full backups, failed agent jobs, page life expectancy, uptime.</summary>
public sealed class MssqlHealthSensorExecutor : ISensorExecutor
{
    private static readonly MssqlSensorExecutor Inner = new();

    public const string Query = """
SELECT
  (SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1) AS [User sessions],
  (SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id <> 0) AS [Blocked requests],
  (SELECT COUNT(*) FROM sys.databases WHERE state_desc <> 'ONLINE') AS [Databases not online],
  (SELECT COUNT(*) FROM sys.databases d WHERE d.database_id > 4 AND d.state_desc = 'ONLINE' AND d.source_database_id IS NULL
     AND NOT EXISTS (SELECT 1 FROM msdb.dbo.backupset b WHERE b.database_name = d.name AND b.type = 'D' AND b.backup_finish_date > DATEADD(hour, -26, GETDATE()))) AS [Databases without full backup 26h],
  (SELECT COUNT(*) FROM msdb.dbo.sysjobhistory h WHERE h.step_id = 0 AND h.run_status = 0
     AND msdb.dbo.agent_datetime(h.run_date, h.run_time) > DATEADD(hour, -24, GETDATE())) AS [Failed agent jobs 24h],
  (SELECT TOP 1 CAST(cntr_value AS bigint) FROM sys.dm_os_performance_counters WHERE counter_name = 'Page life expectancy' AND object_name LIKE '%Buffer Manager%') AS [Page life expectancy],
  (SELECT CAST(DATEDIFF(minute, sqlserver_start_time, GETDATE()) / 60.0 AS decimal(10,1)) FROM sys.dm_os_sys_info) AS [Uptime hours]
""";

    public const string DefaultChannel = "blocked.requests";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "mssql-health",
        DisplayName = "SQL Server Health",
        Description = "Microsoft SQL Server: user sessions, blocked requests, databases not online, user databases without a full backup in 26 h, failed SQL Agent jobs in 24 h, page life expectancy and uptime - one read-only query. Needs VIEW SERVER STATE and read access to msdb.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = DatabaseHealth.ConnectionParameters(MssqlSensorExecutor.Definition)
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(context.OpenTcpPorts.Contains(1433)
            ? SensorDiscoveryCheckResult.Available(DatabaseHealth.Suggestion(Definition.Key, "SQL Server Health",
                "SQL Server port 1433 is open. SQL credentials can be inherited from the parent.", 1433, "mssql.port", DefaultChannel,
                ("mssql.database", "master"), ("mssql.encrypt", "true"), ("mssql.trustServerCertificate", "true")))
            : SensorDiscoveryCheckResult.NotAvailable);

    public ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default) =>
        Inner.ExecuteAsync(new SensorExecutionContext(MssqlSensorExecutor.Definition.Key, context.Target,
            DatabaseHealth.WithQuery(context.Settings, Query, DefaultChannel), context.SensorId, context.PreviousObservation), cancellationToken);
}

/// <summary>PostgreSQL health: connections and their share of max_connections, lock waits, idle transactions, the longest transaction, deadlocks, size, replica lag.</summary>
public sealed class PostgresHealthSensorExecutor : ISensorExecutor
{
    private static readonly PostgreSqlSensorExecutor Inner = new();

    public const string Query = """
SELECT
  (SELECT count(*) FROM pg_stat_activity WHERE backend_type = 'client backend') AS "Connections",
  round(100.0 * (SELECT count(*) FROM pg_stat_activity WHERE backend_type = 'client backend') / current_setting('max_connections')::int, 1) AS "Connections used percent",
  (SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock') AS "Waiting on locks",
  (SELECT count(*) FROM pg_stat_activity WHERE state = 'idle in transaction') AS "Idle in transaction",
  round(COALESCE((SELECT EXTRACT(EPOCH FROM max(now() - xact_start)) FROM pg_stat_activity WHERE backend_type = 'client backend' AND state <> 'idle' AND xact_start IS NOT NULL), 0)::numeric, 0) AS "Longest transaction seconds",
  (SELECT sum(deadlocks) FROM pg_stat_database) AS "Deadlocks total",
  round(pg_database_size(current_database()) / 1073741824.0, 2) AS "Database size GB",
  round(COALESCE(EXTRACT(EPOCH FROM now() - pg_last_xact_replay_timestamp()), 0)::numeric, 0) AS "Replica lag seconds"
""";

    public const string DefaultChannel = "connections.used.percent";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "postgres-health",
        DisplayName = "PostgreSQL Health",
        Description = "PostgreSQL: client connections and their share of max_connections, sessions waiting on locks or idle in a transaction, the longest running transaction, deadlocks, database size and replica lag - one read-only query (PostgreSQL 10+; a pg_monitor member sees every session).",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = DatabaseHealth.ConnectionParameters(PostgreSqlSensorExecutor.Definition)
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(context.OpenTcpPorts.Contains(5432)
            ? SensorDiscoveryCheckResult.Available(DatabaseHealth.Suggestion(Definition.Key, "PostgreSQL Health",
                "PostgreSQL port 5432 is open. Credentials can be inherited from the parent.", 5432, "postgres.port", DefaultChannel,
                ("postgres.database", "postgres")))
            : SensorDiscoveryCheckResult.NotAvailable);

    public ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default) =>
        Inner.ExecuteAsync(new SensorExecutionContext(PostgreSqlSensorExecutor.Definition.Key, context.Target,
            DatabaseHealth.WithQuery(context.Settings, Query, DefaultChannel), context.SensorId, context.PreviousObservation), cancellationToken);
}

/// <summary>
/// MySQL / MariaDB health: connections and their share of max_connections, running threads, queries over 60 s, slow queries, aborted
/// connects, uptime. MariaDB (and MySQL 5.6) keep the status in information_schema, MySQL 5.7+ in performance_schema - the sensor asks
/// the first and falls back to the second, so one type serves both.
/// </summary>
public sealed class MySqlHealthSensorExecutor : ISensorExecutor
{
    private static readonly MySqlSensorExecutor Inner = new();

    public static string Query(string statusTable) => $"""
SELECT
  (SELECT VARIABLE_VALUE FROM {statusTable} WHERE VARIABLE_NAME = 'Threads_connected') + 0 AS `Threads connected`,
  ROUND(100 * (SELECT VARIABLE_VALUE FROM {statusTable} WHERE VARIABLE_NAME = 'Threads_connected') / @@max_connections, 1) AS `Connections used percent`,
  (SELECT VARIABLE_VALUE FROM {statusTable} WHERE VARIABLE_NAME = 'Threads_running') + 0 AS `Threads running`,
  (SELECT COUNT(*) FROM information_schema.PROCESSLIST WHERE COMMAND NOT IN ('Sleep', 'Daemon', 'Binlog Dump', 'Binlog Dump GTID') AND TIME > 60) AS `Queries over 60s`,
  (SELECT VARIABLE_VALUE FROM {statusTable} WHERE VARIABLE_NAME = 'Slow_queries') + 0 AS `Slow queries total`,
  (SELECT VARIABLE_VALUE FROM {statusTable} WHERE VARIABLE_NAME = 'Aborted_connects') + 0 AS `Aborted connects total`,
  ROUND((SELECT VARIABLE_VALUE FROM {statusTable} WHERE VARIABLE_NAME = 'Uptime') / 3600, 1) AS `Uptime hours`
""";

    public const string DefaultChannel = "connections.used.percent";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "mysql-health",
        DisplayName = "MySQL / MariaDB Health",
        Description = "MySQL or MariaDB: connected threads and their share of max_connections, running threads, queries running over 60 s, slow queries, aborted connects and uptime - one read-only query (needs the PROCESS privilege to see every query).",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = DatabaseHealth.ConnectionParameters(MySqlSensorExecutor.Definition)
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(context.OpenTcpPorts.Contains(3306)
            ? SensorDiscoveryCheckResult.Available(DatabaseHealth.Suggestion(Definition.Key, "MySQL / MariaDB Health",
                "MySQL / MariaDB port 3306 is open. Credentials can be inherited from the parent.", 3306, "mysql.port", DefaultChannel,
                ("mysql.database", "information_schema")))
            : SensorDiscoveryCheckResult.NotAvailable);

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        var mariaDb = await RunAsync(context, "information_schema.GLOBAL_STATUS", cancellationToken);
        if (mariaDb.State != SensorState.Critical || !IsMissingStatusTable(mariaDb.Message))
        {
            return mariaDb;
        }

        return await RunAsync(context, "performance_schema.global_status", cancellationToken);
    }

    // MySQL 5.7+ answers "Unknown table 'GLOBAL_STATUS' in information_schema" (or, with show_compatibility_56, a deprecation error).
    public static bool IsMissingStatusTable(string? message) =>
        message is not null && message.Contains("GLOBAL_STATUS", StringComparison.OrdinalIgnoreCase);

    private static ValueTask<SensorExecutionResult> RunAsync(SensorExecutionContext context, string statusTable, CancellationToken cancellationToken) =>
        Inner.ExecuteAsync(new SensorExecutionContext(MySqlSensorExecutor.Definition.Key, context.Target,
            DatabaseHealth.WithQuery(context.Settings, Query(statusTable), DefaultChannel), context.SensorId, context.PreviousObservation), cancellationToken);
}
