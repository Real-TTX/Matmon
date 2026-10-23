using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Matmon.Core.Domain;

/// <summary>
/// A network printer over SNMP through the standard Printer-MIB (RFC 3805) and the HOST-RESOURCES-MIB printer table -
/// what every laser and inkjet of the last twenty years serves, whatever the vendor: the supply levels (toner, ink, drum,
/// waste toner, fuser), the paper trays, the page counter, the printer's own error bits (jam, door open, no toner,
/// offline …) and its active alerts. One SNMP walk per table, nothing vendor-specific.
/// <para>The state is the printer's: an error bit that stops printing (no toner, jam, door open, offline, a supply
/// missing) or a critical alert is critical, the softer bits (low toner, low or no paper, service requested, output
/// full) and warning alerts are a warning. The supply levels are channels with a default on the lowest one.</para>
/// </summary>
public sealed class PrinterSensorExecutor : ISensorExecutor
{
    public const string LowestSupplyChannel = "supplyMinPercent";
    public const string PageCountChannel = "pageCount";
    public const string EmptyTraysChannel = "traysEmpty";

    private const string PrinterTable = "1.3.6.1.2.1.25.3.5.1";
    private const string DeviceDescr = "1.3.6.1.2.1.25.3.2.1.3";
    private const string SuppliesTable = "1.3.6.1.2.1.43.11.1.1";
    private const string InputMax = "1.3.6.1.2.1.43.8.2.1.9";
    private const string InputLevel = "1.3.6.1.2.1.43.8.2.1.10";
    private const string InputName = "1.3.6.1.2.1.43.8.2.1.13";
    private const string LifeCount = "1.3.6.1.2.1.43.10.2.1.4";
    private const string GeneralPrinterName = "1.3.6.1.2.1.43.5.1.1.16";
    private const string AlertSeverity = "1.3.6.1.2.1.43.18.1.1.2";
    private const string AlertDescription = "1.3.6.1.2.1.43.18.1.1.8";

    private static readonly string[] Walks = [PrinterTable, DeviceDescr, GeneralPrinterName, SuppliesTable, InputMax, InputLevel, InputName, LifeCount, AlertSeverity, AlertDescription];

