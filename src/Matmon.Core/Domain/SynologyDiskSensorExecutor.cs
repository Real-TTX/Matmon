using System.Diagnostics;
using System.Globalization;

namespace Matmon.Core.Domain;

/// <summary>
/// Detailed per-disk health of a Synology DiskStation via SNMP (synoDisk table
/// <c>1.3.6.1.4.1.6574.2.1.1</c>). Where the compact <c>synology-health</c> sensor only
/// rolls up a single SMART summary, this one emits one temperature + one status channel
/// <em>per physical disk</em>, plus an overall SMART status and the hottest-disk temperature.
/// </summary>
public sealed class SynologyDiskSensorExecutor : ISensorExecutor
{
    private const string DiskTableRoot = "1.3.6.1.4.1.6574.2.1.1";

    // synoDisk columns we read.
    private const int ColName = 2;        // diskID, e.g. "Disk 1"
    private const int ColModel = 3;       // diskModel
    private const int ColStatus = 5;      // diskStatus: 1-3 not a fault (3 = NotInitialized/cache/spare), 4/5 critical
    private const int ColTemperature = 6; // diskTemperature (°C)
    private const int ColHealth = 13;     // diskHealthStatus (DSM 7+): 1 ok, 2 warn, 3/4 critical

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "synology-disk",
        DisplayName = "Synology Disk",
        Description = "Per-disk temperature, status and SMART health of a Synology DiskStation via SNMP.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = SynologyHealthSensorExecutor.Definition.Parameters
    };

    public string SensorTypeKey => Definition.Key;

    public async ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        // Reuse the Synology Health detection, but offer this as the detailed alternative
        // (lower priority so the compact health sensor stays the primary suggestion).
        var health = await new SynologyHealthSensorExecutor().DiscoverAsync(context, cancellationToken);
        if (!health.IsAvailable || health.Suggestions.Count == 0)
        {
            return SensorDiscoveryCheckResult.NotAvailable;
        }

        return SensorDiscoveryCheckResult.Available(
            new SensorDiscoverySuggestion(
                Definition.Key,
                "Synology Disk",
                string.Empty,
                health.Suggestions[0].Settings,
                "SNMP answered and Synology disks were detected.",
                70));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target is required");
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(10);
        var watch = Stopwatch.StartNew();

        try
        {
            var items = await SnmpSensorExecutor.DiscoverAsync(
                context.Target,
                context.Settings,
                DiskTableRoot,
                timeout,
                cancellationToken);

            var disks = ParseDisks(items);
            watch.Stop();

            if (disks.Count == 0)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "Synology SNMP disk table returned no disks");
            }

            var channels = BuildChannels(disks);
            var state = DetermineState(disks);
            var message = BuildMessage(state, disks);

            var defaultChannel = channels.FirstOrDefault(channel => channel.Key == "maxTemperature" && channel.Value.HasValue)
                ?? channels.FirstOrDefault(channel => channel.Value.HasValue)
                ?? channels[0];
            var marked = MarkDefault(channels, defaultChannel.Key);

            var result = state switch
            {
                SensorState.Critical => SensorExecutionResult.Critical(watch.Elapsed, message, defaultChannel.Value, defaultChannel.Key, marked),
                SensorState.Warning => SensorExecutionResult.Warning(watch.Elapsed, message, defaultChannel.Value, defaultChannel.Key, marked),
                _ => SensorExecutionResult.Healthy(watch.Elapsed, message, defaultChannel.Value, defaultChannel.Key, marked)
            };

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Unknown("execution cancelled");
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, $"execution timed out after {timeout.TotalSeconds:0.#} seconds");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static IReadOnlyList<DiskSnapshot> ParseDisks(IReadOnlyList<SnmpDiscoveryItem> items)
    {
        var rows = new Dictionary<int, Dictionary<int, string>>();
        foreach (var item in items)
        {
            var suffix = NormalizeOid(item.Oid);
            var root = NormalizeOid(DiskTableRoot);
            if (!suffix.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = suffix[root.Length..]
                .TrimStart('.')
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var column) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowIndex))
            {
                continue;
            }

            if (!rows.TryGetValue(rowIndex, out var columns))
            {
                columns = new Dictionary<int, string>();
                rows[rowIndex] = columns;
            }

            columns[column] = item.Value;
        }

        return rows
            .OrderBy(row => row.Key)
            .Select(row =>
            {
                var columns = row.Value;
                return new DiskSnapshot(
                    row.Key,
                    columns.GetValueOrDefault(ColName)?.Trim(),
                    columns.GetValueOrDefault(ColModel)?.Trim(),
                    ReadNumeric(columns, ColStatus),
                    ReadNumeric(columns, ColHealth),
                    ReadNumeric(columns, ColTemperature));
            })
            .ToArray();
    }

    private static IReadOnlyList<SensorChannelValue> BuildChannels(IReadOnlyList<DiskSnapshot> disks)
    {
        var channels = new List<SensorChannelValue>();

        var temperatures = disks.Where(disk => disk.Temperature.HasValue).Select(disk => disk.Temperature!.Value).ToArray();
        double? maxTemperature = temperatures.Length > 0 ? temperatures.Max() : null;

        var worst = disks.Select(DiskSeverity).DefaultIfEmpty(0).Max();

        channels.Add(new SensorChannelValue
        {
            Key = "maxTemperature",
            Label = "Hottest disk",
            Value = maxTemperature,
            Unit = "C",
            MeasurementKind = SensorMeasurementKind.Temperature
        });
        channels.Add(new SensorChannelValue
        {
            // 0 = all healthy, 1 = warning, 2 = critical - same convention as the Health sensors.
            Key = "smartStatus",
            Label = "SMART status",
            Value = worst,
            MeasurementKind = SensorMeasurementKind.Count,
            LogByDefault = false
        });
        channels.Add(new SensorChannelValue
        {
            Key = "diskCount",
            Label = "Disks",
            Value = disks.Count,
            MeasurementKind = SensorMeasurementKind.Count,
            LogByDefault = false
        });

        var ordinal = 0;
        foreach (var disk in disks)
        {
            ordinal++;
            var name = string.IsNullOrWhiteSpace(disk.Name) ? $"Disk {ordinal}" : disk.Name!;
            channels.Add(new SensorChannelValue
            {
                Key = $"disk{disk.Index}Temp",
                Label = $"{name} temp",
                Value = disk.Temperature,
                Unit = "C",
                MeasurementKind = SensorMeasurementKind.Temperature
            });
            channels.Add(new SensorChannelValue
            {
                Key = $"disk{disk.Index}Status",
                Label = name,
                Value = DiskSeverity(disk),
                MeasurementKind = SensorMeasurementKind.Count,
                LogByDefault = false
            });
        }

        return channels;
    }

    /// <summary>0 healthy / 1 warning / 2 critical for a single disk. Health comes from diskHealthStatus (SMART:
    /// 2 warn, 3-4 critical) plus a failed/crashed diskStatus (4-5). diskStatus 1-3 are all "not a fault":
    /// 1 Normal, 2 Initialized, 3 NotInitialized - a disk not assigned to a pool (new/unused, hot spare, or an
    /// SSD/NVMe cache device), which DSM does not warn about.</summary>
    private static int DiskSeverity(DiskSnapshot disk)
    {
        var status = disk.StatusCode.HasValue ? (int)Math.Round(disk.StatusCode.Value) : 1;
        var health = disk.HealthCode.HasValue ? (int)Math.Round(disk.HealthCode.Value) : 0;

        if (status is 4 or 5 || health is 3 or 4)
        {
            return 2;
        }

        if (health == 2)
        {
            return 1;
        }

        return 0;
    }

    private static SensorState DetermineState(IReadOnlyList<DiskSnapshot> disks)
    {
        var worst = disks.Select(DiskSeverity).DefaultIfEmpty(0).Max();
        return worst switch
        {
            2 => SensorState.Critical,
            1 => SensorState.Warning,
            _ => SensorState.Healthy
        };
    }

    private static string BuildMessage(SensorState state, IReadOnlyList<DiskSnapshot> disks)
    {
        var statusText = state switch
        {
            SensorState.Critical => "critical",
            SensorState.Warning => "warning",
            _ => "ok"
        };

        var failing = disks.Where(disk => DiskSeverity(disk) > 0)
            .Select(disk => string.IsNullOrWhiteSpace(disk.Name) ? $"disk {disk.Index}" : disk.Name!)
            .ToArray();
        var temperatures = disks.Where(disk => disk.Temperature.HasValue).Select(disk => disk.Temperature!.Value).ToArray();
        var tempText = temperatures.Length > 0 ? $", up to {temperatures.Max():0.#}°C" : string.Empty;

        if (failing.Length > 0)
        {
            return $"disks {statusText} - {string.Join(", ", failing)}{tempText}";
        }

        return $"disks ok - {disks.Count} healthy{tempText}";
    }

    private static double? ReadNumeric(Dictionary<int, string> columns, int column)
    {
        if (columns.TryGetValue(column, out var raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    private static string NormalizeOid(string oid) => oid.Trim().Trim('.');

    private static List<SensorChannelValue> MarkDefault(IReadOnlyList<SensorChannelValue> channels, string defaultChannelKey)
    {
        return channels
            .Select(channel => channel with
            {
                IsDefault = string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
    }

    private sealed record DiskSnapshot(
        int Index,
        string? Name,
        string? Model,
        double? StatusCode,
        double? HealthCode,
        double? Temperature);
}
