namespace Matmon.Core.Domain;

public sealed class WindowsProcessSensorExecutor : ISensorExecutor
{
    private static readonly PowerShellRemoteSensorExecutor InnerExecutor = new();

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "windows-process",
        DisplayName = "Windows Process",
        Description = "Checks that a named Windows process is running (instance count) and its CPU / memory usage, over WinRM.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            .. BuildWinRmParameters(),
            new SensorParameterDefinition
            {
                Key = "windows.processName",
                Label = "Process name",
                Kind = SensorParameterKind.Text,
                Description = "Process name without .exe, for example w3wp or sqlservr.",
                Required = true,
                Placeholder = "w3wp"
            },
            new SensorParameterDefinition
            {
                Key = "windows.processMinCount",
                Label = "Min count",
                Kind = SensorParameterKind.Integer,
                Description = "Minimum expected process count",
                DefaultValue = "1",
                Min = 0,
                Max = 10000,
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

        if (!context.OpenTcpPorts.Contains(5985) && !context.OpenTcpPorts.Contains(5986))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var useSsl = context.OpenTcpPorts.Contains(5986);
        var settings = new MonitoringSettings();
        settings.Parameters["winrm.port"] = useSsl ? "5986" : "5985";
        settings.Parameters["winrm.useSsl"] = useSsl ? "true" : "false";
        settings.Parameters["windows.processMinCount"] = "1";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Windows Process",
                    context.Host,
                    settings,
                    $"WinRM port {(useSsl ? 5986 : 5985)} is open. Pick a process name.",
                    68)));
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!MonitoringSettings.TryReadParameter(context.Settings, "windows.processName", out var processName) ||
            string.IsNullOrWhiteSpace(processName))
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "process name is required"));
        }

        var minCount = MonitoringSettings.TryReadParameterInt(context.Settings, "windows.processMinCount", out var configuredMinCount)
            ? Math.Max(configuredMinCount, 0)
            : 1;

        var settings = context.Settings.Clone();
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["defaultChannelKey"] = "processCount";
        settings.DefaultChannelKey ??= "processCount";
        settings.Parameters["script"] = BuildScript(processName.Trim(), minCount);
        return InnerExecutor.ExecuteAsync(
            new SensorExecutionContext(PowerShellRemoteSensorExecutor.Definition.Key, context.Target, settings),
            cancellationToken);
    }

    private static string BuildScript(string processName, int minCount)
    {
        return $$"""
$processName = '{{EscapePowerShellSingleQuoted(processName)}}'
$minCount = {{minCount}}
$processes = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
$count = $processes.Count
$workingSetMb = if ($count -gt 0) { ($processes | Measure-Object -Property WorkingSet64 -Sum).Sum / 1MB } else { 0 }
$cpuSeconds = if ($count -gt 0) { ($processes | Measure-Object -Property CPU -Sum).Sum } else { 0 }
$countOk = if ($count -ge $minCount) { 1 } else { 0 }
[pscustomobject]@{
    processCount = [double]$count
    countOk = [double]$countOk
    workingSetMb = [math]::Round([double]$workingSetMb, 2)
    cpuSeconds = [math]::Round([double]$cpuSeconds, 2)
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
