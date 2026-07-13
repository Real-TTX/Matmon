using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

namespace Matmon.Core.Domain;

/// <summary>Shared engine for the SQL-query sensors (MSSQL / PostgreSQL / MySQL): run a query on a provider's
/// <see cref="DbConnection"/> and turn numeric result columns into channels. Each executor only builds its
/// provider-specific connection; the query execution, channel extraction and threshold handling live here.</summary>
internal static class SqlQuerySensorSupport
{
    public static async ValueTask<SensorExecutionResult> RunQueryAsync(
        DbConnection connection,
        string query,
        MonitoringSettings settings,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();

        try
        {
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(timeoutCts.Token);
                using var command = connection.CreateCommand();
                command.CommandText = query;
                command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, timeoutCts.Token);
                var channels = await ReadChannelsAsync(reader, timeoutCts.Token);
                watch.Stop();

                if (channels.Count == 0)
                {
                    return SensorExecutionResult.Critical(watch.Elapsed, "query returned no numeric channels");
                }

                var defaultChannelKey = MonitoringSettings.TryReadParameter(settings, "defaultChannelKey", out var configuredDefaultChannelKey)
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
                return SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, $"SQL query timed out after {timeout.TotalSeconds:0.#} seconds");
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return SensorExecutionResult.Unknown("query cancelled");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static async Task<IReadOnlyList<SensorChannelValue>> ReadChannelsAsync(
        DbDataReader reader,
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
            // Long form: one channel per row via channel,value[,unit] columns.
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

            // Wide form: numeric columns of the first row become channels.
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

    private static int FindColumn(IReadOnlyList<DbColumn> schema, string name)
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
            case byte byteValue: result = byteValue; return true;
            case sbyte sbyteValue: result = sbyteValue; return true;
            case short shortValue: result = shortValue; return true;
            case ushort ushortValue: result = ushortValue; return true;
            case int intValue: result = intValue; return true;
            case uint uintValue: result = uintValue; return true;
            case long longValue: result = longValue; return true;
            case ulong ulongValue: result = ulongValue; return true;
            case float floatValue: result = floatValue; return true;
            case double doubleValue: result = doubleValue; return true;
            case decimal decimalValue: result = (double)decimalValue; return true;
            case bool boolValue: result = boolValue ? 1 : 0; return true;
            default:
                return double.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out result);
        }
    }

    /// <summary>Split a "host:port" target. Returns false (leaving the whole string as host) when there is no
    /// single trailing numeric port - so bare hosts / IPv6 without a port are passed through unchanged.</summary>
    public static bool TryParseHostPort(string target, out string host, out int port)
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
