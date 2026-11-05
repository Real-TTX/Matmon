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

    // What every script starts with: is a cmdlet / tool there at all (Has), a list of what is wrong in words ($problems),
    // numbers formatted the same on every server (Inv - a German server would write "8,2"), and a count with its noun (N).
    // The script ends with $out.message: the problems, or - when there are none - a short summary.
    public const string Prelude = """
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
function Has([string]$name) { [bool](Get-Command $name -ErrorAction SilentlyContinue) }
function Inv([string]$format) { [string]::Format([Globalization.CultureInfo]::InvariantCulture, $format, $args) }
function N([int]$count, [string]$noun) { if ($count -eq 1) { "1 $noun" } else { "$count $($noun)s" } }
function Short($items, [int]$max = 3) {
    $list = @($items | Where-Object { $_ })
    if ($list.Count -le $max) { $list -join ', ' } else { ($list[0..($max - 1)] -join ', ') + " and $($list.Count - $max) more" }
}
$problems = New-Object System.Collections.Generic.List[string]
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
if ($out.exchangeShell -eq 0) { $problems.Add("Exchange Management Shell not found on $env:COMPUTERNAME - is this an Exchange server?") }
$services = @(Get-Service -Name 'MSExchange*' -ErrorAction SilentlyContinue | Where-Object { $_.StartType -eq 'Automatic' })
$stopped = @($services | Where-Object { $_.Status -ne 'Running' })
$out.servicesStopped = $stopped.Count
$out.servicesRunning = $services.Count - $stopped.Count
if ($stopped.Count -gt 0) { $problems.Add("$(N $stopped.Count 'service') stopped: $(Short ($stopped | ForEach-Object { $_.Name }))") }
$queues = @()
if (Has 'Get-Queue') { $queues = @(Get-Queue -Server $env:COMPUTERNAME -ErrorAction SilentlyContinue) }
$total = ($queues | Measure-Object -Property MessageCount -Sum).Sum
$out.queueMessages = [int]$(if ($total) { $total } else { 0 })
$retry = @($queues | Where-Object { "$($_.Status)" -eq 'Retry' })
$out.queuesRetry = $retry.Count
if ($retry.Count -gt 0) {
    $first = $retry[0]
    $problems.Add("$(N $retry.Count 'queue') in Retry: $(Short ($retry | ForEach-Object { "$($_.Identity)" }) 2)" + $(if ($first.LastError) { " ($("$($first.LastError)".Trim()))" } else { '' }))
}
$databases = @()
if (Has 'Get-MailboxDatabase') { $databases = @(Get-MailboxDatabase -Server $env:COMPUTERNAME -Status -ErrorAction SilentlyContinue) }
$dismounted = @($databases | Where-Object { -not $_.Mounted })
$out.databases = $databases.Count
$out.databasesDismounted = $dismounted.Count
if ($dismounted.Count -gt 0) { $problems.Add("$(N $dismounted.Count 'database') dismounted: $(Short ($dismounted | ForEach-Object { $_.Name }))") }
$certificateText = ''
if (Has 'Get-ExchangeCertificate') {
    $certificates = @(Get-ExchangeCertificate -Server $env:COMPUTERNAME -ErrorAction SilentlyContinue | Where-Object { "$($_.Services)" -ne 'None' })
    if ($certificates.Count -gt 0) {
        $soonest = $certificates | Sort-Object NotAfter | Select-Object -First 1
        $out.certificateDaysLeft = [int][math]::Floor(($soonest.NotAfter - (Get-Date)).TotalDays)
        $certificateText = "certificate $($soonest.Subject -replace '^CN=([^,]+).*$', '$1') valid $($out.certificateDaysLeft) more days"
    }
}
$health = @()
if (Has 'Test-ServiceHealth') { $health = @(Test-ServiceHealth -Server $env:COMPUTERNAME -ErrorAction SilentlyContinue) }
$unhealthy = @($health | Where-Object { -not $_.RequiredServicesRunning })
$out.rolesUnhealthy = $unhealthy.Count
foreach ($role in ($unhealthy | Select-Object -First 3)) { $problems.Add("role $($role.Role): $(Short $role.ServicesNotRunning) not running") }
$out.state = if ($out.exchangeShell -eq 0 -or $out.servicesStopped -gt 0 -or $out.databasesDismounted -gt 0 -or $out.rolesUnhealthy -gt 0) { 'critical' } elseif ($out.queuesRetry -gt 0) { 'warning' } else { 'ok' }
$out.message = if ($problems.Count -gt 0) { $problems -join '; ' } else {
    ((@("$(N $out.servicesRunning 'service') running", "$(N $out.databases 'database') mounted", "$(N $out.queueMessages 'message') queued", $certificateText) | Where-Object { $_ }) -join ', ')
}
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
$stopped = @($services | Where-Object { $_.Status -ne 'Running' } | ForEach-Object { $_.Name })
$missing = @($names | Where-Object { $services.Name -notcontains $_ })
$out.servicesStopped = $stopped.Count + $missing.Count
if ($missing -contains 'NTDS') { $problems.Add("no Active Directory Domain Services on $env:COMPUTERNAME - is this a domain controller?") }
elseif ($missing.Count -gt 0) { $problems.Add("not installed: $(Short $missing)") }
if ($stopped.Count -gt 0) { $problems.Add("$(N $stopped.Count 'service') stopped: $(Short $stopped)") }
$shares = @()
if (Has 'Get-SmbShare') { $shares = @(Get-SmbShare -Name 'SYSVOL', 'NETLOGON' -ErrorAction SilentlyContinue) }
$out.sharesMissing = 2 - $shares.Count
if ($out.sharesMissing -gt 0 -and $missing -notcontains 'NTDS') {
    $problems.Add("share missing: $(Short (@('SYSVOL', 'NETLOGON') | Where-Object { $shares.Name -notcontains $_ }))")
}
$replication = @()
if (Has 'repadmin.exe') { $replication = @(repadmin /showrepl $env:COMPUTERNAME /csv 2>$null | ConvertFrom-Csv) }
$links = @($replication | Where-Object { $_.'Source DSA' })
$failing = @($links | Where-Object { [int]("0" + $_.'Number of Failures') -gt 0 })
$out.replicationLinks = $links.Count
$out.replicationFailures = $failing.Count
if ($failing.Count -gt 0) {
    $partners = @($failing | ForEach-Object { "$($_.'Source DSA') ($($_.'Naming Context' -replace ',.*$', ''): $($_.'Number of Failures') failures, last error $($_.'Last Failure Status'))" } | Select-Object -Unique)
    $problems.Add("replication failing from $(Short $partners 2)")
}
$dcdiag = @()
if (Has 'dcdiag.exe') { $dcdiag = @(dcdiag /q /s:$env:COMPUTERNAME 2>$null) }
$failedTests = @($dcdiag | ForEach-Object { if ("$_" -match 'failed test\s+(\S+)') { $Matches[1] } } | Select-Object -Unique)
$out.dcdiagFailedTests = $failedTests.Count
if ($failedTests.Count -gt 0) { $problems.Add("dcdiag failed: $(Short $failedTests 4)") }
$timeText = ''
if ((Has 'Get-ADDomain') -and (Has 'w32tm.exe')) {
    $pdc = (Get-ADDomain -ErrorAction SilentlyContinue).PDCEmulator
    if ($pdc) {
        $sample = w32tm /stripchart /computer:$pdc /samples:1 /dataonly 2>$null | Select-Object -Last 1
        if ("$sample" -match ',\s*([+-]?\d+(\.\d+)?)s') {
            $out.timeOffsetSeconds = [math]::Round([math]::Abs([double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)), 2)
            $timeText = Inv 'time {0:0.##} s off the PDC ({1})' $out.timeOffsetSeconds ($pdc -replace '\..*$', '')
        }
    }
}
$out.state = if ($out.servicesStopped -gt 0 -or $out.sharesMissing -gt 0 -or $out.replicationFailures -gt 0) { 'critical' } elseif ($out.dcdiagFailedTests -gt 0) { 'warning' } else { 'ok' }
$out.message = if ($problems.Count -gt 0) { $problems -join '; ' } else {
    ((@('AD services running', "$(N $links.Count 'replication link') healthy", $(if (Has 'dcdiag.exe') { 'dcdiag passed' } else { '' }), $timeText) | Where-Object { $_ }) -join ', ')
}
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
$names = 'TFSJobAgent', 'W3SVC', 'WAS'
$services = @(Get-Service -Name $names -ErrorAction SilentlyContinue)
$stopped = @($services | Where-Object { $_.Status -ne 'Running' } | ForEach-Object { $_.Name })
$out.servicesStopped = $stopped.Count + @($names | Where-Object { $services.Name -notcontains $_ }).Count
if ($services.Name -notcontains 'TFSJobAgent') { $problems.Add("no TFS Job Agent on $env:COMPUTERNAME - is this the Azure DevOps application tier?") }
if ($stopped.Count -gt 0) { $problems.Add("$(N $stopped.Count 'service') stopped: $(Short $stopped)") }
$poolText = ''
if (Get-Module -ListAvailable -Name WebAdministration) {
    try {
        Import-Module WebAdministration -ErrorAction Stop
        $pools = @(Get-ChildItem IIS:\AppPools | Where-Object { $_.Name -match 'Azure DevOps|Team Foundation|TFS' })
        $stoppedPools = @($pools | Where-Object { $_.State -ne 'Started' } | ForEach-Object { $_.Name })
        $out.appPools = $pools.Count
        $out.appPoolsStopped = $stoppedPools.Count
        if ($stoppedPools.Count -gt 0) { $problems.Add("application pool stopped: $(Short $stoppedPools)") }
        $poolText = "$(N $pools.Count 'application pool') started"
    } catch { }
}
$address = $url.TrimEnd('/') + '/_apis/connectionData'
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    $response = Invoke-WebRequest -Uri $address -UseDefaultCredentials -UseBasicParsing -TimeoutSec 30
    $out.webOk = [int]($response.StatusCode -eq 200)
    if ($out.webOk -eq 0) { $problems.Add("$address answered HTTP $($response.StatusCode)") }
} catch {
    $out.webOk = 0
    $reason = if ($_.Exception.Response) { "HTTP $([int]$_.Exception.Response.StatusCode)" } else { "$($_.Exception.Message)".Trim() }
    $problems.Add("no answer from $address`: $reason")
}
$out.webResponseMs = [int]$watch.ElapsedMilliseconds
$out.state = if ($out.servicesStopped -gt 0 -or $out.webOk -eq 0 -or $out.appPoolsStopped -gt 0) { 'critical' } else { 'ok' }
$out.message = if ($problems.Count -gt 0) { $problems -join '; ' } else {
    ((@("web tier answers in $($out.webResponseMs) ms", $poolText, 'job agent and IIS running') | Where-Object { $_ }) -join ', ')
}
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
