namespace Matmon.Core.Domain;

/// <summary>
/// Health of a Windows server ROLE - Exchange Server, an Active Directory domain controller, Azure DevOps Server - as a sensor of its
/// own. Each is a thin wrapper over the generic <see cref="PowerShellRemoteSensorExecutor"/> engine with a fixed script (the pattern of
/// <see cref="WindowsDiskSensorExecutor"/>): WinRM to the server, or locally when a Matmon agent runs on it (target = the machine itself).
/// The scripts return an object (the engine turns it into JSON) whose numbers are the channels and whose <c>state</c> is the sensor
/// state; a cmdlet or tool that is missing on the target is checked for first - inside the engine's try a missing command would
/// end the whole script instead of leaving one reading out. Default thresholds live in <see cref="SensorThresholdDefaults"/>.
/// Run through the engine locally (WindowsRoleHealthSensorTests); NOT verified against a real Exchange server, domain controller or
/// Azure DevOps Server.
/// </summary>
internal static class WindowsRoleHealth
{
    private static readonly PowerShellRemoteSensorExecutor Engine = new();

    public static SensorParameterDefinition[] WinRmParameters(params SensorParameterDefinition[] extra) =>
        [.. PowerShellRemoteSensorExecutor.Definition.Parameters.Where(parameter => parameter.Key.StartsWith("winrm.", StringComparison.OrdinalIgnoreCase)), .. extra];

    public static ValueTask<SensorExecutionResult> RunAsync(SensorExecutionContext context, string script, string defaultChannel, CancellationToken cancellationToken)
    {
        var settings = context.Settings.Clone();
        settings.Parameters["script"] = script;
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["failOnStderr"] = "false";
        settings.Parameters["defaultChannelKey"] = defaultChannel;
        return Engine.ExecuteAsync(new SensorExecutionContext(PowerShellRemoteSensorExecutor.Definition.Key, context.Target, settings), cancellationToken);
    }

    // A small helper every script starts with: is a cmdlet / tool there at all?
    public const string Prelude = """
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
function Has([string]$name) { [bool](Get-Command $name -ErrorAction SilentlyContinue) }
$out = [ordered]@{}
""";
}

/// <summary>Exchange Server (on-premises): services, mail queues, mounted databases, role health, certificate expiry.</summary>
public sealed class ExchangeHealthSensorExecutor : ISensorExecutor
{
    public const string Script = WindowsRoleHealth.Prelude + "\n" + """
if (-not (Has 'Get-Queue')) {
    try { Add-PSSnapin Microsoft.Exchange.Management.PowerShell.SnapIn -ErrorAction Stop } catch { }
}
$out.exchangeShell = [int](Has 'Get-Queue')
$services = @(Get-Service -Name 'MSExchange*' -ErrorAction SilentlyContinue | Where-Object { $_.StartType -eq 'Automatic' })
$out.servicesStopped = @($services | Where-Object { $_.Status -ne 'Running' }).Count
$out.servicesRunning = @($services | Where-Object { $_.Status -eq 'Running' }).Count
$queues = @()
if (Has 'Get-Queue') { $queues = @(Get-Queue -Server $env:COMPUTERNAME -ErrorAction SilentlyContinue) }
$total = ($queues | Measure-Object -Property MessageCount -Sum).Sum
$out.queueMessages = [int]$(if ($total) { $total } else { 0 })
$out.queuesRetry = @($queues | Where-Object { "$($_.Status)" -eq 'Retry' }).Count
$databases = @()
if (Has 'Get-MailboxDatabase') { $databases = @(Get-MailboxDatabase -Server $env:COMPUTERNAME -Status -ErrorAction SilentlyContinue) }
$out.databases = $databases.Count
$out.databasesDismounted = @($databases | Where-Object { -not $_.Mounted }).Count
if (Has 'Get-ExchangeCertificate') {
    $certificates = @(Get-ExchangeCertificate -Server $env:COMPUTERNAME -ErrorAction SilentlyContinue | Where-Object { "$($_.Services)" -ne 'None' })
    if ($certificates.Count -gt 0) {
        $out.certificateDaysLeft = [int](($certificates | ForEach-Object { ($_.NotAfter - (Get-Date)).TotalDays } | Measure-Object -Minimum).Minimum)
    }
}
$health = @()
if (Has 'Test-ServiceHealth') { $health = @(Test-ServiceHealth -Server $env:COMPUTERNAME -ErrorAction SilentlyContinue) }
$out.rolesUnhealthy = @($health | Where-Object { -not $_.RequiredServicesRunning }).Count
$out.state = if ($out.exchangeShell -eq 0 -or $out.servicesStopped -gt 0 -or $out.databasesDismounted -gt 0 -or $out.rolesUnhealthy -gt 0) { 'critical' } elseif ($out.queuesRetry -gt 0) { 'warning' } else { 'ok' }
[pscustomobject]$out
""";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "exchange-health",
        DisplayName = "Exchange Server Health",
        Description = "Exchange Server (on-premises) over WinRM or a local agent: Exchange services stopped, messages in the queues and queues in Retry, mailbox databases dismounted, roles whose required services are not running (Test-ServiceHealth) and the days left on the soonest-expiring service certificate. Needs an account in \"View-Only Organization Management\".",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = WindowsRoleHealth.WinRmParameters()
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);

    public ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default) =>
        WindowsRoleHealth.RunAsync(context, Script, "queueMessages", cancellationToken);
}

