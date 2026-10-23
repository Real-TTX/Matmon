using System.Globalization;

namespace Matmon.Core.Domain;

public sealed class SnmpInterfaceSensorExecutor : ISensorExecutor
{
    private static readonly SnmpSensorExecutor InnerExecutor = new();

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "snmp-interface",
        DisplayName = "SNMP Interface",
        Description = "Reads an interface's status, traffic (Mbit/s, from the counters' change since the last check) and utilisation via SNMP ifIndex.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            .. BuildSnmpParameters(),
            new SensorParameterDefinition
            {
                Key = "snmp.interfaceIndex",
                Label = "Interface index",
                Kind = SensorParameterKind.Integer,
                Description = "SNMP ifIndex of the interface.",
                Required = true,
                Min = 1,
                Max = 2147483647,
                Step = "1"
            }
        ]
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

        var settings = new MonitoringSettings();
        settings.Parameters["snmp.community"] = string.IsNullOrWhiteSpace(context.SnmpCommunity)
            ? "public"
            : context.SnmpCommunity.Trim();
        settings.Parameters["snmp.version"] = string.IsNullOrWhiteSpace(context.SnmpVersion)
            ? "v2c"
            : context.SnmpVersion.Trim();
        settings.Parameters["snmp.port"] = (context.SnmpPort is >= 1 and <= 65535 ? context.SnmpPort : 161).ToString(CultureInfo.InvariantCulture);
        settings.Parameters["snmp.interfaceIndex"] = "1";
        settings.Parameters["snmp.oids"] = BuildInterfaceOids(1);
        settings.DefaultChannelKey = "oper_status";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "SNMP Interface",
                    string.Empty,
                    settings,
                    "SNMP answered. Pick an interface index or use SNMP walk.",
                    68)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = context.Settings.Clone();
        if ((!MonitoringSettings.TryReadParameter(settings, "snmp.oids", out var rawOids) ||
             string.IsNullOrWhiteSpace(rawOids)) &&
            MonitoringSettings.TryReadParameterInt(settings, "snmp.interfaceIndex", out var interfaceIndex) &&
            interfaceIndex > 0)
        {
            settings.Parameters["snmp.oids"] = BuildInterfaceOids(interfaceIndex);
        }

        // An interface read through the 64-bit counters is in the ifXTable, so its speed (ifHighSpeed) is there too -
        // asked for as well when the list does not (an older sensor's list), for the utilisation.
        if (MonitoringSettings.TryReadParameter(settings, "snmp.oids", out var oids) && !string.IsNullOrWhiteSpace(oids))
        {
            settings.Parameters["snmp.oids"] = InterfaceTraffic.WithSpeed(oids);
        }

        settings.DefaultChannelKey ??= "oper_status";
        var result = await InnerExecutor.ExecuteAsync(
            new SensorExecutionContext(SnmpSensorExecutor.Definition.Key, context.Target, settings),
            cancellationToken);
        if (result.Channels.Count == 0)
        {
            return result; // the read failed - nothing to work a rate out of
        }

        var withTraffic = InterfaceTraffic.AddRates(settings.Parameters["snmp.oids"], result, context.PreviousObservation, DateTimeOffset.UtcNow);
        return ReferenceEquals(withTraffic, result) ? result : SensorThresholdEvaluator.ApplyChannelThresholds(settings, withTraffic);
    }

    private static string BuildInterfaceOids(int ifIndex)
    {
        return $"""
1.3.6.1.2.1.2.2.1.8.{ifIndex}|Oper Status
1.3.6.1.2.1.31.1.1.1.6.{ifIndex}|In Octets
1.3.6.1.2.1.31.1.1.1.10.{ifIndex}|Out Octets
1.3.6.1.2.1.31.1.1.1.15.{ifIndex}|Speed
1.3.6.1.2.1.2.2.1.14.{ifIndex}|In Errors
1.3.6.1.2.1.2.2.1.20.{ifIndex}|Out Errors
""";
    }

    private static IReadOnlyList<SensorParameterDefinition> BuildSnmpParameters()
    {
        return SnmpSensorExecutor.Definition.Parameters
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
            Options = parameter.Options,
            CredentialKind = parameter.CredentialKind,
            VisibleWhenParameterKey = parameter.VisibleWhenParameterKey,
            VisibleWhenValues = parameter.VisibleWhenValues
        };
    }
}

/// <summary>
/// Traffic as a RATE. An interface's octet counters only ever grow, so the number that means something is how much they
/// grew since the last check: (now - before) x 8 / seconds = bit/s, shown in Mbit/s, and against the interface's speed as
/// a utilisation. The counters are found by their OID (64-bit ifHCIn/OutOctets, else 32-bit ifIn/OutOctets), whatever
/// the user labelled them. A 64-bit counter that went DOWN was reset (a reboot) - that check gets no rate; a 32-bit one
/// wraps at 4 GiB, which on a gigabit link happens every 34 s, so one wrap is taken into account (and a "rate" faster than
/// the link is thrown away rather than shown). Pure - the previous result and the time are passed in.
/// </summary>
public static class InterfaceTraffic
{
    private const string HcInOctets = "1.3.6.1.2.1.31.1.1.1.6.";
    private const string HcOutOctets = "1.3.6.1.2.1.31.1.1.1.10.";
    private const string InOctets = "1.3.6.1.2.1.2.2.1.10.";
    private const string OutOctets = "1.3.6.1.2.1.2.2.1.16.";
    private const string HighSpeed = "1.3.6.1.2.1.31.1.1.1.15.";   // Mbit/s
    private const string Speed = "1.3.6.1.2.1.2.2.1.5.";           // bit/s
    private const double Wrap32 = 4294967296d;

