using System.Data;
using System.Diagnostics;
using System.Globalization;
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

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target) &&
            !MonitoringSettings.TryReadParameter(context.Settings, "mssql.connectionString", out _))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target or connection string is required");
        }

        if (!MonitoringSettings.TryReadParameter(context.Settings, "query", out var query) ||
            string.IsNullOrWhiteSpace(query))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "query is required");
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(10);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();

        try
        {
            await using var connection = new SqlConnection(BuildConnectionString(context));
            await connection.OpenAsync(timeoutCts.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = query;
            command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, timeoutCts.Token);
            var channels = await ReadChannelsAsync(reader, timeoutCts.Token);
            watch.Stop();

            if (channels.Count == 0)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "query returned no numeric channels");
            }

            var defaultChannelKey = MonitoringSettings.TryReadParameter(context.Settings, "defaultChannelKey", out var configuredDefaultChannelKey)
                ? configuredDefaultChannelKey.Trim()
                : string.Empty;
            var defaultChannel = SelectDefaultChannel(channels, defaultChannelKey);
            var markedChannels = channels
                .Select(channel => channel with
                {
                    IsDefault = string.Equals(channel.Key, defaultChannel.Key, StringComparison.OrdinalIgnoreCase)
                })
                .ToArray();

            var result = SensorExecutionResult.Healthy(
                watch.Elapsed,
                $"{markedChannels.Length} SQL channel{(markedChannels.Length == 1 ? string.Empty : "s")}",
                defaultChannel.Value,
                defaultChannel.Key,
                markedChannels);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, $"SQL query timed out after {timeout.TotalSeconds:0.#} seconds");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
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

        if (TryParseHostPort(target, out var host, out var parsedPort))
        {
            return $"{host},{parsedPort}";
        }

        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "mssql.port", out var configuredPort)
            ? configuredPort
            : 1433;
        return $"{target},{port}";
    }

    private static async Task<IReadOnlyList<SensorChannelValue>> ReadChannelsAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var schema = reader.GetColumnSchema();
        var channels = new List<SensorChannelValue>();
        var rowIndex = 0;

        var channelColumnIndex = FindColumn(schema, "channel");
        var valueColumnIndex = FindColumn(schema, "value");
        var unitColumnIndex = FindColumn(schema, "unit");

        while (await reader.ReadAsync(cancellationToken))
        {
            if (channelColumnIndex >= 0 && valueColumnIndex >= 0)
            {
                var channelName = Convert.ToString(reader.GetValue(channelColumnIndex), CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(channelName) &&
                    TryConvertDouble(reader.GetValue(valueColumnIndex), out var channelValue))
                {
                    var unit = unitColumnIndex >= 0
                        ? Convert.ToString(reader.GetValue(unitColumnIndex), CultureInfo.InvariantCulture)
                        : null;
                    channels.Add(new SensorChannelValue
                    {
                        Key = NormalizeChannelKey(channelName),
                        Label = channelName.Trim(),
                        Value = channelValue,
                        Unit = string.IsNullOrWhiteSpace(unit) ? null : unit
                    });
                }

                rowIndex++;
                continue;
            }

            if (rowIndex > 0)
            {
                break;
            }

            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = reader.GetValue(index);
                if (!TryConvertDouble(value, out var numericValue))
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(reader.GetName(index))
                    ? $"value{index + 1}"
                    : reader.GetName(index);
                channels.Add(new SensorChannelValue
                {
                    Key = NormalizeChannelKey(name),
                    Label = name,
                    Value = numericValue
                });
            }

            rowIndex++;
        }

        return channels
            .GroupBy(channel => channel.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
    }

    private static int FindColumn(IReadOnlyList<System.Data.Common.DbColumn> schema, string name)
    {
        for (var index = 0; index < schema.Count; index++)
        {
            if (string.Equals(schema[index].ColumnName, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static SensorChannelValue SelectDefaultChannel(
        IReadOnlyList<SensorChannelValue> channels,
        string defaultChannelKey)
    {
        if (!string.IsNullOrWhiteSpace(defaultChannelKey))
        {
            var configured = channels.FirstOrDefault(channel =>
                string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase));
            if (configured is not null)
            {
                return configured;
            }
        }

        return channels.First(channel => channel.Value.HasValue);
    }

    private static bool TryConvertDouble(object? value, out double result)
    {
        if (value is null or DBNull)
        {
            result = default;
            return false;
        }

        switch (value)
        {
            case byte byteValue:
                result = byteValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            case float floatValue:
                result = floatValue;
                return true;
            case double doubleValue:
                result = doubleValue;
                return true;
            case decimal decimalValue:
                result = (double)decimalValue;
                return true;
            case bool boolValue:
                result = boolValue ? 1 : 0;
                return true;
            default:
                return double.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out result);
        }
    }

    private static bool TryParseHostPort(string target, out string host, out int port)
    {
        var colonIndex = target.LastIndexOf(':');
        if (colonIndex > 0 &&
            colonIndex < target.Length - 1 &&
            target.Count(character => character == ':') == 1 &&
            int.TryParse(target[(colonIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
        {
            host = target[..colonIndex];
            return true;
        }

        host = target;
        port = default;
        return false;
    }

    private static string NormalizeChannelKey(string raw)
    {
        var chars = raw.Trim()
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '.')
            .ToArray();
        var key = string.Join('.', new string(chars)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(key) ? "value" : key;
    }
}
