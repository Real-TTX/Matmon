using System.Globalization;

namespace Matmon.Core.Domain;

/// <summary>Counts Windows event-log entries over WinRM, filtered by log / provider / event ID / message, and
/// reports totals + error + warning counts for the last 24 hours and 7 days as channels. A thin wrapper over the
/// PowerShell-remoting engine (like the other Windows sensors).</summary>
public sealed class WindowsEventLogSensorExecutor : ISensorExecutor
{
    private static readonly PowerShellRemoteSensorExecutor InnerExecutor = new();

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "windows-eventlog",
        DisplayName = "Windows Event Log",
        Description = "Counts Windows event-log entries (filtered by log, provider, event ID or message) over WinRM - total, error and warning counts for the last 24h and 7 days.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            .. BuildWinRmParameters(),
            new SensorParameterDefinition
            {
                Key = "eventlog.logName",
                Label = "Log",
                Kind = SensorParameterKind.Text,
                Description = "Event log to read (e.g. System, Application, Security, or a channel like Microsoft-Windows-Backup).",
                DefaultValue = "System",
                Required = true,
                Placeholder = "System"
            },
            new SensorParameterDefinition
            {
                Key = "eventlog.providerName",
                Label = "Source / provider",
                Kind = SensorParameterKind.Text,
                Description = "Optional: only count events from this provider (event source).",
                Placeholder = "Microsoft-Windows-Kernel-Power"
            },
            new SensorParameterDefinition
            {
                Key = "eventlog.eventIds",
                Label = "Event IDs",
                Kind = SensorParameterKind.Text,
                Description = "Optional: comma-separated event IDs to count (e.g. 41, 6008).",
                Placeholder = "41, 6008"
            },
            new SensorParameterDefinition
            {
                Key = "eventlog.messageMatch",
                Label = "Message contains",
                Kind = SensorParameterKind.Text,
                Description = "Optional: only count events whose message contains this text (case-insensitive).",
                Placeholder = "timeout"
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
        settings.Parameters["eventlog.logName"] = "System";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Windows Event Log",
                    context.Host,
                    settings,
                    $"WinRM port {(useSsl ? 5986 : 5985)} is open. The System event log can be counted.",
                    55)));
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var logName = MonitoringSettings.TryReadParameter(context.Settings, "eventlog.logName", out var configuredLog) &&
            !string.IsNullOrWhiteSpace(configuredLog)
                ? configuredLog.Trim()
                : "System";
        var providerName = MonitoringSettings.TryReadParameter(context.Settings, "eventlog.providerName", out var provider) &&
            !string.IsNullOrWhiteSpace(provider)
                ? provider.Trim()
                : null;
        var eventIds = ParseEventIds(MonitoringSettings.TryReadParameter(context.Settings, "eventlog.eventIds", out var ids) ? ids : null);
        var messageMatch = MonitoringSettings.TryReadParameter(context.Settings, "eventlog.messageMatch", out var match) &&
            !string.IsNullOrWhiteSpace(match)
                ? match.Trim()
                : null;

        var settings = context.Settings.Clone();
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["defaultChannelKey"] = "errors24h";
        settings.DefaultChannelKey ??= "errors24h";
        settings.Parameters["script"] = BuildScript(logName, providerName, eventIds, messageMatch);

        return InnerExecutor.ExecuteAsync(
            new SensorExecutionContext(PowerShellRemoteSensorExecutor.Definition.Key, context.Target, settings),
            cancellationToken);
    }

    private static IReadOnlyList<int> ParseEventIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : -1)
            .Where(id => id >= 0)
            .Distinct()
            .Take(50)
            .ToArray();
    }

    // Single-quoted PowerShell literal; a literal ' is escaped by doubling it.
    private static string PsLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private static string BuildScript(string logName, string? providerName, IReadOnlyList<int> eventIds, string? messageMatch)
    {
        var filterLines = new List<string> { $"$filter = @{{ LogName = {PsLiteral(logName)}; StartTime = $since7d }}" };
        if (!string.IsNullOrWhiteSpace(providerName))
        {
            filterLines.Add($"$filter['ProviderName'] = {PsLiteral(providerName)}");
        }

        if (eventIds.Count > 0)
        {
            filterLines.Add($"$filter['Id'] = @({string.Join(", ", eventIds)})");
        }

        var messageFilter = string.IsNullOrWhiteSpace(messageMatch)
            ? string.Empty
            : $"\n$events = @($events | Where-Object {{ $_.Message -and $_.Message.ToLower().Contains({PsLiteral(messageMatch.ToLowerInvariant())}) }})";

        return $$"""
$now = Get-Date
$since7d = $now.AddDays(-7)
$since24h = $now.AddHours(-24)
{{string.Join("\n", filterLines)}}
$events = @(Get-WinEvent -FilterHashtable $filter -ErrorAction SilentlyContinue){{messageFilter}}
$last24 = @($events | Where-Object { $_.TimeCreated -ge $since24h })
[pscustomobject]@{
    count24h = [double]$last24.Count
    errors24h = [double]@($last24 | Where-Object { $_.Level -eq 1 -or $_.Level -eq 2 }).Count
    warnings24h = [double]@($last24 | Where-Object { $_.Level -eq 3 }).Count
    count7d = [double]$events.Count
    errors7d = [double]@($events | Where-Object { $_.Level -eq 1 -or $_.Level -eq 2 }).Count
    warnings7d = [double]@($events | Where-Object { $_.Level -eq 3 }).Count
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
