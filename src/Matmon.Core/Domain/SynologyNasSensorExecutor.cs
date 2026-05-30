namespace Matmon.Core.Domain;

public sealed class SynologyNasSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "synology",
        DisplayName = "Synology NAS",
        Description = "Synology-oriented SNMP monitoring. Use Walk to discover and pick the OIDs you want.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = BuildParameters()
    };

    private static readonly SnmpSensorExecutor InnerExecutor = new();

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

        var looksLikeSynology = context.OpenTcpPorts.Contains(5000) ||
            context.OpenTcpPorts.Contains(5001) ||
            (!string.IsNullOrWhiteSpace(context.SnmpSummary) &&
                context.SnmpSummary.Contains("synology", StringComparison.OrdinalIgnoreCase));

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
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        settings.Parameters["snmp.oids"] = "1.3.6.1.2.1.1.3.0|Uptime";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Synology NAS",
                    string.Empty,
                    settings,
                    "SNMP answered and Synology DSM ports or system description were detected.",
                    88)));
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        return InnerExecutor.ExecuteAsync(context, cancellationToken);
    }

    private static IReadOnlyList<SensorParameterDefinition> BuildParameters()
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
            CredentialKind = parameter.CredentialKind,
            VisibleWhenParameterKey = parameter.VisibleWhenParameterKey,
            VisibleWhenValues = parameter.VisibleWhenValues,
            DefaultValue = parameter.Key switch
            {
                "snmp.community" => "public",
                "snmp.version" => "v2c",
                "snmp.port" => "161",
                _ => parameter.DefaultValue
            },
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
                .ToArray()
        };
    }
}
