namespace Matmon.Core.Domain;

public sealed class WindowsServiceSensorExecutor : ISensorExecutor
{
    private static readonly PowerShellRemoteSensorExecutor InnerExecutor = new();

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "windows-service",
        DisplayName = "Windows Service",
        Description = "Checks whether a named Windows service exists and is in its expected state (running/stopped) over WinRM.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            .. BuildWinRmParameters(),
            new SensorParameterDefinition
            {
                Key = "windows.serviceName",
                Label = "Service name",
                Kind = SensorParameterKind.Text,
                Description = "Windows service name, for example Spooler or MSSQLSERVER.",
                Required = true,
                Placeholder = "Spooler"
            },
            new SensorParameterDefinition
            {
                Key = "windows.serviceExpectedState",
                Label = "Expected state",
                Kind = SensorParameterKind.ValueList,
                Description = "Expected service state",
                DefaultValue = "Running",
                Options =
                [
                    new SensorParameterOption { Value = "Running", Label = "Running" },
                    new SensorParameterOption { Value = "Stopped", Label = "Stopped" }
                ]
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!context.OpenTcpPorts.Contains(5985) && !context.OpenTcpPorts.Contains(5986))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var useSsl = context.OpenTcpPorts.Contains(5986);
        var settings = new MonitoringSettings();
        settings.Parameters["winrm.port"] = useSsl ? "5986" : "5985";
        settings.Parameters["winrm.useSsl"] = useSsl ? "true" : "false";
        settings.Parameters["windows.serviceExpectedState"] = "Running";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Windows Service",
                    context.Host,
                    settings,
                    $"WinRM port {(useSsl ? 5986 : 5985)} is open. Pick a service name.",
                    70)));
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!MonitoringSettings.TryReadParameter(context.Settings, "windows.serviceName", out var serviceName) ||
            string.IsNullOrWhiteSpace(serviceName))
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "service name is required"));
        }

        var expectedState = MonitoringSettings.TryReadParameter(context.Settings, "windows.serviceExpectedState", out var configuredExpectedState) &&
            !string.IsNullOrWhiteSpace(configuredExpectedState)
            ? configuredExpectedState.Trim()
            : "Running";

        var settings = context.Settings.Clone();
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["defaultChannelKey"] = "stateOk";
        settings.DefaultChannelKey ??= "stateOk";
        settings.Parameters["script"] = BuildScript(serviceName.Trim(), expectedState);
        return InnerExecutor.ExecuteAsync(
            new SensorExecutionContext(PowerShellRemoteSensorExecutor.Definition.Key, context.Target, settings),
            cancellationToken);
    }

    private static string BuildScript(string serviceName, string expectedState)
    {
        return $$"""
$serviceName = '{{EscapePowerShellSingleQuoted(serviceName)}}'
$expectedState = '{{EscapePowerShellSingleQuoted(expectedState)}}'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$exists = if ($service) { 1 } else { 0 }
$running = if ($service -and $service.Status -eq 'Running') { 1 } else { 0 }
$stateOk = if ($service -and $service.Status.ToString() -eq $expectedState) { 1 } else { 0 }
[pscustomobject]@{
    exists = [double]$exists
    running = [double]$running
    stateOk = [double]$stateOk
}
""";
    }

    private static IReadOnlyList<SensorParameterDefinition> BuildWinRmParameters()
    {
        return PowerShellRemoteSensorExecutor.Definition.Parameters
            .Where(parameter => parameter.Key.StartsWith("winrm.", StringComparison.OrdinalIgnoreCase))
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

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
