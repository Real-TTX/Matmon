using System.Globalization;

namespace Matmon.Core.Domain;

/// <summary>
/// What a UPS says, whatever it was read through - the standard UPS-MIB over SNMP (<see cref="UpsSnmpSensorExecutor"/>)
/// or a Network UPS Tools server (<see cref="NutUpsSensorExecutor"/>). Every field is optional: a UPS that does not
/// report a value simply has no channel for it.
/// </summary>
public sealed record UpsReading
{
    public bool? OnBattery { get; init; }

    public bool? LowBattery { get; init; }

    public bool? ReplaceBattery { get; init; }

    public bool? Overload { get; init; }

    public bool? OnBypass { get; init; }

    public bool? Charging { get; init; }

    public double? ChargePercent { get; init; }

    public double? RuntimeMinutes { get; init; }

    public double? LoadPercent { get; init; }

    public double? InputVoltage { get; init; }

    public double? OutputVoltage { get; init; }

    public double? BatteryVoltage { get; init; }

    public double? TemperatureCelsius { get; init; }

    /// <summary>How long it has been running on battery (UPS-MIB only; 0 while on mains).</summary>
    public double? SecondsOnBattery { get; init; }

    public string? Model { get; init; }
}

/// <summary>
/// The ONE channel set every UPS sensor reports, so a rule, a threshold or a wallboard tile works the same for an APC
/// on a network card and a cheap UPS on the USB port of a NAS. The flags are 1/0 channels (<c>on_battery</c>,
/// <c>low_battery</c>, ...), which is what the default thresholds alarm on - a power cut used to stay green until the
/// battery had drained below 50 %, because the only channels were charge, runtime and load.
/// </summary>
public static class UpsChannels
{
    public const string OnBattery = "on_battery";
    public const string LowBattery = "low_battery";
    public const string ReplaceBattery = "replace_battery";
    public const string Overload = "overload";
    public const string OnBypass = "on_bypass";
    public const string BatteryCharge = "battery_charge";
    public const string RuntimeMinutes = "runtime_minutes";
    public const string LoadPercent = "load_percent";
    public const string InputVoltage = "input_voltage";
    public const string OutputVoltage = "output_voltage";
    public const string BatteryVoltage = "battery_voltage";
    public const string Temperature = "temperature";
    public const string SecondsOnBattery = "seconds_on_battery";

    /// <summary>The channels in the order a sensor lists them: the flags first (what needs attention), then the values.</summary>
    public static List<SensorChannelValue> ToChannels(UpsReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var channels = new List<SensorChannelValue>();

        // The power cut is worth a history (how often, how long); the other flags only mirror the state.
        AddFlag(channels, OnBattery, "On battery", reading.OnBattery, logByDefault: true);
        AddFlag(channels, LowBattery, "Low battery", reading.LowBattery);
        AddFlag(channels, ReplaceBattery, "Replace battery", reading.ReplaceBattery);
        AddFlag(channels, Overload, "Overload", reading.Overload);
        AddFlag(channels, OnBypass, "On bypass", reading.OnBypass);
        AddValue(channels, BatteryCharge, "Battery charge", reading.ChargePercent, "%", SensorMeasurementKind.Percent);
        AddValue(channels, RuntimeMinutes, "Runtime", reading.RuntimeMinutes, "min", SensorMeasurementKind.Unknown);
        AddValue(channels, LoadPercent, "Load", reading.LoadPercent, "%", SensorMeasurementKind.Percent);
        AddValue(channels, InputVoltage, "Input voltage", reading.InputVoltage, "V", SensorMeasurementKind.Unknown);
        AddValue(channels, OutputVoltage, "Output voltage", reading.OutputVoltage, "V", SensorMeasurementKind.Unknown);
        AddValue(channels, BatteryVoltage, "Battery voltage", reading.BatteryVoltage, "V", SensorMeasurementKind.Unknown);
        AddValue(channels, Temperature, "Temperature", reading.TemperatureCelsius, "°C", SensorMeasurementKind.Temperature);
        AddValue(channels, SecondsOnBattery, "Time on battery", reading.SecondsOnBattery, "s", SensorMeasurementKind.Duration);
        return channels;
    }

