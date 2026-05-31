using System.Globalization;

namespace Matmon.Core.Domain;

public sealed class SnmpInterfaceSensorExecutor : ISensorExecutor
{
    private static readonly SnmpSensorExecutor InnerExecutor = new();

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "snmp-interface",
        DisplayName = "SNMP Interface",
        Description = "Reads interface counters and status via SNMP ifIndex.",
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

    public ValueTask<SensorExecutionResult> ExecuteAsync(
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

        settings.DefaultChannelKey ??= "oper_status";
        return InnerExecutor.ExecuteAsync(
            new SensorExecutionContext(SnmpSensorExecutor.Definition.Key, context.Target, settings),
            cancellationToken);
    }

    private static string BuildInterfaceOids(int ifIndex)
    {
        return $"""
1.3.6.1.2.1.2.2.1.8.{ifIndex}|Oper Status
1.3.6.1.2.1.31.1.1.1.6.{ifIndex}|In Octets
1.3.6.1.2.1.31.1.1.1.10.{ifIndex}|Out Octets
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