/// <summary>Active Directory domain controller: AD services, SYSVOL / NETLOGON, replication failures, dcdiag, time offset to the PDC.</summary>
public sealed class ActiveDirectoryHealthSensorExecutor : ISensorExecutor
{
    public const string Script = WindowsRoleHealth.Prelude + "\n" + """
$names = 'NTDS', 'DNS', 'Netlogon', 'kdc', 'W32Time', 'DFSR'
$services = @(Get-Service -Name $names -ErrorAction SilentlyContinue)
$out.servicesStopped = @($services | Where-Object { $_.Status -ne 'Running' }).Count + ($names.Count - $services.Count)
$shares = @()
if (Has 'Get-SmbShare') { $shares = @(Get-SmbShare -Name 'SYSVOL', 'NETLOGON' -ErrorAction SilentlyContinue) }
$out.sharesMissing = 2 - $shares.Count
$replication = @()
if (Has 'repadmin.exe') { $replication = @(repadmin /showrepl $env:COMPUTERNAME /csv 2>$null | ConvertFrom-Csv) }
$out.replicationLinks = @($replication | Where-Object { $_.'Source DSA' }).Count
$out.replicationFailures = @($replication | Where-Object { [int]("0" + $_.'Number of Failures') -gt 0 }).Count
$dcdiag = @()
if (Has 'dcdiag.exe') { $dcdiag = @(dcdiag /q /s:$env:COMPUTERNAME 2>$null) }
$out.dcdiagFailedTests = @($dcdiag | Where-Object { $_ -match 'failed test' }).Count
if ((Has 'Get-ADDomain') -and (Has 'w32tm.exe')) {
    $pdc = (Get-ADDomain -ErrorAction SilentlyContinue).PDCEmulator
    if ($pdc) {
        $sample = w32tm /stripchart /computer:$pdc /samples:1 /dataonly 2>$null | Select-Object -Last 1
        if ("$sample" -match ',\s*([+-]?\d+(\.\d+)?)s') { $out.timeOffsetSeconds = [math]::Round([math]::Abs([double]$Matches[1]), 2) }
    }
}
$out.state = if ($out.servicesStopped -gt 0 -or $out.sharesMissing -gt 0 -or $out.replicationFailures -gt 0) { 'critical' } elseif ($out.dcdiagFailedTests -gt 0) { 'warning' } else { 'ok' }
[pscustomobject]$out
""";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "ad-health",
        DisplayName = "Active Directory Health",
        Description = "A domain controller over WinRM or a local agent: stopped AD services (NTDS, DNS, Netlogon, KDC, W32Time, DFSR), missing SYSVOL / NETLOGON shares, replication links with failures (repadmin), failed dcdiag tests and the time offset to the PDC emulator. One sensor per domain controller.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = WindowsRoleHealth.WinRmParameters()
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);

    public ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default) =>
        WindowsRoleHealth.RunAsync(context, Script, "replicationFailures", cancellationToken);
}

/// <summary>Azure DevOps Server / TFS (on-premises application tier): job agent and IIS, its application pools, whether the web tier answers.</summary>
public sealed class AzureDevOpsServerHealthSensorExecutor : ISensorExecutor
{
    private const string UrlPlaceholder = "__MATMON_DEVOPS_URL__";

    public const string Script = WindowsRoleHealth.Prelude + "\n" + """
$url = '__MATMON_DEVOPS_URL__'
$services = @(Get-Service -Name 'TFSJobAgent', 'W3SVC', 'WAS' -ErrorAction SilentlyContinue)
$out.servicesStopped = @($services | Where-Object { $_.Status -ne 'Running' }).Count
if (Get-Module -ListAvailable -Name WebAdministration) {
    try {
        Import-Module WebAdministration -ErrorAction Stop
        $pools = @(Get-ChildItem IIS:\AppPools | Where-Object { $_.Name -match 'Azure DevOps|Team Foundation|TFS' })
        $out.appPools = $pools.Count
        $out.appPoolsStopped = @($pools | Where-Object { $_.State -ne 'Started' }).Count
    } catch { }
}
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    $response = Invoke-WebRequest -Uri ($url.TrimEnd('/') + '/_apis/connectionData') -UseDefaultCredentials -UseBasicParsing -TimeoutSec 30
    $out.webOk = [int]($response.StatusCode -eq 200)
} catch { $out.webOk = 0 }
$out.webResponseMs = [int]$watch.ElapsedMilliseconds
$out.state = if ($out.servicesStopped -gt 0 -or $out.webOk -eq 0 -or $out.appPoolsStopped -gt 0) { 'critical' } else { 'ok' }
[pscustomobject]$out
""";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "azure-devops-health",
        DisplayName = "Azure DevOps Server Health",
        Description = "Azure DevOps Server / TFS application tier over WinRM or a local agent: TFS Job Agent and IIS services, the Azure DevOps application pools, and whether the web tier answers (_apis/connectionData with the account's own sign-in) and how fast. For the database tier add SQL Server Health.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = WindowsRoleHealth.WinRmParameters(new SensorParameterDefinition
        {
            Key = "devops.url",
            Label = "Server address",
            Kind = SensorParameterKind.Text,
            DefaultValue = "http://localhost/",
            Placeholder = "http://localhost:8080/tfs/",
            Description = "The address of Azure DevOps Server as the application tier itself reaches it (e.g. http://localhost/ or http://localhost:8080/tfs/)."
        })
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);

    public ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        var url = MonitoringSettings.TryReadParameter(context.Settings, "devops.url", out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : "http://localhost/";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "the server address must be an http or https address"));
        }

        // Into a single-quoted PowerShell string: a quote is doubled, nothing else is special there.
        return WindowsRoleHealth.RunAsync(context, Script.Replace(UrlPlaceholder, uri.ToString().Replace("'", "''"), StringComparison.Ordinal),
            "webResponseMs", cancellationToken);
    }
}
