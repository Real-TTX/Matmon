using MySqlConnector;

namespace Matmon.Core.Domain;

/// <summary>Runs a MySQL / MariaDB query and turns numeric result columns into channels. Mirrors the MSSQL
/// sensor; query execution + channel extraction is shared via <see cref="SqlQuerySensorSupport"/>.</summary>
public sealed class MySqlSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "mysql",
        DisplayName = "MySQL / MariaDB",
        Description = "Runs a MySQL or MariaDB query and turns numeric result columns into channels.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "mysql.database",
                Label = "Database",
                Kind = SensorParameterKind.Text,
                Description = "Database (schema) name",
                Placeholder = "information_schema"
            },
            new SensorParameterDefinition
            {
                Key = "mysql.username",
                Label = "Username",
                Kind = SensorParameterKind.Text,
                Description = "MySQL login.",
                CredentialKind = MonitoringCredentialKind.MySql,
                Placeholder = "monitor"
            },
            new SensorParameterDefinition
            {
                Key = "mysql.password",
                Label = "Password",
                Kind = SensorParameterKind.Secret,
                Description = "MySQL login password",
                CredentialKind = MonitoringCredentialKind.MySql
            },
            new SensorParameterDefinition
            {
                Key = "mysql.port",
                Label = "Port",
                Kind = SensorParameterKind.Integer,
                Description = "MySQL TCP port",
                DefaultValue = "3306",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "mysql.sslMode",
                Label = "SSL mode",
                Kind = SensorParameterKind.ValueList,
                Description = "TLS negotiation with the server.",
                DefaultValue = "Preferred",
                Options =
                [
                    new SensorParameterOption { Value = "Preferred", Label = "Preferred" },
                    new SensorParameterOption { Value = "Required", Label = "Required" },
                    new SensorParameterOption { Value = "VerifyFull", Label = "Verify full" },
                    new SensorParameterOption { Value = "None", Label = "None" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "mysql.connectionString",
                Label = "Connection string",
                Kind = SensorParameterKind.Multiline,
                Description = "Optional full connection string. If set, it overrides host/database/user fields.",
                Placeholder = "Server=db01;Port=3306;Database=app;User Id=monitor;Password=secret;SslMode=Preferred"
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

        if (!context.OpenTcpPorts.Contains(3306))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["mysql.port"] = "3306";
        settings.Parameters["mysql.sslMode"] = "Preferred";
        settings.Parameters["query"] = "SELECT 1 AS value";
        settings.Parameters["defaultChannelKey"] = "value";
        settings.DefaultChannelKey = "value";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "MySQL Query",
                    string.Empty,
                    settings,
                    "MySQL port 3306 is open. Credentials can be inherited from the parent.",
                    82)));
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target) &&
            !MonitoringSettings.TryReadParameter(context.Settings, "mysql.connectionString", out _))
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
            new MySqlConnection(BuildConnectionString(context)), query, context.Settings, timeout, cancellationToken);
    }

    private static string BuildConnectionString(SensorExecutionContext context)
    {
        if (MonitoringSettings.TryReadParameter(context.Settings, "mysql.connectionString", out var connectionString) &&
            !string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString.Trim();
        }

        var host = context.Target.Trim();
        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "mysql.port", out var configuredPort)
            ? configuredPort
            : 3306;
        if (SqlQuerySensorSupport.TryParseHostPort(host, out var parsedHost, out var parsedPort))
        {
            host = parsedHost;
            port = parsedPort;
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)port,
            ConnectionTimeout = (uint)Math.Max(1, (int)Math.Ceiling((context.Settings.Timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds)),
            SslMode = MonitoringSettings.TryReadParameter(context.Settings, "mysql.sslMode", out var sslMode) &&
                Enum.TryParse<MySqlSslMode>(sslMode.Trim(), ignoreCase: true, out var parsedSslMode)
                    ? parsedSslMode
                    : MySqlSslMode.Preferred
        };

        if (MonitoringSettings.TryReadParameter(context.Settings, "mysql.database", out var database) &&
            !string.IsNullOrWhiteSpace(database))
        {
            builder.Database = database.Trim();
        }

        if (MonitoringSettings.TryReadParameter(context.Settings, "mysql.username", out var username) &&
            !string.IsNullOrWhiteSpace(username))
        {
            builder.UserID = username.Trim();
            builder.Password = MonitoringSettings.TryReadParameter(context.Settings, "mysql.password", out var password)
                ? password
                : string.Empty;
        }

        return builder.ConnectionString;
    }
}