    /// <summary>One line for the sensor's message: "On battery - 87 % - 23 min left", "Online - charging - 100 %".</summary>
    public static string Describe(UpsReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var parts = new List<string>();
        if (reading.OnBattery == true)
        {
            parts.Add(reading.LowBattery == true ? "On battery, LOW" : "On battery");
        }
        else if (reading.OnBypass == true)
        {
            parts.Add("On bypass");
        }
        else if (reading.OnBattery == false)
        {
            parts.Add(reading.Charging == true ? "Online, charging" : "Online");
        }

        if (reading.ChargePercent is double charge)
        {
            parts.Add($"{Format(charge)} %");
        }

        if (reading.RuntimeMinutes is double runtime)
        {
            parts.Add($"{Format(runtime)} min left");
        }

        if (reading.Overload == true)
        {
            parts.Add("overloaded");
        }

        if (reading.ReplaceBattery == true)
        {
            parts.Add("replace the battery");
        }

        return parts.Count == 0 ? "UPS answered" : string.Join(" - ", parts);
    }

    /// <summary>
    /// The variables a NUT server lists for one UPS (<c>LIST VAR</c>). The state is <c>ups.status</c>, a list of
    /// flags: OL online, OB on battery, LB low battery, RB replace battery, CHRG charging, BYPASS, OVER overloaded,
    /// FSD forced shutdown (the server is about to switch everything off - as urgent as a low battery).
    /// </summary>
    public static UpsReading FromNut(IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var flags = (Read(variables, "ups.status") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(flag => flag.ToUpperInvariant())
            .ToHashSet();
        var hasStatus = flags.Count > 0;

        var runtimeSeconds = Number(variables, "battery.runtime");
        return new UpsReading
        {
            OnBattery = hasStatus ? flags.Contains("OB") : null,
            LowBattery = hasStatus ? flags.Contains("LB") || flags.Contains("FSD") : null,
            ReplaceBattery = hasStatus ? flags.Contains("RB") : null,
            Overload = hasStatus ? flags.Contains("OVER") : null,
            OnBypass = hasStatus ? flags.Contains("BYPASS") : null,
            Charging = hasStatus ? flags.Contains("CHRG") : null,
            ChargePercent = Number(variables, "battery.charge"),
            RuntimeMinutes = runtimeSeconds is double seconds ? Math.Round(seconds / 60.0, 1) : null,
            LoadPercent = Number(variables, "ups.load"),
            InputVoltage = Number(variables, "input.voltage"),
            OutputVoltage = Number(variables, "output.voltage"),
            BatteryVoltage = Number(variables, "battery.voltage"),
            TemperatureCelsius = Number(variables, "ups.temperature") ?? Number(variables, "battery.temperature"),
            Model = JoinModel(Read(variables, "ups.mfr") ?? Read(variables, "device.mfr"), Read(variables, "ups.model") ?? Read(variables, "device.model"))
        };
    }

    /// <summary>
    /// The UPS-MIB (RFC 1628) values an SNMP read returned, by channel key. upsOutputSource: 3 normal, 4 bypass,
    /// 5 battery; upsBatteryStatus: 2 normal, 3 low, 4 depleted; upsSecondsOnBattery is 0 on mains.
    /// </summary>
    public static UpsReading FromRfc1628(IReadOnlyList<SensorChannelValue> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        double? Value(string key) => channels.FirstOrDefault(channel => string.Equals(channel.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

        var outputSource = Value("output_source");
        var batteryStatus = Value("battery_status");
        var secondsOnBattery = Value(SecondsOnBattery);
        bool? onBattery = outputSource is null && secondsOnBattery is null
            ? null
            : outputSource == 5 || secondsOnBattery > 0;

        return new UpsReading
        {
            OnBattery = onBattery,
            LowBattery = batteryStatus is null ? null : batteryStatus is 3 or 4,
            OnBypass = outputSource is null ? null : outputSource == 4,
            ChargePercent = Value(BatteryCharge),
            RuntimeMinutes = Value(RuntimeMinutes),
            LoadPercent = Value(LoadPercent),
            InputVoltage = Value(InputVoltage),
            SecondsOnBattery = secondsOnBattery
        };
    }

    /// <summary>
    /// The values an APC card returned through its PowerNet-MIB (<see cref="UpsSnmpSensorExecutor.ApcOids"/>), with the
    /// status codes the NUT snmp-ups driver uses for APC: upsBasicOutputStatus 3 = on battery, 9 / 10 / 16 / 17 = bypass;
    /// upsBasicBatteryStatus 3 low, 4 fault, 5 = low battery; upsAdvBatteryReplaceIndicator 2 = replace. The runtime is a
    /// TimeTicks value, read as seconds.
    /// </summary>
    public static UpsReading FromApc(IReadOnlyList<SensorChannelValue> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        double? Value(string key) => channels.FirstOrDefault(channel => string.Equals(channel.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

        var outputStatus = Value("apc_output_status");
        var batteryStatus = Value("apc_battery_status");
        var replace = Value("apc_replace_battery");
        var runtimeSeconds = Value("apc_runtime");
        return new UpsReading
        {
            OnBattery = outputStatus is null ? null : outputStatus == 3,
            OnBypass = outputStatus is null ? null : outputStatus is 9 or 10 or 16 or 17,
            LowBattery = batteryStatus is null ? null : batteryStatus is 3 or 4 or 5,
            ReplaceBattery = replace is null ? null : replace == 2,
            ChargePercent = Value(BatteryCharge),
            RuntimeMinutes = runtimeSeconds is double seconds ? Math.Round(seconds / 60.0, 1) : null,
            LoadPercent = Value(LoadPercent),
            InputVoltage = Value(InputVoltage),
            OutputVoltage = Value(OutputVoltage),
            BatteryVoltage = Value(BatteryVoltage),
            TemperatureCelsius = Value(Temperature)
        };
    }

    /// <summary>The UPS channels added to (or, for a key it already has, merged into) what a read returned: the flags
    /// derived from the raw UPS-MIB values, and a unit and a readable label on the values it read.</summary>
    public static List<SensorChannelValue> Merge(IReadOnlyList<SensorChannelValue> existing, UpsReading reading)
    {
        var derived = ToChannels(reading).ToDictionary(channel => channel.Key, StringComparer.OrdinalIgnoreCase);
        var merged = new List<SensorChannelValue>();
        foreach (var channel in existing)
        {
            if (derived.Remove(channel.Key, out var normalized))
            {
                merged.Add(channel with
                {
                    Label = normalized.Label,
                    Unit = normalized.Unit,
                    MeasurementKind = normalized.MeasurementKind,
                    LogByDefault = channel.LogByDefault && normalized.LogByDefault
                });
                continue;
            }

            merged.Add(channel);
        }

        // New flags go to the front, as in ToChannels: they are what needs attention.
        merged.InsertRange(0, derived.Values.Where(channel => channel.MeasurementKind == SensorMeasurementKind.Boolean));
        merged.AddRange(derived.Values.Where(channel => channel.MeasurementKind != SensorMeasurementKind.Boolean));
        return merged;
    }

    private static void AddFlag(List<SensorChannelValue> channels, string key, string label, bool? value, bool logByDefault = false)
    {
        if (value is null)
        {
            return;
        }

        channels.Add(new SensorChannelValue
        {
            Key = key,
            Label = label,
            Value = value.Value ? 1 : 0,
            MeasurementKind = SensorMeasurementKind.Boolean,
            LogByDefault = logByDefault
        });
    }

    private static void AddValue(List<SensorChannelValue> channels, string key, string label, double? value, string unit, SensorMeasurementKind kind)
    {
        if (value is null)
        {
            return;
        }

        channels.Add(new SensorChannelValue
        {
            Key = key,
            Label = label,
            Value = value,
            Unit = unit,
            MeasurementKind = kind
        });
    }

    private static string? Read(IReadOnlyDictionary<string, string> variables, string key) =>
        variables.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static double? Number(IReadOnlyDictionary<string, string> variables, string key) =>
        Read(variables, key) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string? JoinModel(string? manufacturer, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return manufacturer;
        }

        return string.IsNullOrWhiteSpace(manufacturer) || model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase)
            ? model
            : $"{manufacturer} {model}";
    }

    private static string Format(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
