using Npgsql;

namespace Matmon.Core.Domain;

/// <summary>Runs a PostgreSQL query and turns numeric result columns into channels. Mirrors the MSSQL sensor;
/// the query execution + channel extraction is shared via <see cref="SqlQuerySensorSupport"/>.</summary>
public sealed class PostgreSqlSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "postgres",
        DisplayName = "PostgreSQL",
        Description = "Runs a PostgreSQL query and turns numeric result columns into channels.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "postgres.database",
                Label = "Database",
                Kind = SensorParameterKind.Text,
                Description = "Database name",
                DefaultValue = "postgres",
                Placeholder = "postgres"
            },
            new SensorParameterDefinition
            {
                Key = "postgres.username",
                Label = "Username",
                Kind = SensorParameterKind.Text,
                Description = "PostgreSQL login (role).",
                CredentialKind = MonitoringCredentialKind.PostgreSql,
                Placeholder = "monitor"
            },
            new SensorParameterDefinition
            {
                Key = "postgres.password",
                Label = "Password",
                Kind = SensorParameterKind.Secret,
                Description = "PostgreSQL login password",
                CredentialKind = MonitoringCredentialKind.PostgreSql
            },
            new SensorParameterDefinition
            {
                Key = "postgres.port",
                Label = "Port",
                Kind = SensorParameterKind.Integer,
                Description = "PostgreSQL TCP port",
                DefaultValue = "5432",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "postgres.sslMode",
                Label = "SSL mode",
                Kind = SensorParameterKind.ValueList,
                Description = "TLS negotiation with the server.",
                DefaultValue = "Prefer",
                Options =
                [
                    new SensorParameterOption { Value = "Prefer", Label = "Prefer" },
                    new SensorParameterOption { Value = "Require", Label = "Require" },
                    new SensorParameterOption { Value = "VerifyFull", Label = "Verify full" },
                    new SensorParameterOption { Value = "Disable", Label = "Disable" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "postgres.connectionString",
                Label = "Connection string",
                Kind = SensorParameterKind.Multiline,
                Description = "Optional full connection string. If set, it overrides host/database/user fields.",
                Placeholder = "Host=db01;Port=5432;Database=postgres;Username=monitor;Password=secret;SSL Mode=Prefer"
            },
            new SensorParameterDefinition
            {
                Key = "query",
                Label = "Query",
                Kind = SensorParameterKind.Multiline,
                Description = "Numeric columns become channels. Multiple rows can use columns channel,value,unit.",
                Required = true,
                Placeholder = "SELECT 1 AS value"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!context.OpenTcpPorts.Contains(5432))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["postgres.database"] = "postgres";
        settings.Parameters["postgres.port"] = "5432";
        settings.Parameters["postgres.sslMode"] = "Prefer";
        settings.Parameters["query"] = "SELECT 1 AS value";
        settings.Parameters["defaultChannelKey"] = "value";
        settings.DefaultChannelKey = "value";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "PostgreSQL Query",
                    string.Empty,
                    settings,
                    "PostgreSQL port 5432 is open. Credentials can be inherited from the parent.",
                    82)));
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target) &&
            !MonitoringSettings.TryReadParameter(context.Settings, "postgres.connectionString", out _))
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "target or connection string is required"));
        }

        if (!MonitoringSettings.TryReadParameter(context.Settings, "query", out var query) ||
            string.IsNullOrWhiteSpace(query))
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "query is required"));
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(10);
        return SqlQuerySensorSupport.RunQueryAsync(
            new NpgsqlConnection(BuildConnectionString(context)), query, context.Settings, timeout, cancellationToken);
    }

    private static string BuildConnectionString(SensorExecutionContext context)
    {
        if (MonitoringSettings.TryReadParameter(context.Settings, "postgres.connectionString", out var connectionString) &&
            !string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString.Trim();
        }

        var host = context.Target.Trim();
        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "postgres.port", out var configuredPort)
            ? configuredPort
            : 5432;
        if (SqlQuerySensorSupport.TryParseHostPort(host, out var parsedHost, out var parsedPort))
        {
            host = parsedHost;
            port = parsedPort;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = MonitoringSettings.TryReadParameter(context.Settings, "postgres.database", out var database) &&
                !string.IsNullOrWhiteSpace(database)
                    ? database.Trim()
                    : "postgres",
            Timeout = Math.Max(1, (int)Math.Ceiling((context.Settings.Timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds)),
            SslMode = MonitoringSettings.TryReadParameter(context.Settings, "postgres.sslMode", out var sslMode) &&
                Enum.TryParse<SslMode>(sslMode.Trim(), ignoreCase: true, out var parsedSslMode)
                    ? parsedSslMode
                    : SslMode.Prefer
        };

        if (MonitoringSettings.TryReadParameter(context.Settings, "postgres.username", out var username) &&
            !string.IsNullOrWhiteSpace(username))
        {
            builder.Username = username.Trim();
            builder.Password = MonitoringSettings.TryReadParameter(context.Settings, "postgres.password", out var password)
                ? password
                : string.Empty;
        }

        return builder.ConnectionString;
    }
}