    /// <summary>hrPrinterDetectedErrorState, bit 0 = the most significant bit of the first octet (RFC 2790).</summary>
    private static readonly (int Bit, string Name, SensorState State)[] ErrorBits =
    [
        (0, "low paper", SensorState.Warning),
        (1, "no paper", SensorState.Warning),
        (2, "low toner", SensorState.Warning),
        (3, "no toner", SensorState.Critical),
        (4, "door open", SensorState.Critical),
        (5, "paper jam", SensorState.Critical),
        (6, "offline", SensorState.Critical),
        (7, "service requested", SensorState.Warning),
        (8, "input tray missing", SensorState.Warning),
        (9, "output tray missing", SensorState.Warning),
        (10, "supply missing", SensorState.Critical),
        (11, "output nearly full", SensorState.Warning),
        (12, "output full", SensorState.Warning),
        (13, "input tray empty", SensorState.Warning),
        (14, "maintenance overdue", SensorState.Warning)
    ];

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "snmp-printer",
        DisplayName = "Printer (SNMP)",
        Description = "A network printer through the standard Printer-MIB: toner / ink / drum / waste toner levels, paper trays, page counter, the printer's error bits (jam, door open, no toner, offline …) and its alerts. Any vendor.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = SnmpSensorExecutor.Definition.Parameters.Where(parameter => parameter.Key != "snmp.oids").Select(parameter => parameter with { }).ToArray()
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        // A printer says so in its sysDescr often enough, and port 9100 (raw printing) is next to unmistakable.
        var printerish = context.OpenTcpPorts.Contains(9100)
            || (context.SnmpSummary ?? string.Empty).Contains("printer", StringComparison.OrdinalIgnoreCase)
            || (context.SnmpSummary ?? string.Empty).Contains("laserjet", StringComparison.OrdinalIgnoreCase);
        if (!context.SnmpResponded || !printerish)
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["snmp.community"] = context.SnmpCommunity;
        settings.Parameters["snmp.version"] = context.SnmpVersion;
        settings.Parameters["snmp.port"] = context.SnmpPort.ToString(CultureInfo.InvariantCulture);
        return ValueTask.FromResult(SensorDiscoveryCheckResult.Available(
            new SensorDiscoverySuggestion(Definition.Key, "Printer", context.Host, settings,
                "The device answers SNMP and looks like a printer (port 9100 or its description).", 82)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        var target = context.Target?.Trim();
        if (string.IsNullOrEmpty(target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "The printer's address is missing (the host's address, or a target).");
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(5);
        var watch = Stopwatch.StartNew();
        var items = new List<SnmpDiscoveryItem>();
        try
        {
            foreach (var root in Walks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.AddRange(await SnmpSensorExecutor.DiscoverAsync(target, context.Settings, root, timeout, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (items.Count == 0)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The printer at {target} does not answer SNMP: {exception.Message}");
        }
        catch (Exception exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The printer at {target} stopped answering in the middle of a read: {exception.Message}");
        }

        watch.Stop();
        var reading = Parse(items);
        return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, BuildResult(reading, watch.Elapsed));
    }

    // ---- parsing (pure) --------------------------------------------------------------------------------------------------

    public sealed record PrinterSupply(string Name, int Class, double? Percent, string Index);

    public sealed record PrinterTray(string Name, double? Percent, bool Empty);

    public sealed record PrinterAlert(int Severity, string Description);

    public sealed record PrinterReading(
        string? Model,
        int? Status,
        IReadOnlyList<string> ErrorNames,
        SensorState ErrorState,
        IReadOnlyList<PrinterSupply> Supplies,
        IReadOnlyList<PrinterTray> Trays,
        double? PageCount,
        IReadOnlyList<PrinterAlert> Alerts,
        bool HasPrinterMib);

    /// <summary>Turns the walked OIDs into a reading. A level of -3 means "some left" (a third-party cartridge often),
    /// -1/-2 "not known"; neither becomes a percentage.</summary>
    public static PrinterReading Parse(IReadOnlyList<SnmpDiscoveryItem> items)
    {
        var byOid = new Dictionary<string, SnmpDiscoveryItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            byOid[item.Oid.TrimStart('.')] = item;
        }

        IEnumerable<(string Index, SnmpDiscoveryItem Item)> Column(string column) =>
            byOid.Where(pair => pair.Key.StartsWith(column + ".", StringComparison.Ordinal))
                .Select(pair => (pair.Key[(column.Length + 1)..], pair.Value))
                .OrderBy(pair => pair.Item1, OidIndexComparer.Instance);

        double? Number(string oid) => byOid.TryGetValue(oid, out var item) && double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        string? Text(string oid) => byOid.TryGetValue(oid, out var item) && Clean(item) is { Length: > 0 } text ? text : null;

        // The printer's own row in the HOST-RESOURCES printer table (usually index 1).
        var printer = Column(PrinterTable + ".1").FirstOrDefault();
        int? status = printer.Item is { } statusItem && int.TryParse(statusItem.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStatus) ? parsedStatus : null;
        var errorNames = new List<string>();
        var errorState = SensorState.Healthy;
        if (printer.Index is { } deviceIndex && byOid.TryGetValue(PrinterTable + ".2." + deviceIndex, out var errorItem))
        {
            var bits = ErrorBytes(errorItem);
            foreach (var (bit, name, state) in ErrorBits)
            {
                if (bit / 8 < bits.Length && (bits[bit / 8] & (0x80 >> (bit % 8))) != 0)
                {
                    errorNames.Add(name);
                    errorState = Worse(errorState, state);
                }
            }
        }

        var model = printer.Index is { } index ? Text(DeviceDescr + "." + index) : null;
        model ??= Column(DeviceDescr).Select(pair => Clean(pair.Item)).FirstOrDefault(text => text.Contains("print", StringComparison.OrdinalIgnoreCase));
        // prtGeneralPrinterName (RFC 3805) - where the device table does not name the printer.
        model ??= Column(GeneralPrinterName).Select(pair => Clean(pair.Item)).FirstOrDefault(text => text.Length > 0);

        var supplies = new List<PrinterSupply>();
        foreach (var (supplyIndex, descriptionItem) in Column(SuppliesTable + ".6"))
        {
            var level = Number(SuppliesTable + ".9." + supplyIndex);
            var max = Number(SuppliesTable + ".8." + supplyIndex);
            var supplyClass = (int)(Number(SuppliesTable + ".4." + supplyIndex) ?? 3);
            double? percent = level is >= 0 && max is > 0 ? Math.Clamp(Math.Round(level.Value / max.Value * 100, 1), 0, 100) : null;
            var name = Clean(descriptionItem);
            supplies.Add(new PrinterSupply(name.Length == 0 ? "Supply " + supplyIndex : name, supplyClass, percent, supplyIndex));
        }

        var trays = new List<PrinterTray>();
        foreach (var (trayIndex, levelItem) in Column(InputLevel))
        {
            if (!double.TryParse(levelItem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var level))
            {
                continue;
            }

            var max = Number(InputMax + "." + trayIndex);
            var name = Text(InputName + "." + trayIndex) ?? "Tray " + trayIndex.Split('.')[^1];
            trays.Add(new PrinterTray(name, level >= 0 && max is > 0 ? Math.Clamp(Math.Round(level / max.Value * 100, 1), 0, 100) : null, level == 0));
        }

        var pageCount = Column(LifeCount).Select(pair => double.TryParse(pair.Item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (double?)null)
            .FirstOrDefault(value => value is not null);

        var alerts = new List<PrinterAlert>();
        foreach (var (alertIndex, severityItem) in Column(AlertSeverity))
        {
            if (int.TryParse(severityItem.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var severity))
            {
                alerts.Add(new PrinterAlert(severity, Text(AlertDescription + "." + alertIndex) ?? "Alert " + alertIndex));
            }
        }

        var hasPrinterMib = byOid.Keys.Any(oid => oid.StartsWith("1.3.6.1.2.1.43.", StringComparison.Ordinal)) || printer.Item is not null;
        return new PrinterReading(model, status, errorNames, errorState, supplies, trays, pageCount, alerts, hasPrinterMib);
    }

    // ---- the result (pure) -----------------------------------------------------------------------------------------------

    public static SensorExecutionResult BuildResult(PrinterReading reading, TimeSpan duration)
    {
        if (!reading.HasPrinterMib)
        {
            return SensorExecutionResult.Critical(duration, "The device answers SNMP but serves no Printer-MIB - is this a printer, and is the community / SNMP user the right one?");
        }

        var channels = new List<SensorChannelValue>();
        var known = reading.Supplies.Where(supply => supply.Percent is not null).ToList();
        if (known.Count > 0)
        {
            channels.Add(new SensorChannelValue
            {
                Key = LowestSupplyChannel, Label = "Lowest supply", Value = known.Min(supply => supply.Percent), Unit = "%", MeasurementKind = SensorMeasurementKind.Percent
            });
        }

        if (reading.PageCount is { } pages)
        {
            channels.Add(new SensorChannelValue { Key = PageCountChannel, Label = "Pages printed", Value = pages, MeasurementKind = SensorMeasurementKind.Count });
        }

        if (reading.Trays.Count > 0)
        {
            channels.Add(new SensorChannelValue
            {
                Key = EmptyTraysChannel, Label = "Empty paper trays", Value = reading.Trays.Count(tray => tray.Empty), MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false
            });
        }

        var criticalAlerts = reading.Alerts.Count(alert => alert.Severity == 3);
        var warningAlerts = reading.Alerts.Count(alert => alert.Severity is 4 or 5);
        channels.Add(new SensorChannelValue { Key = "criticalAlerts", Label = "Critical alerts", Value = criticalAlerts, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false });
        channels.Add(new SensorChannelValue { Key = "warningAlerts", Label = "Warning alerts", Value = warningAlerts, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false });

        var used = new HashSet<string>(channels.Select(channel => channel.Key), StringComparer.Ordinal);
        foreach (var supply in known)
        {
            channels.Add(new SensorChannelValue
            {
                Key = UniqueKey("supply." + RedfishSensorExecutor.Slug(supply.Name), used),
                Label = supply.Class == 4 ? $"{supply.Name} (space left)" : supply.Name,
                Value = supply.Percent,
                Unit = "%",
                MeasurementKind = SensorMeasurementKind.Percent
            });
        }

        foreach (var tray in reading.Trays.Where(tray => tray.Percent is not null))
        {
            channels.Add(new SensorChannelValue
            {
                Key = UniqueKey("tray." + RedfishSensorExecutor.Slug(tray.Name), used), Label = tray.Name, Value = tray.Percent, Unit = "%", MeasurementKind = SensorMeasurementKind.Percent, LogByDefault = false
            });
        }

        var defaultKey = channels.Any(channel => channel.Key == LowestSupplyChannel) ? LowestSupplyChannel
            : channels.Any(channel => channel.Key == PageCountChannel) ? PageCountChannel
            : channels[0].Key;
        channels = channels.Select(channel => channel with { IsDefault = channel.Key == defaultKey }).ToList();
        var value = channels.First(channel => channel.IsDefault).Value;

        var state = reading.ErrorState;
        if (criticalAlerts > 0)
        {
            state = SensorState.Critical;
        }
        else if (warningAlerts > 0)
        {
            state = Worse(state, SensorState.Warning);
        }

        var message = Describe(reading, known);
        return state switch
        {
            SensorState.Critical => SensorExecutionResult.Critical(duration, message, value, defaultKey, channels),
            SensorState.Warning => SensorExecutionResult.Warning(duration, message, value, defaultKey, channels),
            _ => SensorExecutionResult.Healthy(duration, message, value, defaultKey, channels)
        };
    }

    private static string Describe(PrinterReading reading, List<PrinterSupply> known)
    {
        var builder = new StringBuilder(string.IsNullOrWhiteSpace(reading.Model) ? "Printer" : reading.Model.Trim());
        builder.Append(": ").Append(reading.Status switch { 3 => "idle", 4 => "printing", 5 => "warming up", _ => "ready" });
        var problems = reading.ErrorNames.ToList();
        problems.AddRange(reading.Alerts.Where(alert => alert.Severity is 3 or 4 or 5).Select(alert => alert.Description).Where(text => !problems.Contains(text, StringComparer.OrdinalIgnoreCase)));
        if (problems.Count > 0)
        {
            builder.Append(" - ").Append(string.Join(", ", problems.Take(4)));
        }

        if (known.Count > 0)
        {
            var lowest = known.MinBy(supply => supply.Percent)!;
            builder.Append($" ({lowest.Name} {lowest.Percent!.Value.ToString("0", CultureInfo.InvariantCulture)} %");
            builder.Append(reading.PageCount is { } pages ? $", {pages.ToString("#,0", CultureInfo.InvariantCulture)} pages)" : ")");
        }
        else if (reading.PageCount is { } pages)
        {
            builder.Append($" ({pages.ToString("#,0", CultureInfo.InvariantCulture)} pages)");
        }

        return builder.ToString();
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private static byte[] ErrorBytes(SnmpDiscoveryItem item)
    {
        if (item.RawHex is { Length: > 0 } hex && hex.Length % 2 == 0)
        {
            try
            {
                return Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                // fall through
            }
        }

        return Encoding.Latin1.GetBytes(item.Value);
    }

    private static string Clean(SnmpDiscoveryItem item)
    {
        // Printers pad descriptions with NULs and spaces; some send them as hex when a byte is not text.
        if (item.RawHex is { Length: > 0 } hex && item.Value == hex)
        {
            try
            {
                return Encoding.Latin1.GetString(Convert.FromHexString(hex)).Trim('\0', ' ').Trim();
            }
            catch (FormatException)
            {
                // fall through
            }
        }

        return item.Value.Trim('\0', ' ').Trim();
    }

    private static SensorState Worse(SensorState left, SensorState right) =>
        left == SensorState.Critical || right == SensorState.Critical ? SensorState.Critical
        : left == SensorState.Warning || right == SensorState.Warning ? SensorState.Warning
        : left;

    private static string UniqueKey(string key, HashSet<string> used)
    {
        var candidate = key;
        for (var index = 2; !used.Add(candidate); index++)
        {
            candidate = key + "-" + index.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    /// <summary>Orders OID index suffixes ("1.10" after "1.9") numerically, component by component.</summary>
    private sealed class OidIndexComparer : IComparer<string>
    {
        public static readonly OidIndexComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var left = (x ?? string.Empty).Split('.');
            var right = (y ?? string.Empty).Split('.');
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var compared = long.TryParse(left[index], out var a) && long.TryParse(right[index], out var b)
                    ? a.CompareTo(b)
                    : string.CompareOrdinal(left[index], right[index]);
                if (compared != 0)
                {
                    return compared;
                }
            }

            return left.Length.CompareTo(right.Length);
        }
    }
}
