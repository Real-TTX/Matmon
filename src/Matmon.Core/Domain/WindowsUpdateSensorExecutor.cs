namespace Matmon.Core.Domain;

/// <summary>
/// Pending Windows Updates on a host over WinRM. Thin wrapper over the generic
/// <see cref="PowerShellRemoteSensorExecutor"/> engine running a fixed script that queries the
/// Windows Update Agent COM API (<c>Microsoft.Update.Session</c>) for not-yet-installed updates.
/// </summary>
public sealed class WindowsUpdateSensorExecutor : ISensorExecutor
{
    private static readonly PowerShellRemoteSensorExecutor Inner = new();

    // Emits pendingUpdates / securityUpdates counts and a 'state' string the engine maps to the
    // sensor state. The WU search can be slow, so the sensor defaults to a calm poll cadence.
    private const string UpdateScript = """
$ErrorActionPreference = 'Stop'
$out = [ordered]@{}
try {
    $session = New-Object -ComObject Microsoft.Update.Session
    $searcher = $session.CreateUpdateSearcher()
    $result = $searcher.Search("IsInstalled=0 and IsHidden=0")
    $total = [int]$result.Updates.Count
    $security = 0
    foreach ($u in $result.Updates) {
        foreach ($c in $u.Categories) {
            if ($c.Name -match 'Security') { $security++; break }
        }
    }
    $out["pendingUpdates"] = $total
    $out["securityUpdates"] = $security
    $out["state"] = if ($total -gt 0) { 'warning' } else { 'ok' }
} catch {
    $out["pendingUpdates"] = -1
    $out["state"] = 'critical'
}
[pscustomobject]$out
""";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "windows-update",
        DisplayName = "Windows Update",
        Description = "Counts pending (not-yet-installed) Windows updates on a host over WinRM via the Windows Update Agent COM API.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters = PowerShellRemoteSensorExecutor.Definition.Parameters
            .Where(parameter => parameter.Key.StartsWith("winrm.", StringComparison.OrdinalIgnoreCase))
            .ToArray()
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        _ = cancellationToken;
        return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = context.Settings.Clone();
        settings.Parameters["script"] = UpdateScript;
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["defaultChannelKey"] = "pendingUpdates";

        var inner = new SensorExecutionContext(PowerShellRemoteSensorExecutor.Definition.Key, context.Target, settings);
        return Inner.ExecuteAsync(inner, cancellationToken);
    }
}
