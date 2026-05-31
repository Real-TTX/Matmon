namespace Matmon.Core.Domain;

public sealed class UpsSnmpSensorExecutor : ISensorExecutor
{
    private static readonly SnmpSensorExecutor InnerExecutor = new();

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "ups-snmp",
        DisplayName = "UPS SNMP",
        Description = "Reads common UPS-MIB values over SNMP, such as battery charge, runtime and load.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = BuildParameters()
    };

    private const string DefaultOids = """
1.3.6.1.2.1.33.1.2.4.0|Battery Charge
1.3.6.1.2.1.33.1.2.3.0|Runtime Minutes
1.3.6.1.2.1.33.1.4.4.1.5.1|Load Percent
1.3.6.1.2.1.33.1.2.1.0|Battery Status
""";

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

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = context.Settings.Clone();
        if (!MonitoringSettings.TryReadParameter(settings, "snmp.oids", out var rawOids) ||
            string.IsNullOrWhiteSpace(rawOids))
        {
            settings.Parameters["snmp.oids"] = DefaultOids;
        }

        settings.DefaultChannelKey ??= "battery_charge";
        return InnerExecutor.ExecuteAsync(
            new SensorExecutionContext(SnmpSensorExecutor.Definition.Key, context.Target, settings),
            cancellationToken);
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
