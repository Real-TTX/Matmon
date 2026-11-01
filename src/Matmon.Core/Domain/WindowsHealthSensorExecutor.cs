using System.Globalization;

namespace Matmon.Core.Domain;

/// <summary>
/// Compact overall health of a Windows host over WinRM: CPU load, memory used %, disk used %
/// and a single SMART summary. A dedicated sensor type (so "Windows Health" sits alongside
/// Linux/Synology/UniFi Health) that is a thin wrapper over the generic
/// <see cref="PowerShellRemoteSensorExecutor"/> engine with the fixed Windows-health script.
/// </summary>
public sealed class WindowsHealthSensorExecutor : ISensorExecutor
{
    private static readonly PowerShellRemoteSensorExecutor Inner = new();

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "windows-health",
        DisplayName = "Windows Health",
        Description = "Overall Windows health over WinRM - CPU, memory, disk usage and a SMART summary.",
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

        // An agent on this Windows machine: the health script runs locally - no WinRM, no credential.
        if (context.LocalWindows)
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.Available(BuildSuggestion(port: null, useSsl: false)));
        }

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
        var settings = context.Settings.Clone();
        settings.Parameters["script"] = PowerShellRemoteSensorExecutor.WindowsHealthDiscoveryScript;
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["defaultChannelKey"] = "cpuLoad";

        var inner = new SensorExecutionContext(PowerShellRemoteSensorExecutor.Definition.Key, context.Target, settings);
        return Inner.ExecuteAsync(inner, cancellationToken);
    }

    private static SensorDiscoverySuggestion BuildSuggestion(int? port, bool useSsl)
    {
        var settings = new MonitoringSettings
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultChannelKey = "cpuLoad"
        };
        if (port is int winrmPort)
        {
            settings.Parameters["winrm.port"] = winrmPort.ToString(CultureInfo.InvariantCulture);
            settings.Parameters["winrm.useSsl"] = useSsl ? "true" : "false";
        }

        foreach (var channel in new[] { "cpuLoad", "memoryUsedPercent", "diskUsedPercent" })
        {
            MonitoringSettings.SetChannelThreshold(settings, channel, "warning", new ThresholdRule(ThresholdDirection.Above, 85));
            MonitoringSettings.SetChannelThreshold(settings, channel, "critical", new ThresholdRule(ThresholdDirection.Above, 95));
        }

        MonitoringSettings.SetChannelThreshold(settings, "smartStatus", "warning", new ThresholdRule(ThresholdDirection.Above, 0));
        MonitoringSettings.SetChannelThreshold(settings, "smartStatus", "critical", new ThresholdRule(ThresholdDirection.Above, 1));

        return new SensorDiscoverySuggestion(
            Definition.Key,
            port is null ? "Windows Health" : useSsl ? "Windows Health (WinRM HTTPS)" : "Windows Health",
            port is null ? SensorDiscoveryContext.ThisMachine : string.Empty,
            settings,
            port is null
                ? "Runs on this machine through the agent - no WinRM, no credential."
                : $"WinRM port {port} is open. Windows credentials can be inherited from the parent.",
            88);
    }
}
