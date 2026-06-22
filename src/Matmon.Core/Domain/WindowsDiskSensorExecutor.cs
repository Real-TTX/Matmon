using System.Globalization;

namespace Matmon.Core.Domain;

/// <summary>
/// Detailed per-disk health of a Windows host over WinRM. Where the compact Windows Health
/// sensor only rolls up a single SMART summary, this emits one status (+ temperature, + SSD
/// wear) channel per physical disk and a free-space channel per volume, plus an overall
/// SMART status. It is a thin wrapper over the generic <see cref="PowerShellRemoteSensorExecutor"/>
/// engine with a fixed disk-discovery script, so it reuses the battle-tested WinRM remoting.
/// </summary>
public sealed class WindowsDiskSensorExecutor : ISensorExecutor
{
    private static readonly PowerShellRemoteSensorExecutor Inner = new();

    // Emits a flat object: one entry per physical disk + volume, an overall smartStatus,
    // and a 'state' string the generic engine maps to the sensor state.
    private const string DiskScript = """
$ErrorActionPreference = 'SilentlyContinue'
$out = [ordered]@{}
$worst = 0
$maxTemp = $null
$i = 0
foreach ($d in (Get-PhysicalDisk)) {
    $i++
    $sev = 0
    if ($d.HealthStatus -eq 'Unhealthy') { $sev = 2 }
    elseif ($d.HealthStatus -eq 'Warning') { $sev = 1 }
    if ($d.OperationalStatus -and $d.OperationalStatus -ne 'OK' -and $sev -lt 1) { $sev = 1 }
    if ($sev -gt $worst) { $worst = $sev }
    $out["disk${i}Status"] = $sev
    $rc = $d | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
    if ($rc -and $null -ne $rc.Temperature -and $rc.Temperature -gt 0) {
        $t = [double]$rc.Temperature
        $out["disk${i}Temp"] = [math]::Round($t, 1)
        if ($null -eq $maxTemp -or $t -gt $maxTemp) { $maxTemp = $t }
    }
    if ($rc -and $null -ne $rc.Wear -and $rc.Wear -gt 0) {
        $out["disk${i}Wear"] = [double]$rc.Wear
    }
}
foreach ($v in (Get-Volume | Where-Object { $_.DriveLetter -and $_.Size -gt 0 })) {
    $out["volume$($v.DriveLetter)FreePercent"] = [math]::Round(($v.SizeRemaining / $v.Size) * 100, 1)
}
$out["diskCount"] = $i
$out["smartStatus"] = $worst
if ($null -ne $maxTemp) { $out["maxTemperature"] = [math]::Round($maxTemp, 1) }
$out["state"] = if ($worst -ge 2) { 'critical' } elseif ($worst -ge 1) { 'warning' } else { 'ok' }
[pscustomobject]$out
""";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "windows-disk",
        DisplayName = "Windows Disk",
        Description = "Per-disk SMART/health, temperature and SSD wear plus per-volume free space of a Windows host over WinRM.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = PowerShellRemoteSensorExecutor.Definition.Parameters
            .Where(parameter => parameter.Key.StartsWith("winrm.", StringComparison.OrdinalIgnoreCase))
            .ToArray()
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var suggestions = new List<SensorDiscoverySuggestion>();
        if (context.OpenTcpPorts.Contains(5985))
        {
            suggestions.Add(BuildSuggestion(port: 5985, useSsl: false));
        }

        if (context.OpenTcpPorts.Contains(5986))
        {
            suggestions.Add(BuildSuggestion(port: 5986, useSsl: true));
        }

        return ValueTask.FromResult(
            suggestions.Count == 0
                ? SensorDiscoveryCheckResult.NotAvailable
                : SensorDiscoveryCheckResult.Available([.. suggestions]));
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        // Delegate to the generic PowerShell engine with our fixed disk script injected.
        var settings = context.Settings.Clone();
        settings.Parameters["script"] = DiskScript;
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["defaultChannelKey"] = "smartStatus";

        var inner = new SensorExecutionContext(PowerShellRemoteSensorExecutor.Definition.Key, context.Target, settings);
        return Inner.ExecuteAsync(inner, cancellationToken);
    }

    private static SensorDiscoverySuggestion BuildSuggestion(int port, bool useSsl)
    {
        var settings = new MonitoringSettings
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultChannelKey = "smartStatus"
        };
        settings.Parameters["winrm.port"] = port.ToString(CultureInfo.InvariantCulture);
        settings.Parameters["winrm.useSsl"] = useSsl ? "true" : "false";
        MonitoringSettings.SetChannelThreshold(settings, "smartStatus", "warning", new ThresholdRule(ThresholdDirection.Above, 0));
        MonitoringSettings.SetChannelThreshold(settings, "smartStatus", "critical", new ThresholdRule(ThresholdDirection.Above, 1));

        return new SensorDiscoverySuggestion(
            Definition.Key,
            useSsl ? "Windows Disk (WinRM HTTPS)" : "Windows Disk",
            string.Empty,
            settings,
            $"WinRM port {port} is open. Windows credentials can be inherited from the parent.",
            68);
    }
}
