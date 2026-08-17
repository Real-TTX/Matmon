using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Matmon.Core.Domain;

public sealed class SynologyHealthSensorExecutor : ISensorExecutor
{
    private const string SystemMibRoot = "1.3.6.1.4.1.6574.1";
    private const string DiskTableRoot = "1.3.6.1.4.1.6574.2.1.1";
    // The Synology RAID table is the STORAGE POOL (RAID group), not the volume. Volumes (/volume1, /volume2, …)
    // are filesystems and live in the standard HOST-RESOURCES-MIB hrStorageTable that DSM's net-snmp exposes.
    // Free space per volume must come from there - reading only the RAID table reported the pool's free space.
    private const string RaidTableRoot = "1.3.6.1.4.1.6574.3.1.1";
    private const string HrStorageRoot = "1.3.6.1.2.1.25.2.3.1";
    // CPU and memory load are not in the Synology MIB - they come from the standard UCD-SNMP-MIB
    // that DSM's net-snmp exposes: ssCpu* under 2021.11, memory (KB) under 2021.4.
    private const string CpuMibRoot = "1.3.6.1.4.1.2021.11";
    private const string MemoryMibRoot = "1.3.6.1.4.1.2021.4";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "synology-health",
        DisplayName = "Synology Health",
        Description = "Checks Synology health, disk, RAID, CPU and memory metrics via SNMP.",
        // Dynamic: the storage channel set grows with the number of volumes and storage pools on the NAS.
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = BuildParameters()
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!context.SnmpResponded)
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var looksLikeSynology =
            (!string.IsNullOrWhiteSpace(context.SnmpSummary) &&
                (context.SnmpSummary.Contains("synology", StringComparison.OrdinalIgnoreCase) ||
                 context.SnmpSummary.Contains("diskstation", StringComparison.OrdinalIgnoreCase) ||
                 context.SnmpSummary.Contains("dsm", StringComparison.OrdinalIgnoreCase))) ||
            context.OpenTcpPorts.Contains(5000) ||
            context.OpenTcpPorts.Contains(5001);

        if (!looksLikeSynology)
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["snmp.community"] = string.IsNullOrWhiteSpace(context.SnmpCommunity)
            ? "public"
            : context.SnmpCommunity.Trim();
        settings.Parameters["snmp.version"] = string.IsNullOrWhiteSpace(context.SnmpVersion)
            ? "v2c"
            : context.SnmpVersion.Trim();
        settings.Parameters["snmp.port"] = (context.SnmpPort is >= 1 and <= 65535 ? context.SnmpPort : 161)
            .ToString(CultureInfo.InvariantCulture);

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Synology Health",
                    string.Empty,
                    settings,
                    "SNMP answered and Synology ports or system description were detected.",
                    92)));
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
            var systemItems = await SnmpSensorExecutor.DiscoverAsync(
                context.Target,
                context.Settings,
                SystemMibRoot,
                timeout,
                cancellationToken);

            var diskItems = await SnmpSensorExecutor.DiscoverAsync(
                context.Target,
                context.Settings,
                DiskTableRoot,
                timeout,
                cancellationToken);

            var raidItems = await SnmpSensorExecutor.DiscoverAsync(
                context.Target,
                context.Settings,
                RaidTableRoot,
                timeout,
                cancellationToken);

            var hrStorageItems = await SnmpSensorExecutor.DiscoverAsync(
                context.Target,
                context.Settings,
                HrStorageRoot,
                timeout,
                cancellationToken);

            var cpuItems = await SnmpSensorExecutor.DiscoverAsync(
                context.Target,
                context.Settings,
                CpuMibRoot,
                timeout,
                cancellationToken);

            var memoryItems = await SnmpSensorExecutor.DiscoverAsync(
                context.Target,
                context.Settings,
                MemoryMibRoot,
                timeout,
                cancellationToken);

            if (systemItems.Count == 0 && diskItems.Count == 0 && raidItems.Count == 0)
            {
                watch.Stop();
                return SensorExecutionResult.Critical(watch.Elapsed, "Synology SNMP MIB returned no values");
            }

            var system = ParseSystemSnapshot(systemItems, cpuItems, memoryItems);
            var disks = ParseDiskSnapshots(diskItems);
            var raids = ParseRaidSnapshots(raidItems);
            var volumes = ParseVolumeSnapshots(hrStorageItems);
            var primaryRaid = SelectPrimaryRaid(raids);
            var channels = BuildChannels(system, disks, raids, volumes, primaryRaid);
            var issues = BuildIssues(system, disks, raids);
            var state = DetermineState(system, disks, raids);
            var modelPrefix = string.IsNullOrWhiteSpace(system.ModelName) ? string.Empty : $"{system.ModelName.Trim()} - ";
            var message = BuildMessage(modelPrefix, state, system, disks, raids, volumes, primaryRaid, issues);
            var defaultChannel = channels.FirstOrDefault(channel => channel.Key.Equals("cpuUtilization", StringComparison.OrdinalIgnoreCase))
                ?? channels.FirstOrDefault(channel => channel.Value.HasValue)
                ?? channels.First();

            var result = state switch
            {
                SensorState.Critical => SensorExecutionResult.Critical(
                    watch.Elapsed,
                    message,
                    defaultChannel.Value,
                    defaultChannel.Key,
                    MarkDefault(channels, defaultChannel.Key)),
                SensorState.Warning => SensorExecutionResult.Warning(
                    watch.Elapsed,
                    message,
                    defaultChannel.Value,
                    defaultChannel.Key,
                    MarkDefault(channels, defaultChannel.Key)),
                _ => SensorExecutionResult.Healthy(
                    watch.Elapsed,
                    message,
                    defaultChannel.Value,
                    defaultChannel.Key,
                    MarkDefault(channels, defaultChannel.Key))
            };

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
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

    private static IReadOnlyList<SensorParameterDefinition> BuildParameters()
    {
        return SnmpSensorExecutor.Definition.Parameters
            .Where(parameter => !string.Equals(parameter.Key, "snmp.oids", StringComparison.OrdinalIgnoreCase))
            .Select(CloneParameter)
            .ToArray();
    }

    private static SensorParameterDefinition CloneParameter(SensorParameterDefinition parameter)
    {
        return new SensorParameterDefinition
        {
            Key = parameter.Key,
            Label = parameter.Label,
            Kind = parameter.Kind,
            Description = parameter.Description,
            Required = parameter.Required,
            DefaultValue = parameter.DefaultValue,
            Placeholder = parameter.Placeholder,
            Min = parameter.Min,
            Max = parameter.Max,
            Step = parameter.Step,
            Options = parameter.Options
                .Select(option => new SensorParameterOption
                {
                    Value = option.Value,
                    Label = option.Label
                })
                .ToArray(),
            CredentialKind = parameter.CredentialKind,
            VisibleWhenParameterKey = parameter.VisibleWhenParameterKey,
            VisibleWhenValues = parameter.VisibleWhenValues.ToArray()
        };
    }

    private static SynologySystemSnapshot ParseSystemSnapshot(
        IReadOnlyList<SnmpDiscoveryItem> items,
        IReadOnlyList<SnmpDiscoveryItem> cpuItems,
        IReadOnlyList<SnmpDiscoveryItem> memoryItems)
    {
        var values = BuildSuffixValueMap(items, SystemMibRoot);
        return new SynologySystemSnapshot(
            ComputeCpuUtilization(BuildSuffixValueMap(cpuItems, CpuMibRoot)),
            ComputeMemoryUtilization(BuildSuffixValueMap(memoryItems, MemoryMibRoot)),
            ReadNumeric(values, "2"),
            ReadStatusOk(values, "1", 1),
            ReadStatusOk(values, "3", 1),
            ReadStatusOk(values, "4.1", 1),
            ReadStatusOk(values, "4.2", 1),
            ReadStatusOk(values, "8", 1),
            ReadText(values, "5.1"),
            ReadText(values, "5.3"));
    }

    /// <summary>UCD-SNMP ssCpuIdle (2021.11.11.0) → 100 − idle; falls back to user+system.</summary>
    private static double? ComputeCpuUtilization(Dictionary<string, SnmpDiscoveryItem> cpu)
    {
        var idle = ReadNumeric(cpu, "11.0");
        if (idle.HasValue)
        {
            return Math.Clamp(100.0 - idle.Value, 0, 100);
        }

        var user = ReadNumeric(cpu, "9.0");
        var system = ReadNumeric(cpu, "10.0");
        if (user.HasValue || system.HasValue)
        {
            return Math.Clamp((user ?? 0) + (system ?? 0), 0, 100);
        }

        return null;
    }

    /// <summary>UCD-SNMP memory (KB): used = total − avail − buffers − cached, as a % of total.</summary>
    private static double? ComputeMemoryUtilization(Dictionary<string, SnmpDiscoveryItem> memory)
    {
        var total = ReadNumeric(memory, "5.0");
        var avail = ReadNumeric(memory, "6.0");
        if (!total.HasValue || total.Value <= 0 || !avail.HasValue)
        {
            return null;
        }

        var buffer = ReadNumeric(memory, "14.0") ?? 0;
        var cached = ReadNumeric(memory, "15.0") ?? 0;
        var used = total.Value - avail.Value - buffer - cached;
        return Math.Clamp(used / total.Value * 100.0, 0, 100);
    }

    private static IReadOnlyList<SynologyDiskSnapshot> ParseDiskSnapshots(IReadOnlyList<SnmpDiscoveryItem> items)
    {
        var rows = BuildTableRows(items, DiskTableRoot);
        return rows
            .OrderBy(row => row.Key)
            .Select(row =>
            {
                var columns = row.Value;
                // Two DIFFERENT enums, do not conflate them: diskHealthStatus (col 13, DSM 7+) is 1 ok / 2 warn /
                // 3-4 critical, while diskStatus (col 5) is 1-2 ok / 3 warn / 4-5 critical. Falling back col 13 to
                // col 5 graded a healthy "Initialized" disk (diskStatus 2) as a health warning - a false alarm.
                return new SynologyDiskSnapshot(
                    row.Key,
                    ReadNumeric(columns, 13),
                    ReadNumeric(columns, 5));
            })
            .ToArray();
    }

    /// <summary>0 healthy / 1 warning / 2 critical for one disk. diskStatus 1-2 are both healthy (2 =
    /// "Initialized"), 3 = not initialized (warning), 4-5 = failed/crashed; diskHealthStatus 2 = warning,
    /// 3-4 = critical. A missing health column (older DSM) is treated as OK, not as a warning. Mirrors the
    /// detailed synology-disk sensor so the two never disagree.</summary>
    private static int DiskSeverity(SynologyDiskSnapshot disk)
    {
        var status = disk.StatusCode.HasValue ? (int)Math.Round(disk.StatusCode.Value) : 1;
        var health = disk.HealthCode.HasValue ? (int)Math.Round(disk.HealthCode.Value) : 0;

        if (status is 4 or 5 || health is 3 or 4)
        {
            return 2;
        }

        if (status == 3 || health == 2)
        {
            return 1;
        }

        return 0;
    }

    /// <summary>Test seam: 0 healthy / 1 warning / 2 critical from the raw diskStatus + diskHealthStatus codes.</summary>
    public static int ClassifyDisk(double? diskStatus, double? diskHealthStatus) =>
        DiskSeverity(new SynologyDiskSnapshot(0, diskHealthStatus, diskStatus));

    private static IReadOnlyList<SynologyRaidSnapshot> ParseRaidSnapshots(IReadOnlyList<SnmpDiscoveryItem> items)
    {
        var rows = BuildTableRows(items, RaidTableRoot);
        return rows
            .OrderBy(row => row.Key)
            .Select(row =>
            {
                var columns = row.Value;
                return new SynologyRaidSnapshot(
                    row.Key,
                    ReadNumeric(columns, 3),
                    ReadNumeric(columns, 7),
                    ReadNumeric(columns, 4),
                    ReadNumeric(columns, 5),
                    ReadTextColumn(columns, 2));
            })
            .ToArray();
    }

    // hrStorageDescr for a Synology volume is "/volume1", "/volume2", … - only these numbered internal volumes
    // are treated as volumes (USB / system mounts are ignored).
    private static readonly Regex VolumeDescrPattern = new(@"^/volume(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Reads volumes from the HOST-RESOURCES-MIB hrStorageTable: total = size × allocationUnits,
    /// free = (size − used) × allocationUnits. (SNMP reports these as 32-bit counters, so a very large volume can
    /// wrap at the agent - the same limitation the Synology RAID counters have; we surface what the agent reports.)
    /// Public for unit testing without a live NAS.
    /// </summary>
    public static IReadOnlyList<SynologyVolumeSnapshot> ParseVolumeSnapshots(IReadOnlyList<SnmpDiscoveryItem> items)
    {
        var rows = BuildTableRows(items, HrStorageRoot);
        var volumes = new List<SynologyVolumeSnapshot>();

        foreach (var row in rows)
        {
            var columns = row.Value;
            var descr = ReadTextColumn(columns, 3);
            if (string.IsNullOrWhiteSpace(descr))
            {
                continue;
            }

            var match = VolumeDescrPattern.Match(descr.Trim());
            if (!match.Success)
            {
                continue;
            }

            var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = ReadNumeric(columns, 4);   // hrStorageAllocationUnits (bytes per unit)
            var size = ReadNumeric(columns, 5);    // hrStorageSize (in allocation units)
            var used = ReadNumeric(columns, 6);    // hrStorageUsed (in allocation units)
            if (!unit.HasValue || unit.Value <= 0 || !size.HasValue)
            {
                continue;
            }

            var totalBytes = size.Value * unit.Value;
            double? freeBytes = used.HasValue ? Math.Max(0d, (size.Value - used.Value) * unit.Value) : null;
            volumes.Add(new SynologyVolumeSnapshot(number, $"Volume {number}", freeBytes, totalBytes));
        }

        return volumes.OrderBy(volume => volume.Index).ToArray();
    }

    /// <summary>Volume 1 is the DSM default; fall back to the lowest-numbered volume that is present.</summary>
    public static SynologyVolumeSnapshot? SelectPrimaryVolume(IReadOnlyList<SynologyVolumeSnapshot> volumes)
    {
        return volumes.OrderBy(volume => volume.Index).FirstOrDefault();
    }

    private static string? ReadTextColumn(Dictionary<int, SnmpDiscoveryItem> columns, int column)
    {
        return columns.TryGetValue(column, out var item) ? item.Value : null;
    }

    private static Dictionary<int, Dictionary<int, SnmpDiscoveryItem>> BuildTableRows(
        IReadOnlyList<SnmpDiscoveryItem> items,
        string rootOid)
    {
        var rows = new Dictionary<int, Dictionary<int, SnmpDiscoveryItem>>();

        foreach (var item in items)
        {
            var suffix = GetSuffix(item.Oid, rootOid);
            if (string.IsNullOrWhiteSpace(suffix))
            {
                continue;
            }

            var parts = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var column) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowIndex))
            {
                continue;
            }

            if (!rows.TryGetValue(rowIndex, out var columns))
            {
                columns = new Dictionary<int, SnmpDiscoveryItem>();
                rows[rowIndex] = columns;
            }

            columns[column] = item;
        }

        return rows;
    }

    private static Dictionary<string, SnmpDiscoveryItem> BuildSuffixValueMap(
        IReadOnlyList<SnmpDiscoveryItem> items,
        string rootOid)
    {
        var values = new Dictionary<string, SnmpDiscoveryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var suffix = GetSuffix(item.Oid, rootOid);
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                values[suffix] = item;
            }
        }

        return values;
    }

    private static string GetSuffix(string oid, string rootOid)
    {
        var normalizedOid = NormalizeOid(oid);
        var normalizedRoot = NormalizeOid(rootOid);
        if (!normalizedOid.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var suffix = normalizedOid[normalizedRoot.Length..].TrimStart('.');
        return suffix;
    }

    private static string NormalizeOid(string oid)
    {
        return oid.Trim().Trim('.');
    }

    private static double? ReadNumeric(Dictionary<string, SnmpDiscoveryItem> values, string key)
    {
        if (!values.TryGetValue(key, out var item))
        {
            return null;
        }

        return ReadNumeric(item);
    }

    private static double? ReadNumeric(Dictionary<int, SnmpDiscoveryItem> columns, int column)
    {
        if (!columns.TryGetValue(column, out var item))
        {
            return null;
        }

        return ReadNumeric(item);
    }

    private static double? ReadNumeric(SnmpDiscoveryItem item)
    {
        if (double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    private static string? ReadText(Dictionary<string, SnmpDiscoveryItem> values, string key)
    {
        return values.TryGetValue(key, out var item)
            ? item.Value
            : null;
    }

    private static bool? ReadStatusOk(Dictionary<string, SnmpDiscoveryItem> values, string key, double normalValue)
    {
        var value = ReadNumeric(values, key);
        if (!value.HasValue)
        {
            return null;
        }

        return Math.Abs(value.Value - normalValue) < 0.001d;
    }

    private static IReadOnlyList<SensorChannelValue> BuildChannels(
        SynologySystemSnapshot system,
        IReadOnlyList<SynologyDiskSnapshot> disks,
        IReadOnlyList<SynologyRaidSnapshot> raids,
        IReadOnlyList<SynologyVolumeSnapshot> volumes,
        SynologyRaidSnapshot? primaryRaid)
    {
        var diskTotal = disks.Count;
        var diskHealthy = disks.Count(disk => DiskSeverity(disk) == 0);
        var diskWarning = disks.Count(disk => DiskSeverity(disk) == 1);
        var diskCritical = disks.Count(disk => DiskSeverity(disk) == 2);
        var diskFailing = disks.Count(disk => disk.HealthCode is 4);

        var raidTotal = raids.Count;
        var raidHealthy = 0;
        var raidWarning = 0;
        var raidDegraded = 0;
        var raidCrashed = 0;

        foreach (var raid in raids)
        {
            var summary = raid.SummaryCode;
            var status = raid.StatusCode;
            if (IsRaidHealthy(summary, status))
            {
                raidHealthy++;
                continue;
            }

            if (IsRaidCrashed(summary, status))
            {
                raidCrashed++;
                continue;
            }

            if (IsRaidDegraded(summary, status))
            {
                raidDegraded++;
                continue;
            }

            raidWarning++;
        }

        // The default storage reading is Volume 1 (the first volume). Fall back to the primary storage pool only
        // when no volume is reported (older DSM / hrStorage missing), so the channel keeps working. Per-volume and
        // per-pool channels are appended below, so both a NAS's volumes AND its pools are visible and thresholdable.
        var primaryVolume = SelectPrimaryVolume(volumes);
        var storageFreeBytes = primaryVolume?.FreeBytes ?? primaryRaid?.FreeBytes;
        var storageTotalBytes = primaryVolume?.TotalBytes ?? primaryRaid?.TotalBytes;
        double? storageFreePercent = storageFreeBytes.HasValue && storageTotalBytes.HasValue && storageTotalBytes.Value > 0
            ? Math.Max(0d, Math.Min(100d, storageFreeBytes.Value / storageTotalBytes.Value * 100d))
            : (double?)null;
        double? storageUsedPercent = storageFreePercent.HasValue
            ? 100d - storageFreePercent.Value
            : (double?)null;
        var storageScopeLabel = primaryVolume?.DisplayName ?? "Storage";

        var channels = new List<SensorChannelValue>
        {
            new()
            {
                Key = "cpuUtilization",
                Label = "CPU",
                Value = system.CpuUtilization,
                Unit = "%"
            },
            new()
            {
                Key = "memoryUtilization",
                Label = "Memory",
                Value = system.MemoryUtilization,
                Unit = "%"
            },
            new()
            {
                Key = "temperature",
                Label = "Temperature",
                Value = system.Temperature,
                Unit = "C"
            },
            new()
            {
                Key = "systemStatusOk",
                Label = "System partition",
                Value = ToBinaryValue(system.SystemStatusOk),
                LogByDefault = false
            },
            new()
            {
                Key = "powerStatusOk",
                Label = "Power supply",
                Value = ToBinaryValue(system.PowerStatusOk),
                LogByDefault = false
            },
            new()
            {
                Key = "systemFanStatusOk",
                Label = "System fan",
                Value = ToBinaryValue(system.SystemFanStatusOk),
                LogByDefault = false
            },
            new()
            {
                Key = "cpuFanStatusOk",
                Label = "CPU fan",
                Value = ToBinaryValue(system.CpuFanStatusOk),
                LogByDefault = false
            },
            new()
            {
                Key = "thermalStatusOk",
                Label = "Thermal",
                Value = ToBinaryValue(system.ThermalStatusOk),
                LogByDefault = false
            },
            new()
            {
                Key = "diskCount",
                Label = "Disks",
                Value = diskTotal
            },
            new()
            {
                Key = "diskHealthyCount",
                Label = "Healthy disks",
                Value = diskHealthy
            },
            new()
            {
                Key = "diskWarningCount",
                Label = "Warning disks",
                Value = diskWarning
            },
            new()
            {
                Key = "diskCriticalCount",
                Label = "Critical disks",
                Value = diskCritical
            },
            new()
            {
                Key = "diskFailingCount",
                Label = "Failing disks",
                Value = diskFailing
            },
            new()
            {
                // Single 0=healthy / 1=warning / 2=critical SMART summary across all
                // disks + RAIDs (same convention as the other Health sensors). Status
                // flag - opt out of statistics logging by default.
                Key = "smartStatus",
                Label = "SMART status",
                Value = (diskCritical + diskFailing + raidCrashed) > 0
                    ? 2
                    : (diskWarning + raidWarning + raidDegraded) > 0
                        ? 1
                        : 0,
                LogByDefault = false
            },
            new()
            {
                Key = "raidCount",
                Label = "RAIDs",
                Value = raidTotal
            },
            new()
            {
                Key = "raidHealthyCount",
                Label = "Healthy RAIDs",
                Value = raidHealthy
            },
            new()
            {
                Key = "raidWarningCount",
                Label = "Warning RAIDs",
                Value = raidWarning
            },
            new()
            {
                Key = "raidDegradedCount",
                Label = "Degraded RAIDs",
                Value = raidDegraded
            },
            new()
            {
                Key = "raidCrashedCount",
                Label = "Crashed RAIDs",
                Value = raidCrashed
            },
            new()
            {
                Key = "storageFreePercent",
                Label = $"{storageScopeLabel} free",
                Value = storageFreePercent,
                Unit = "%"
            },
            new()
            {
                Key = "storageFreeBytes",
                Label = $"{storageScopeLabel} free bytes",
                Value = storageFreeBytes,
                Unit = "B"
            },
            new()
            {
                Key = "storageTotalBytes",
                Label = $"{storageScopeLabel} total",
                Value = storageTotalBytes,
                Unit = "B"
            },
            new()
            {
                Key = "storageUsedPercent",
                Label = $"{storageScopeLabel} used",
                Value = storageUsedPercent,
                Unit = "%"
            }
        };

        // Per-volume channels for the remaining volumes (Volume 1 is already the default storage* set above),
        // and per-pool channels for every storage pool. This is the "show both" the pool/volume split needs;
        // free % is logged, the byte totals opt out of statistics by default to keep telemetry lean.
        foreach (var volume in volumes)
        {
            if (primaryVolume is not null && volume.Index == primaryVolume.Index)
            {
                continue;
            }

            AppendStorageEntityChannels(channels, $"volume.{volume.Index}", volume.DisplayName, volume.FreeBytes, volume.TotalBytes);
        }

        for (var index = 0; index < raids.Count; index++)
        {
            var pool = raids[index];
            var poolLabel = string.IsNullOrWhiteSpace(pool.Name) ? $"Pool {index + 1}" : pool.Name!.Trim();
            AppendStorageEntityChannels(channels, $"pool.{pool.Index}", poolLabel, pool.FreeBytes, pool.TotalBytes);
        }

        return channels;
    }

    /// <summary>Appends free %, free bytes, total bytes and used % channels for one volume or pool.</summary>
    private static void AppendStorageEntityChannels(
        List<SensorChannelValue> channels,
        string keyPrefix,
        string label,
        double? freeBytes,
        double? totalBytes)
    {
        double? freePercent = freeBytes.HasValue && totalBytes.HasValue && totalBytes.Value > 0
            ? Math.Max(0d, Math.Min(100d, freeBytes.Value / totalBytes.Value * 100d))
            : null;
        double? usedPercent = freePercent.HasValue ? 100d - freePercent.Value : null;

        channels.Add(new SensorChannelValue { Key = $"{keyPrefix}.freePercent", Label = $"{label} free", Value = freePercent, Unit = "%" });
        channels.Add(new SensorChannelValue { Key = $"{keyPrefix}.freeBytes", Label = $"{label} free bytes", Value = freeBytes, Unit = "B", LogByDefault = false });
        channels.Add(new SensorChannelValue { Key = $"{keyPrefix}.totalBytes", Label = $"{label} total", Value = totalBytes, Unit = "B", LogByDefault = false });
        channels.Add(new SensorChannelValue { Key = $"{keyPrefix}.usedPercent", Label = $"{label} used", Value = usedPercent, Unit = "%", LogByDefault = false });
    }

    private static double? ToBinaryValue(bool? value)
    {
        return value.HasValue ? (value.Value ? 1d : 0d) : null;
    }

    private static SensorState DetermineState(
        SynologySystemSnapshot system,
        IReadOnlyList<SynologyDiskSnapshot> disks,
        IReadOnlyList<SynologyRaidSnapshot> raids)
    {
        if (system.SystemStatusOk == false ||
            system.PowerStatusOk == false ||
            system.SystemFanStatusOk == false ||
            system.CpuFanStatusOk == false ||
            system.ThermalStatusOk == false ||
            disks.Any(disk => DiskSeverity(disk) == 2) ||
            raids.Any(raid => IsRaidCrashed(raid.SummaryCode, raid.StatusCode)))
        {
            return SensorState.Critical;
        }

        if (disks.Any(disk => DiskSeverity(disk) == 1) ||
            raids.Any(raid => IsRaidDegraded(raid.SummaryCode, raid.StatusCode) || IsRaidWarning(raid.SummaryCode, raid.StatusCode)))
        {
            return SensorState.Warning;
        }

        return SensorState.Healthy;
    }

    private static IReadOnlyList<string> BuildIssues(
        SynologySystemSnapshot system,
        IReadOnlyList<SynologyDiskSnapshot> disks,
        IReadOnlyList<SynologyRaidSnapshot> raids)
    {
        var issues = new List<string>();

        if (system.SystemStatusOk == false)
        {
            issues.Add("system partition failed");
        }

        if (system.PowerStatusOk == false)
        {
            issues.Add("power supply failed");
        }

        if (system.SystemFanStatusOk == false)
        {
            issues.Add("system fan failed");
        }

        if (system.CpuFanStatusOk == false)
        {
            issues.Add("CPU fan failed");
        }

        if (system.ThermalStatusOk == false)
        {
            issues.Add("thermal status failed");
        }

        var failingDisks = disks.Count(disk => DiskSeverity(disk) == 2);
        if (failingDisks > 0)
        {
            issues.Add($"{failingDisks} failing disk{(failingDisks == 1 ? string.Empty : "s")}");
        }

        var warningDisks = disks.Count(disk => DiskSeverity(disk) == 1);
        if (warningDisks > 0)
        {
            issues.Add($"{warningDisks} warning disk{(warningDisks == 1 ? string.Empty : "s")}");
        }

        var crashedRaids = raids.Count(raid => IsRaidCrashed(raid.SummaryCode, raid.StatusCode));
        if (crashedRaids > 0)
        {
            issues.Add($"{crashedRaids} crashed raid{(crashedRaids == 1 ? string.Empty : "s")}");
        }

        var degradedRaids = raids.Count(raid => IsRaidDegraded(raid.SummaryCode, raid.StatusCode));
        if (degradedRaids > 0)
        {
            issues.Add($"{degradedRaids} degraded raid{(degradedRaids == 1 ? string.Empty : "s")}");
        }

        return issues;
    }

    private static string BuildMessage(
        string modelPrefix,
        SensorState state,
        SynologySystemSnapshot system,
        IReadOnlyList<SynologyDiskSnapshot> disks,
        IReadOnlyList<SynologyRaidSnapshot> raids,
        IReadOnlyList<SynologyVolumeSnapshot> volumes,
        SynologyRaidSnapshot? primaryRaid,
        IReadOnlyList<string> issues)
    {
        var cpuText = system.CpuUtilization.HasValue ? $"{system.CpuUtilization.Value:0.#}%" : "-";
        var memoryText = system.MemoryUtilization.HasValue ? $"{system.MemoryUtilization.Value:0.#}%" : "-";
        // Prefer the default volume (Volume 1) for the summary; fall back to the storage pool only when no volume
        // is reported.
        var primaryVolume = SelectPrimaryVolume(volumes);
        var storageFreeBytes = primaryVolume?.FreeBytes ?? primaryRaid?.FreeBytes;
        var storageTotalBytes = primaryVolume?.TotalBytes ?? primaryRaid?.TotalBytes;
        var storageScope = primaryVolume?.DisplayName ?? (primaryRaid is not null ? "pool" : "storage");
        var storageText = storageFreeBytes.HasValue && storageTotalBytes.HasValue && storageTotalBytes.Value > 0
            ? $"{storageScope} {(storageFreeBytes.Value / storageTotalBytes.Value * 100d):0.#}% free"
            : "storage n/a";

        var statusText = state switch
        {
            SensorState.Critical => "critical",
            SensorState.Warning => "warning",
            _ => "ok"
        };

        if (issues.Count > 0)
        {
            return $"{modelPrefix}health {statusText} - {string.Join(", ", issues)}";
        }

        return $"{modelPrefix}health ok - CPU {cpuText}, memory {memoryText}, {storageText}, {disks.Count} disks, {raids.Count} RAID entries";
    }

    private static bool IsRaidWarning(double? summaryCode, double? statusCode)
    {
        var code = summaryCode ?? statusCode;
        if (!code.HasValue)
        {
            return false;
        }

        var integerCode = (int)Math.Round(code.Value, MidpointRounding.AwayFromZero);
        return integerCode is 0 or 6 or 7 or 8 or 9 or 10 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21;
    }

    private static bool IsRaidHealthy(double? summaryCode, double? statusCode)
    {
        var code = summaryCode ?? statusCode;
        if (!code.HasValue)
        {
            return false;
        }

        var integerCode = (int)Math.Round(code.Value, MidpointRounding.AwayFromZero);
        return integerCode == 1;
    }

    private static bool IsRaidDegraded(double? summaryCode, double? statusCode)
    {
        var code = summaryCode ?? statusCode;
        if (!code.HasValue)
        {
            return false;
        }

        var integerCode = (int)Math.Round(code.Value, MidpointRounding.AwayFromZero);
        return integerCode is 4 or 11;
    }

    private static bool IsRaidCrashed(double? summaryCode, double? statusCode)
    {
        var code = summaryCode ?? statusCode;
        if (!code.HasValue)
        {
            return false;
        }

        var integerCode = (int)Math.Round(code.Value, MidpointRounding.AwayFromZero);
        return integerCode is 2 or 3 or 5 or 12;
    }

    private static SynologyRaidSnapshot? SelectPrimaryRaid(IReadOnlyList<SynologyRaidSnapshot> raids)
    {
        return raids
            .Where(raid => raid.TotalBytes.HasValue && raid.TotalBytes.Value > 0)
            .OrderByDescending(raid => raid.TotalBytes!.Value)
            .ThenByDescending(raid => raid.FreeBytes ?? 0d)
            .FirstOrDefault();
    }

    private static List<SensorChannelValue> MarkDefault(IReadOnlyList<SensorChannelValue> channels, string defaultChannelKey)
    {
        return channels
            .Select(channel => channel with
            {
                IsDefault = string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
    }

    private sealed record SynologySystemSnapshot(
        double? CpuUtilization,
        double? MemoryUtilization,
        double? Temperature,
        bool? SystemStatusOk,
        bool? PowerStatusOk,
        bool? SystemFanStatusOk,
        bool? CpuFanStatusOk,
        bool? ThermalStatusOk,
        string? ModelName,
        string? Version);

    private sealed record SynologyDiskSnapshot(
        int Index,
        double? HealthCode,
        double? StatusCode);

    private sealed record SynologyRaidSnapshot(
        int Index,
        double? StatusCode,
        double? SummaryCode,
        double? FreeBytes,
        double? TotalBytes,
        string? Name);

    public sealed record SynologyVolumeSnapshot(
        int Index,
        string DisplayName,
        double? FreeBytes,
        double? TotalBytes);
}
