using Microsoft.Data.SqlClient;

namespace Matmon.Core.Domain;

public sealed class MssqlSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "mssql",
        DisplayName = "MSSQL",
        Description = "Runs a SQL Server query and turns numeric result columns into channels.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "mssql.database",
                Label = "Database",
                Kind = SensorParameterKind.Text,
                Description = "Database name",
                DefaultValue = "master",
                Placeholder = "master"
            },
            new SensorParameterDefinition
            {
                Key = "mssql.username",
                Label = "SQL username",
                Kind = SensorParameterKind.Text,
                Description = "SQL login. Leave empty for integrated/default connection string auth.",
                CredentialKind = MonitoringCredentialKind.SqlServer,
                Placeholder = "monitor"
            },
            new SensorParameterDefinition
            {
                Key = "mssql.password",
                Label = "SQL password",
                Kind = SensorParameterKind.Secret,
                Description = "SQL login password",
                CredentialKind = MonitoringCredentialKind.SqlServer
            },
            new SensorParameterDefinition
            {
                Key = "mssql.port",
                Label = "Port",
                Kind = SensorParameterKind.Integer,
                Description = "SQL Server TCP port",
                DefaultValue = "1433",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "mssql.encrypt",
                Label = "Encrypt",
                Kind = SensorParameterKind.Boolean,
                Description = "Use encrypted SQL connection",
                DefaultValue = "true"
            },
            new SensorParameterDefinition
            {
                Key = "mssql.trustServerCertificate",
                Label = "Trust cert",
                Kind = SensorParameterKind.Boolean,
                Description = "Trust SQL Server certificate. Useful for internal/self-signed SQL servers.",
                DefaultValue = "true"
            },
            new SensorParameterDefinition
            {
                Key = "mssql.connectionString",
                Label = "Connection string",
                Kind = SensorParameterKind.Multiline,
                Description = "Optional full connection string. If set, it overrides host/database/user fields.",
                Placeholder = "Server=sql01;Database=master;User Id=monitor;Password=secret;Encrypt=True;TrustServerCertificate=True"
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

        if (!context.OpenTcpPorts.Contains(1433))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["mssql.database"] = "master";
        settings.Parameters["mssql.port"] = "1433";
        settings.Parameters["mssql.encrypt"] = "true";
        settings.Parameters["mssql.trustServerCertificate"] = "true";
        settings.Parameters["query"] = "SELECT 1 AS value";
        settings.Parameters["defaultChannelKey"] = "value";
        settings.DefaultChannelKey = "value";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "MSSQL Query",
                    string.Empty,
                    settings,
                    "SQL Server port 1433 is open. SQL credentials can be inherited from the parent.",
                    82)));
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target) &&
            !MonitoringSettings.TryReadParameter(context.Settings, "mssql.connectionString", out _))
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
            new SqlConnection(BuildConnectionString(context)), query, context.Settings, timeout, cancellationToken);
    }

    private static string BuildConnectionString(SensorExecutionContext context)
    {
        if (MonitoringSettings.TryReadParameter(context.Settings, "mssql.connectionString", out var connectionString) &&
            !string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString.Trim();
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = BuildDataSource(context),
            InitialCatalog = MonitoringSettings.TryReadParameter(context.Settings, "mssql.database", out var database) &&
                !string.IsNullOrWhiteSpace(database)
                    ? database.Trim()
                    : "master",
            ConnectTimeout = Math.Max(1, (int)Math.Ceiling((context.Settings.Timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds)),
            Encrypt = !MonitoringSettings.TryReadParameterBool(context.Settings, "mssql.encrypt", out var encrypt) || encrypt,
            TrustServerCertificate = !MonitoringSettings.TryReadParameterBool(context.Settings, "mssql.trustServerCertificate", out var trustServerCertificate) || trustServerCertificate
        };

        if (MonitoringSettings.TryReadParameter(context.Settings, "mssql.username", out var username) &&
            !string.IsNullOrWhiteSpace(username))
        {
            builder.UserID = username.Trim();
            builder.Password = MonitoringSettings.TryReadParameter(context.Settings, "mssql.password", out var password)
                ? password
                : string.Empty;
            builder.IntegratedSecurity = false;
        }
        else
        {
            builder.IntegratedSecurity = true;
        }

        return builder.ConnectionString;
    }

    private static string BuildDataSource(SensorExecutionContext context)
    {
        var target = context.Target.Trim();
        if (target.Contains(','))
        {
            return target;
        }

        if (SqlQuerySensorSupport.TryParseHostPort(target, out var host, out var parsedPort))
        {
            return $"{host},{parsedPort}";
        }

        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "mssql.port", out var configuredPort)
            ? configuredPort
            : 1433;
        return $"{target},{port}";
    }
}
