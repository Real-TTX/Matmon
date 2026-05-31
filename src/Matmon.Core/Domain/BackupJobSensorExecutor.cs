namespace Matmon.Core.Domain;

public sealed class BackupJobSensorExecutor : ISensorExecutor
{
    private static readonly PowerShellRemoteSensorExecutor InnerExecutor = new();

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "backup-job",
        DisplayName = "Backup Job",
        Description = "Checks recent Windows backup events or a custom PowerShell backup status script over WinRM.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            .. BuildWinRmParameters(),
            new SensorParameterDefinition
            {
                Key = "backup.mode",
                Label = "Mode",
                Kind = SensorParameterKind.ValueList,
                Description = "Backup source",
                DefaultValue = "windows-eventlog",
                Options =
                [
                    new SensorParameterOption { Value = "windows-eventlog", Label = "Windows Server Backup Event Log" },
                    new SensorParameterOption { Value = "custom-powershell", Label = "Custom PowerShell" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "backup.lookbackHours",
                Label = "Lookback hours",
                Kind = SensorParameterKind.Integer,
                Description = "Maximum allowed age of the newest backup event.",
                DefaultValue = "48",
                Min = 1,
                Max = 8760,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "backup.script",
                Label = "Custom script",
                Kind = SensorParameterKind.Multiline,
                Description = "Custom script. Emit JSON with numeric channels like success and ageHours.",
                VisibleWhenParameterKey = "backup.mode",
                VisibleWhenValues = ["custom-powershell"],
                Placeholder = "[pscustomobject]@{ success = 1; ageHours = 2 }"
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
        settings.Parameters["backup.mode"] = "windows-eventlog";
        settings.Parameters["backup.lookbackHours"] = "48";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Backup Job",
                    context.Host,
                    settings,
                    $"WinRM port {(useSsl ? 5986 : 5985)} is open. Backup status can be checked.",
                    58)));
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var mode = MonitoringSettings.TryReadParameter(context.Settings, "backup.mode", out var configuredMode)
            ? configuredMode.Trim().ToLowerInvariant()
            : "windows-eventlog";
        var lookbackHours = MonitoringSettings.TryReadParameterInt(context.Settings, "backup.lookbackHours", out var configuredLookback)
            ? Math.Clamp(configuredLookback, 1, 8760)
            : 48;

        var settings = context.Settings.Clone();
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["defaultChannelKey"] = "success";
        settings.DefaultChannelKey ??= "success";

        if (mode == "custom-powershell")
        {
            if (!MonitoringSettings.TryReadParameter(context.Settings, "backup.script", out var customScript) ||
                string.IsNullOrWhiteSpace(customScript))
            {
                return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "custom backup script is required"));
            }

            settings.Parameters["script"] = customScript;
        }
        else
        {
            settings.Parameters["script"] = BuildWindowsEventLogScript(lookbackHours);
        }

        return InnerExecutor.ExecuteAsync(
            new SensorExecutionContext(PowerShellRemoteSensorExecutor.Definition.Key, context.Target, settings),
            cancellationToken);
    }

    private static string BuildWindowsEventLogScript(int lookbackHours)
    {
        return $$"""
$lookbackHours = {{lookbackHours}}
$since = (Get-Date).AddHours(-$lookbackHours)
$idsSuccess = @(4, 14, 49)
$idsFailure = @(5, 8, 9, 19, 20, 21, 50, 52, 517, 518, 521)
$events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Backup'; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $idsSuccess -contains $_.Id -or $idsFailure -contains $_.Id } |
    Sort-Object TimeCreated -Descending)
$latest = $events | Select-Object -First 1
$success = if ($latest -and $idsSuccess -contains $latest.Id) { 1 } else { 0 }
$ageHours = if ($latest) { [math]::Round(((Get-Date) - $latest.TimeCreated).TotalHours, 2) } else { $lookbackHours + 1 }
$failedEvents = @($events | Where-Object { $idsFailure -contains $_.Id }).Count
[pscustomobject]@{
    success = [double]$success
    ageHours = [double]$ageHours
    failedEvents = [double]$failedEvents
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
}
