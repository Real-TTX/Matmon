namespace Matmon.Core.Domain;

public sealed class DiskSmartSensorExecutor : ISensorExecutor
{
    private static readonly PowerShellRemoteSensorExecutor InnerExecutor = new();

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "disk-smart",
        DisplayName = "Disk SMART",
        Description = "Checks physical disk health over Windows PowerShell remoting.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters = BuildWinRmParameters()
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

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Disk SMART",
                    context.Host,
                    settings,
                    $"WinRM port {(useSsl ? 5986 : 5985)} is open. Disk health can be checked.",
                    66)));
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = context.Settings.Clone();
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["defaultChannelKey"] = "healthy";
        settings.DefaultChannelKey ??= "healthy";
        settings.Parameters["script"] = """
$disks = @(Get-PhysicalDisk -ErrorAction SilentlyContinue)
if ($disks.Count -eq 0) {
    $diskDrives = @(Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue)
    $unhealthy = @($diskDrives | Where-Object { $_.Status -and $_.Status -notin @('OK', 'Healthy') }).Count
    $healthy = if ($unhealthy -eq 0) { 1 } else { 0 }
    [pscustomobject]@{
        healthy = [double]$healthy
        diskCount = [double]$diskDrives.Count
        unhealthyDisks = [double]$unhealthy
    }
} else {
    $unhealthy = @($disks | Where-Object { $_.HealthStatus -notin @('Healthy', 'OK') }).Count
    $healthy = if ($unhealthy -eq 0) { 1 } else { 0 }
    [pscustomobject]@{
        healthy = [double]$healthy
        diskCount = [double]$disks.Count
        unhealthyDisks = [double]$unhealthy
    }
}
""";

        return InnerExecutor.ExecuteAsync(
            new SensorExecutionContext(PowerShellRemoteSensorExecutor.Definition.Key, context.Target, settings),
            cancellationToken);
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
}