    public const string TrafficIn = "traffic_in";
    public const string TrafficOut = "traffic_out";
    public const string UtilizationIn = "utilization_in";
    public const string UtilizationOut = "utilization_out";

    /// <summary>The OID list with ifHighSpeed added when it reads the 64-bit counters and asks for no speed itself.</summary>
    public static string WithSpeed(string oids)
    {
        var selections = SnmpSensorExecutor.ParseOidSelections(oids);
        if (selections.Any(selection => IsUnder(selection.Oid, HighSpeed) || IsUnder(selection.Oid, Speed))
            || selections.FirstOrDefault(selection => IsUnder(selection.Oid, HcInOctets)) is not { } inCounter)
        {
            return oids;
        }

        var ifIndex = inCounter.Oid[HcInOctets.Length..];
        return oids.TrimEnd() + "\n" + HighSpeed + ifIndex + "|Speed";
    }

    /// <summary>The result with traffic and utilisation channels added (and a word on the traffic in the message) - or the
    /// same instance when there is nothing to work out (no previous result, no counters, a reset).</summary>
    public static SensorExecutionResult AddRates(string oids, SensorExecutionResult result, SensorObservation? previous, DateTimeOffset now)
    {
        if (previous is null)
        {
            return result;
        }

        var seconds = (now - previous.TimestampUtc).TotalSeconds;
        if (seconds < 1)
        {
            return result;
        }

        var selections = SnmpSensorExecutor.ParseOidSelections(oids);
        string? KeyOf(params string[] prefixes) => selections
            .Where(selection => prefixes.Any(prefix => IsUnder(selection.Oid, prefix)))
            .Select(selection => SnmpSensorExecutor.NormalizeKey(string.IsNullOrWhiteSpace(selection.Label) ? selection.Oid : selection.Label.Trim()))
            .FirstOrDefault();
        bool Is32(string? key) => key is not null && selections.Any(selection =>
            (IsUnder(selection.Oid, InOctets) || IsUnder(selection.Oid, OutOctets))
            && SnmpSensorExecutor.NormalizeKey(string.IsNullOrWhiteSpace(selection.Label) ? selection.Oid : selection.Label.Trim()) == key);

        double? Current(string? key) => key is null ? null : result.Channels.FirstOrDefault(channel => channel.Key == key)?.Value;
        double? Before(string? key) => key is null ? null : previous.Channels.FirstOrDefault(channel => channel.Key == key)?.Value;

        var speedKey = KeyOf(HighSpeed, Speed);
        var speedMbps = Current(speedKey) is { } speed and > 0
            ? selections.Any(selection => IsUnder(selection.Oid, HighSpeed)) ? speed : speed / 1_000_000d
            : (double?)null;

        var added = new List<SensorChannelValue>();
        var words = new List<string>();
        foreach (var (direction, key, trafficKey, utilisationKey) in new[]
                 {
                     ("in", KeyOf(HcInOctets, InOctets), TrafficIn, UtilizationIn),
                     ("out", KeyOf(HcOutOctets, OutOctets), TrafficOut, UtilizationOut)
                 })
        {
            if (Rate(Current(key), Before(key), Is32(key), seconds, speedMbps) is not { } bitsPerSecond)
            {
                continue;
            }

            var mbps = Math.Round(bitsPerSecond / 1_000_000d, 3);
            added.Add(new SensorChannelValue { Key = trafficKey, Label = $"Traffic {direction}", Value = mbps, Unit = "Mbit/s" });
            words.Add($"{direction} {FormatBits(bitsPerSecond)}");
            if (speedMbps is { } linkMbps)
            {
                added.Add(new SensorChannelValue
                {
                    Key = utilisationKey,
                    Label = $"Utilization {direction}",
                    Value = Math.Round(Math.Min(100, mbps / linkMbps * 100), 1),
                    Unit = "%",
                    MeasurementKind = SensorMeasurementKind.Percent
                });
            }
        }

        if (added.Count == 0)
        {
            return result;
        }

        var message = string.IsNullOrWhiteSpace(result.Message) ? string.Join(", ", words) : $"{result.Message} - {string.Join(", ", words)}";
        return result with { Channels = [.. result.Channels, .. added], Message = message };
    }

    /// <summary>bit/s from two counter readings, or null when there is no honest answer.</summary>
    public static double? Rate(double? current, double? before, bool is32Bit, double seconds, double? linkMbps)
    {
        if (current is not { } now || before is not { } then || seconds <= 0)
        {
            return null;
        }

        var delta = now - then;
        if (delta < 0)
        {
            if (!is32Bit)
            {
                return null; // a 64-bit counter does not wrap - it was reset
            }

            delta += Wrap32;
        }

        var bitsPerSecond = delta * 8 / seconds;
        return linkMbps is { } link && bitsPerSecond > link * 1_000_000d * 1.05 ? null : bitsPerSecond;
    }

    public static string FormatBits(double bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000_000 => (bitsPerSecond / 1_000_000_000).ToString("0.##", CultureInfo.InvariantCulture) + " Gbit/s",
        >= 1_000_000 => (bitsPerSecond / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + " Mbit/s",
        _ => (bitsPerSecond / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + " kbit/s"
    };

    private static bool IsUnder(string oid, string prefix) => oid.TrimStart('.').StartsWith(prefix, StringComparison.Ordinal);
}
