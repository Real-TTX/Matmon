namespace Matmon.Core.Domain;

/// <summary>
/// A UPS with a network card, read over SNMP through the standard UPS-MIB (RFC 1628) - on top of the generic SNMP read,
/// it turns the raw values into the shared UPS channels (<see cref="UpsChannels"/>): <c>on_battery</c> from the output
/// source / the seconds on battery, <c>low_battery</c> from the battery status, <c>on_bypass</c>. Without those a power
/// cut stayed green until the battery had drained below 50 %. Some cards serve the UPS-MIB only once it is switched on
/// in their settings (often next to the vendor MIB).
/// </summary>
public sealed class UpsSnmpSensorExecutor : ISensorExecutor
{
    private static readonly SnmpSensorExecutor InnerExecutor = new();

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "ups-snmp",
        DisplayName = "UPS SNMP",
        Description = "A UPS with a network card, read over SNMP (standard UPS-MIB, RFC 1628): on battery, low battery, charge, runtime, load, input voltage.",
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
        if (!MonitoringSettings.TryReadParameter(settings, "snmp.oids", out var rawOids) ||
            string.IsNullOrWhiteSpace(rawOids))
        {
            settings.Parameters["snmp.oids"] = DefaultOids;
        }

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

        var reading = UpsChannels.FromRfc1628(result.Channels);
        var merged = new SensorExecutionResult(result.State, result.Duration, result.Value, UpsChannels.Describe(reading))
        {
            DefaultChannelKey = result.DefaultChannelKey,
            Channels = UpsChannels.Merge(result.Channels, reading)
        };
        return SensorThresholdEvaluator.ApplyChannelThresholds(settings, merged);
    }

    private static IReadOnlyList<SensorParameterDefinition> BuildParameters()
    {
        return SnmpSensorExecutor.Definition.Parameters
            .Select(parameter => parameter.Key == "snmp.oids"
                ? CloneParameter(parameter) with
                {
                    DefaultValue = DefaultOids,
                    Description = "UPS-MIB OIDs. Override when your UPS vendor exposes different OIDs."
                }
                : CloneParameter(parameter))
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
