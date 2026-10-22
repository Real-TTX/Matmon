namespace Matmon.Core.Domain;

/// <summary>
/// A UPS with a network card, read over SNMP through the standard UPS-MIB (RFC 1628) - on top of the generic SNMP read,
/// it turns the raw values into the shared UPS channels (<see cref="UpsChannels"/>): <c>on_battery</c> from the output
/// source / the seconds on battery, <c>low_battery</c> from the battery status, <c>on_bypass</c>. Without those a power
/// cut stayed green until the battery had drained below 50 %. Some cards serve the UPS-MIB only once it is switched on
/// in their settings (often next to the vendor MIB) - and an APC card that does not can be read through its own
/// PowerNet-MIB instead (<c>ups.profile</c> = apc), into the same channels.
/// </summary>
public sealed class UpsSnmpSensorExecutor : ISensorExecutor
{
    private static readonly SnmpSensorExecutor InnerExecutor = new();

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "ups-snmp",
        DisplayName = "UPS SNMP",
        Description = "A UPS with a network card, read over SNMP (standard UPS-MIB, RFC 1628, or APC's PowerNet-MIB): on battery, low battery, charge, runtime, load, input voltage.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = BuildParameters()
    };

    /// <summary>upsEstimatedChargeRemaining, upsEstimatedMinutesRemaining, upsOutputPercentLoad (line 1),
    /// upsBatteryStatus, upsSecondsOnBattery, upsOutputSource, upsInputVoltage (line 1), upsAlarmsPresent.</summary>
    public const string DefaultOids = """
1.3.6.1.2.1.33.1.2.4.0|Battery Charge
1.3.6.1.2.1.33.1.2.3.0|Runtime Minutes
1.3.6.1.2.1.33.1.4.4.1.5.1|Load Percent
1.3.6.1.2.1.33.1.2.1.0|Battery Status
1.3.6.1.2.1.33.1.2.2.0|Seconds On Battery
1.3.6.1.2.1.33.1.4.1.0|Output Source
1.3.6.1.2.1.33.1.3.3.1.3.1|Input Voltage
1.3.6.1.2.1.33.1.6.1.0|Alarms Present
""";

    public const string ProfileParameter = "ups.profile";
    public const string StandardProfile = "rfc1628";
    public const string ApcProfile = "apc";

    /// <summary>
    /// APC's own PowerNet-MIB (enterprise 318) - for a card that does not serve the UPS-MIB. upsAdvBatteryCapacity,
    /// upsAdvBatteryRunTimeRemaining (TimeTicks, read as seconds), upsAdvOutputLoad, upsBasicBatteryStatus,
    /// upsBasicOutputStatus, upsAdvBatteryReplaceIndicator, upsAdvInputLineVoltage, upsAdvOutputVoltage,
    /// upsAdvBatteryActualVoltage, upsAdvBatteryTemperature. Cross-checked against the OIDs and status codes the NUT
    /// snmp-ups driver uses for APC (apc-mib.c) - not yet against a real card.
    /// </summary>
    public const string ApcOids = """
1.3.6.1.4.1.318.1.1.1.2.2.1.0|Battery Charge
1.3.6.1.4.1.318.1.1.1.2.2.3.0|APC Runtime
1.3.6.1.4.1.318.1.1.1.4.2.3.0|Load Percent
1.3.6.1.4.1.318.1.1.1.2.1.1.0|APC Battery Status
1.3.6.1.4.1.318.1.1.1.4.1.1.0|APC Output Status
1.3.6.1.4.1.318.1.1.1.2.2.4.0|APC Replace Battery
1.3.6.1.4.1.318.1.1.1.3.2.1.0|Input Voltage
1.3.6.1.4.1.318.1.1.1.4.2.1.0|Output Voltage
1.3.6.1.4.1.318.1.1.1.2.2.8.0|Battery Voltage
1.3.6.1.4.1.318.1.1.1.2.2.2.0|Temperature
""";

    /// <summary>
    /// The OIDs a sensor reads: its own list - unless that list is empty or one of the built-in defaults, in which case
    /// the chosen profile's default wins. The editor fills the field with the standard list on create, so switching the
    /// profile to APC would otherwise go on reading the UPS-MIB; a list someone edited is always left alone.
    /// </summary>
    public static string ResolveOids(MonitoringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var profileDefault = IsApcProfile(settings) ? ApcOids : DefaultOids;
        if (!MonitoringSettings.TryReadParameter(settings, "snmp.oids", out var oids) || string.IsNullOrWhiteSpace(oids))
        {
            return profileDefault;
        }

        return IsOidList(oids, DefaultOids) || IsOidList(oids, LegacyDefaultOids) || IsOidList(oids, ApcOids)
            ? profileDefault
            : oids;
    }

    public static bool IsApcProfile(MonitoringSettings settings) =>
        MonitoringSettings.TryReadParameter(settings, ProfileParameter, out var profile) &&
        string.Equals(profile?.Trim(), ApcProfile, StringComparison.OrdinalIgnoreCase);

    /// <summary>What the OID list was before the on-battery values - a sensor that still carries exactly this is
    /// brought up to <see cref="DefaultOids"/> by migration 7 (one it was edited on is left alone).</summary>
    public const string LegacyDefaultOids = """
1.3.6.1.2.1.33.1.2.4.0|Battery Charge
1.3.6.1.2.1.33.1.2.3.0|Runtime Minutes
1.3.6.1.2.1.33.1.4.4.1.5.1|Load Percent
1.3.6.1.2.1.33.1.2.1.0|Battery Status
""";

    /// <summary>Whether an OID list is <paramref name="expected"/>, ignoring blank lines, spaces and line endings.</summary>
    public static bool IsOidList(string? oids, string expected)
    {
        static string Normalize(string? text) => string.Join('\n', (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.Equals(Normalize(oids), Normalize(expected), StringComparison.OrdinalIgnoreCase);
    }

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
        settings.Parameters["snmp.port"] = (context.SnmpPort is >= 1 and <= 65535 ? context.SnmpPort : 161).ToString();
        settings.Parameters["snmp.oids"] = DefaultOids;
        settings.DefaultChannelKey = "battery_charge";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "UPS SNMP",
                    string.Empty,
                    settings,
                    "SNMP answered. UPS-MIB values can be tested.",
                    62)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = context.Settings.Clone();
        settings.Parameters["snmp.oids"] = ResolveOids(settings);

        settings.DefaultChannelKey ??= UpsChannels.BatteryCharge;
        var result = await InnerExecutor.ExecuteAsync(
            new SensorExecutionContext(SnmpSensorExecutor.Definition.Key, context.Target, settings),
            cancellationToken);
        return ToUpsResult(result, context.Settings);
    }

    /// <summary>Adds the UPS channels to what the SNMP read returned and re-applies the thresholds, so the derived
    /// flags (on battery, low battery) alarm like any channel. A failed read is passed through untouched.</summary>
    public static SensorExecutionResult ToUpsResult(SensorExecutionResult result, MonitoringSettings settings)
    {
        if (result.Channels.Count == 0 || result.State is SensorState.Disabled or SensorState.Paused)
        {
            return result;
        }

        // Which MIB the values came from shows in the channels: the APC list carries its own raw status channels.
        var reading = result.Channels.Any(channel => channel.Key.StartsWith("apc_", StringComparison.OrdinalIgnoreCase))
            ? UpsChannels.FromApc(result.Channels)
            : UpsChannels.FromRfc1628(result.Channels);
        var merged = new SensorExecutionResult(result.State, result.Duration, result.Value, UpsChannels.Describe(reading))
        {
            DefaultChannelKey = result.DefaultChannelKey,
            Channels = UpsChannels.Merge(result.Channels, reading)
        };
        return SensorThresholdEvaluator.ApplyChannelThresholds(settings, merged);
    }

    private static IReadOnlyList<SensorParameterDefinition> BuildParameters()
    {
        var profile = new SensorParameterDefinition
        {
            Key = ProfileParameter,
            Label = "MIB",
            Kind = SensorParameterKind.ValueList,
            Description = "Which MIB the card is read through. The standard UPS-MIB works for most cards (some only once it is switched on in their settings); APC PowerNet reads an APC card through APC's own MIB.",
            DefaultValue = StandardProfile,
            Options =
            [
                new SensorParameterOption { Value = StandardProfile, Label = "Standard UPS-MIB (RFC 1628)" },
                new SensorParameterOption { Value = ApcProfile, Label = "APC PowerNet-MIB" }
            ]
        };

        return SnmpSensorExecutor.Definition.Parameters
            .Select(parameter => parameter.Key == "snmp.oids"
                ? CloneParameter(parameter) with
                {
                    DefaultValue = DefaultOids,
                    Description = "The values read. Leave the built-in list and the MIB choice above decides; edit it when your UPS exposes different OIDs."
                }
                : CloneParameter(parameter))
            .Prepend(profile)
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
            Group = parameter.Group,
            CredentialKind = parameter.CredentialKind,
            VisibleWhenParameterKey = parameter.VisibleWhenParameterKey,
            VisibleWhenValues = parameter.VisibleWhenValues
        };
    }
}
