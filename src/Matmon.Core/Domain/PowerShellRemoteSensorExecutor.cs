using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Matmon.Core.Domain;

public sealed class PowerShellRemoteSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "powershell",
        DisplayName = "PowerShell Remote",
        Description = "Run a PowerShell script on a remote Windows host over WinRM and extract numeric channels from JSON, XML, regex or text output.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "winrm.username",
                Label = "Windows username",
                Kind = SensorParameterKind.Text,
                Description = "Remote account on the Windows host",
                Required = true,
                Placeholder = "monitor",
                CredentialKind = MonitoringCredentialKind.Windows
            },
            new SensorParameterDefinition
            {
                Key = "winrm.password",
                Label = "Windows password",
                Kind = SensorParameterKind.Secret,
                Description = "Windows account password",
                Required = true,
                CredentialKind = MonitoringCredentialKind.Windows
            },
            new SensorParameterDefinition
            {
                Key = "winrm.port",
                Label = "WinRM port",
                Kind = SensorParameterKind.Integer,
                Description = "WinRM port on the remote host",
                DefaultValue = "5985",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "winrm.useSsl",
                Label = "Use SSL",
                Kind = SensorParameterKind.Boolean,
                Description = "Use WinRM over HTTPS instead of HTTP",
                DefaultValue = "false"
            },
            new SensorParameterDefinition
            {
                Key = "winrm.configurationName",
                Label = "Configuration name",
                Kind = SensorParameterKind.Text,
                Description = "PowerShell remoting endpoint on the Windows host",
                DefaultValue = "Microsoft.PowerShell",
                Placeholder = "Microsoft.PowerShell"
            },
            new SensorParameterDefinition
            {
                Key = "script",
                Label = "Script",
                Kind = SensorParameterKind.ScriptEditor,
                Description = "PowerShell script executed on the remote host. Emit JSON, XML, regex-friendly text or a single numeric value.",
                Required = true,
                Placeholder = """
Get-CimInstance Win32_OperatingSystem |
    Select-Object FreePhysicalMemory
"""
            },
            new SensorParameterDefinition
            {
                Key = "outputFormat",
                Label = "Output format",
                Kind = SensorParameterKind.ValueList,
                Description = "How the sensor should interpret the script output",
                DefaultValue = "auto",
                Options =
                [
                    new SensorParameterOption { Value = "auto", Label = "Auto" },
                    new SensorParameterOption { Value = "json", Label = "JSON" },
                    new SensorParameterOption { Value = "xml", Label = "XML" },
                    new SensorParameterOption { Value = "regex", Label = "Regex" },
                    new SensorParameterOption { Value = "text", Label = "Text" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "regexPattern",
                Label = "Regex pattern",
                Kind = SensorParameterKind.Multiline,
                Description = "Used when output format is Regex. Named capture groups become channels.",
                Placeholder = "(?<value>\\d+(?:\\.\\d+)?)",
                VisibleWhenParameterKey = "outputFormat",
                VisibleWhenValues = ["regex"]
            },
            new SensorParameterDefinition
            {
                Key = "defaultChannelKey",
                Label = "Default channel key",
                Kind = SensorParameterKind.Text,
                Description = "Optional channel key to graph. If empty, the first numeric channel is used.",
                Placeholder = "cpu.load"
            },
            new SensorParameterDefinition
            {
                Key = "failOnStderr",
                Label = "Fail on stderr",
                Kind = SensorParameterKind.Boolean,
                Description = "Treat stderr output as a sensor error",
                DefaultValue = "true"
            }
        ]
    };

    private const string DefaultOutputFormat = "auto";
    internal const string WindowsHealthDiscoveryScript = """
$ErrorActionPreference = 'SilentlyContinue'
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average | Select-Object -ExpandProperty Average
$totalMemoryMb = [double]$os.TotalVisibleMemorySize / 1024
$freeMemoryMb = [double]$os.FreePhysicalMemory / 1024
$memoryUsedPercent = if ($totalMemoryMb -gt 0) { (($totalMemoryMb - $freeMemoryMb) / $totalMemoryMb) * 100 } else { 0 }

$disks = Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3'
$diskTotal = [double](($disks | Measure-Object -Property Size -Sum).Sum)
$diskFree = [double](($disks | Measure-Object -Property FreeSpace -Sum).Sum)
$diskUsedPercent = if ($diskTotal -gt 0) { (($diskTotal - $diskFree) / $diskTotal) * 100 } else { 0 }

$smartStatus = -1
$physical = Get-PhysicalDisk
if ($physical) {
    $smartStatus = 0
    foreach ($d in $physical) {
        if ($d.HealthStatus -eq 'Unhealthy') { $smartStatus = 2 }
        elseif ($d.HealthStatus -eq 'Warning' -and $smartStatus -lt 2) { $smartStatus = 1 }
    }
}

[pscustomobject]@{
    cpuLoad = [math]::Round([double]$cpu, 2)
    memoryUsedPercent = [math]::Round([double]$memoryUsedPercent, 2)
    memoryFreeMb = [math]::Round([double]$freeMemoryMb, 2)
    diskUsedPercent = [math]::Round([double]$diskUsedPercent, 2)
    smartStatus = $smartStatus
}
""";

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        _ = context;

        // Auto-discovery of Windows health is owned by the dedicated WindowsHealthSensorExecutor
        // now; this generic "run any PowerShell script" sensor stays manual-only.
        return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target is required");
        }

        if (!TryReadRemoteUsername(context.Settings, out var username) ||
            string.IsNullOrWhiteSpace(username))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "Windows username is required");
        }

        if (!MonitoringSettings.TryReadParameter(context.Settings, "script", out var script) ||
            string.IsNullOrWhiteSpace(script))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "script is required");
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(30);
        var useSsl = MonitoringSettings.TryReadParameterBool(context.Settings, "winrm.useSsl", out var configuredUseSsl) &&
            configuredUseSsl;
        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "winrm.port", out var configuredPort)
            ? configuredPort
            : (useSsl ? 5986 : 5985);
        var password = TryReadRemotePassword(context.Settings, out var configuredPassword)
            ? configuredPassword
            : string.Empty;
        if (string.IsNullOrWhiteSpace(password))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "Windows password is required for WinRM sensors");
        }

        var configurationName = MonitoringSettings.TryReadParameter(context.Settings, "winrm.configurationName", out var configuredConfigurationName) &&
            !string.IsNullOrWhiteSpace(configuredConfigurationName)
            ? configuredConfigurationName.Trim()
            : "Microsoft.PowerShell";
        var outputFormat = ResolveOutputFormat(context.Settings);
        var regexPattern = MonitoringSettings.TryReadParameter(context.Settings, "regexPattern", out var configuredRegexPattern)
            ? configuredRegexPattern
            : string.Empty;
        var defaultChannelKey = MonitoringSettings.TryReadParameter(context.Settings, "defaultChannelKey", out var configuredDefaultChannelKey)
            ? configuredDefaultChannelKey.Trim()
            : string.Empty;
        var failOnStderr = !MonitoringSettings.TryReadParameterBool(context.Settings, "failOnStderr", out var configuredFailOnStderr) ||
            configuredFailOnStderr;

        var watch = Stopwatch.StartNew();

        var endpointFailure = await ValidateWinRmEndpointAsync(context.Target, port, useSsl, timeout, cancellationToken);
        if (endpointFailure is not null)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, endpointFailure);
        }

        var usernameCandidates = BuildUsernameCandidates(context.Target, username).ToArray();
        Process? process = null;
        var lastFailureMessage = string.Empty;

        try
        {
            foreach (var candidateUsername in usernameCandidates)
            {
                process = StartPowerShellRemotingProcess(
                    context.Target,
                    candidateUsername,
                    port,
                    password,
                    useSsl,
                    configurationName,
                    timeout,
                    outputFormat,
                    script,
                    regexPattern,
                    defaultChannelKey);

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                using var registration = timeoutCts.Token.Register(() => TryKill(process));
                await process.WaitForExitAsync(timeoutCts.Token);

                var stdout = await stdoutTask;
                var stderr = NormalizeRemoteStderr(await stderrTask);
                watch.Stop();

                var parse = ParseOutput(stdout, outputFormat, regexPattern, defaultChannelKey);
                if (!parse.Channels.Any())
                {
                    lastFailureMessage = BuildParseFailureMessage(stdout, stderr, process.ExitCode);
                    if (IsRemoteConnectivityFailure(lastFailureMessage) && candidateUsername != usernameCandidates.Last())
                    {
                        continue;
                    }

                    return SensorExecutionResult.Critical(watch.Elapsed, BuildRemoteFailureHint(lastFailureMessage, context.Target, port, useSsl));
                }

                var defaultChannel = SelectDefaultChannel(parse.Channels, defaultChannelKey);
                if (defaultChannel is null || !defaultChannel.Value.HasValue)
                {
                    return SensorExecutionResult.Critical(
                        watch.Elapsed,
                        "no numeric default channel could be selected from the script output");
                }

                var sensorState = ResolveState(process.ExitCode, failOnStderr, stderr, parse.StateHint);
                if (sensorState == SensorState.Critical && process.ExitCode != 0 && string.IsNullOrWhiteSpace(stderr))
                {
                    stderr = $"remote execution exited with code {process.ExitCode}";
                }

                if (sensorState == SensorState.Critical)
                {
                    var result = SensorExecutionResult.Critical(
                        watch.Elapsed,
                        BuildMessage(defaultChannel, parse.Message, stderr, parse.Channels.Count),
                        defaultChannel.Value,
                        defaultChannel.Key,
                        MarkDefault(parse.Channels, defaultChannel.Key));
                    return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
                }

                if (sensorState == SensorState.Warning)
                {
                    var result = SensorExecutionResult.Warning(
                        watch.Elapsed,
                        BuildMessage(defaultChannel, parse.Message, stderr, parse.Channels.Count),
                        defaultChannel.Value,
                        defaultChannel.Key,
                        MarkDefault(parse.Channels, defaultChannel.Key));
                    return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
                }

                var healthyResult = SensorExecutionResult.Healthy(
                    watch.Elapsed,
                    BuildMessage(defaultChannel, parse.Message, stderr, parse.Channels.Count),
                    defaultChannel.Value,
                    defaultChannel.Key,
                    MarkDefault(parse.Channels, defaultChannel.Key));
                return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, healthyResult);
            }

            return SensorExecutionResult.Critical(
                watch.Elapsed,
                BuildRemoteFailureHint(string.IsNullOrWhiteSpace(lastFailureMessage) ? "remote execution failed" : lastFailureMessage, context.Target, port, useSsl));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            return SensorExecutionResult.Unknown("execution cancelled");
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, $"execution timed out after {timeout.TotalSeconds:0.#} seconds");
        }
        catch (Win32Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(
                watch.Elapsed,
                $"PowerShell client not available: {ex.Message}");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
        finally
        {
            TryKill(process);
            process?.Dispose();
        }
    }

    private static Process StartPowerShellRemotingProcess(
        string target,
        string username,
        int port,
        string password,
        bool useSsl,
        string configurationName,
        TimeSpan timeout,
        string outputFormat,
        string script,
        string regexPattern,
        string defaultChannelKey)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment["MATMON_TARGET"] = target;
        startInfo.Environment["MATMON_USERNAME"] = username;
        startInfo.Environment["MATMON_PASSWORD"] = password;
        startInfo.Environment["MATMON_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["MATMON_USESSL"] = useSsl ? "true" : "false";
        startInfo.Environment["MATMON_CONFIGURATION"] = configurationName;
        startInfo.Environment["MATMON_OUTPUT_FORMAT"] = outputFormat;
        startInfo.Environment["MATMON_REGEX_PATTERN"] = regexPattern;
        startInfo.Environment["MATMON_DEFAULT_CHANNEL"] = defaultChannelKey;
        startInfo.Environment["MATMON_PS_SCRIPT_B64"] = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        startInfo.Environment["MATMON_TIMEOUT_SECONDS"] = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        var wrapperCommand = BuildPowerShellRemotingWrapperCommand();
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapperCommand)));

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("pwsh process could not be started.");

        return process;
    }

    private static string BuildPowerShellRemotingWrapperCommand()
    {
        return """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$target = $env:MATMON_TARGET
$username = $env:MATMON_USERNAME
$password = $env:MATMON_PASSWORD
$port = [int]($env:MATMON_PORT ?? '5985')
$useSsl = ($env:MATMON_USESSL ?? 'false') -eq 'true'
$configurationName = $env:MATMON_CONFIGURATION
$outputFormat = ($env:MATMON_OUTPUT_FORMAT ?? 'auto').Trim().ToLowerInvariant()
$regexPattern = $env:MATMON_REGEX_PATTERN
$defaultChannelKey = $env:MATMON_DEFAULT_CHANNEL
$script = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($env:MATMON_PS_SCRIPT_B64))

if ([string]::IsNullOrWhiteSpace($configurationName)) {
    $configurationName = 'Microsoft.PowerShell'
}

if ([string]::IsNullOrWhiteSpace($password)) {
    throw 'Windows password is required for WinRM sensors.'
}

$securePassword = ConvertTo-SecureString $password -AsPlainText -Force
$credential = [PSCredential]::new($username, $securePassword)

$sessionParams = @{
    ComputerName = $target
    Credential = $credential
    Authentication = 'Negotiate'
    ErrorAction = 'Stop'
}

if ($port -gt 0) {
    $sessionParams.Port = $port
}

if ($useSsl) {
    $sessionParams.UseSSL = $true
}

if (-not [string]::IsNullOrWhiteSpace($configurationName)) {
    $sessionParams.ConfigurationName = $configurationName
}

try {
    $session = New-PSSession @sessionParams
    try {
        $result = Invoke-Command -Session $session -ScriptBlock ([scriptblock]::Create($script))

        switch ($outputFormat) {
            'json' {
                $result | ConvertTo-Json -Depth 20 -Compress -ErrorAction Stop
            }
            'xml' {
                $result | ConvertTo-Xml -As String -Depth 20 -ErrorAction Stop
            }
            default {
                if ($null -eq $result) {
                    ''
                }
                elseif ($result -is [string]) {
                    $result
                }
                else {
                    $result | ConvertTo-Json -Depth 20 -Compress -ErrorAction Stop
                }
            }
        }
    }
    finally {
        if ($session) {
            Remove-PSSession $session
        }
    }
}
catch {
    Write-Error $_
    exit 1
}
""";
    }

    private static string ResolveOutputFormat(MonitoringSettings settings)
    {
        return MonitoringSettings.TryReadParameter(settings, "outputFormat", out var outputFormat) &&
               !string.IsNullOrWhiteSpace(outputFormat)
            ? outputFormat.Trim().ToLowerInvariant()
            : DefaultOutputFormat;
    }

    private static SensorDiscoverySuggestion BuildWindowsHealthSuggestion(int port, bool useSsl)
    {
        var settings = new MonitoringSettings
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultChannelKey = "cpuLoad"
        };
        settings.Parameters["winrm.port"] = port.ToString(CultureInfo.InvariantCulture);
        settings.Parameters["winrm.useSsl"] = useSsl ? "true" : "false";
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["defaultChannelKey"] = "cpuLoad";
        settings.Parameters["script"] = WindowsHealthDiscoveryScript;
        MonitoringSettings.SetChannelThreshold(
            settings,
            "cpuLoad",
            "warning",
            new ThresholdRule(ThresholdDirection.Above, 85));
        MonitoringSettings.SetChannelThreshold(
            settings,
            "cpuLoad",
            "critical",
            new ThresholdRule(ThresholdDirection.Above, 95));
        MonitoringSettings.SetChannelThreshold(
            settings,
            "memoryUsedPercent",
            "warning",
            new ThresholdRule(ThresholdDirection.Above, 85));
        MonitoringSettings.SetChannelThreshold(
            settings,
            "memoryUsedPercent",
            "critical",
            new ThresholdRule(ThresholdDirection.Above, 95));
        MonitoringSettings.SetChannelThreshold(
            settings,
            "diskUsedPercent",
            "warning",
            new ThresholdRule(ThresholdDirection.Above, 85));
        MonitoringSettings.SetChannelThreshold(
            settings,
            "diskUsedPercent",
            "critical",
            new ThresholdRule(ThresholdDirection.Above, 95));
        MonitoringSettings.SetChannelThreshold(
            settings,
            "smartStatus",
            "warning",
            new ThresholdRule(ThresholdDirection.Above, 0));
        MonitoringSettings.SetChannelThreshold(
            settings,
            "smartStatus",
            "critical",
            new ThresholdRule(ThresholdDirection.Above, 1));

        return new SensorDiscoverySuggestion(
            Definition.Key,
            useSsl ? "Windows Health (WinRM HTTPS)" : "Windows Health",
            string.Empty,
            settings,
            $"WinRM port {port} is open. Windows credentials can be inherited from the parent.",
            87);
    }

    private static bool TryReadRemoteUsername(MonitoringSettings settings, out string username)
    {
        if (MonitoringSettings.TryReadParameter(settings, "winrm.username", out username) &&
            !string.IsNullOrWhiteSpace(username))
        {
            username = username.Trim();
            return true;
        }

        if (MonitoringSettings.TryReadParameter(settings, "ssh.username", out username) &&
            !string.IsNullOrWhiteSpace(username))
        {
            username = username.Trim();
            return true;
        }

        username = string.Empty;
        return false;
    }

    private static bool TryReadRemotePassword(MonitoringSettings settings, out string password)
    {
        if (MonitoringSettings.TryReadParameter(settings, "winrm.password", out password) &&
            !string.IsNullOrWhiteSpace(password))
        {
            return true;
        }

        if (MonitoringSettings.TryReadParameter(settings, "ssh.password", out password) &&
            !string.IsNullOrWhiteSpace(password))
        {
            return true;
        }

        password = string.Empty;
        return false;
    }

    private static IEnumerable<string> BuildUsernameCandidates(string target, string username)
    {
        var trimmedUsername = username.Trim();
        yield return trimmedUsername;

        if (trimmedUsername.Contains('\\') || trimmedUsername.Contains('@'))
        {
            yield break;
        }

        var trimmedTarget = target.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedTarget))
        {
            yield return $"{trimmedTarget}\\{trimmedUsername}";
        }

        yield return $".\\{trimmedUsername}";
    }

    private static async ValueTask<string?> ValidateWinRmEndpointAsync(
        string target,
        int port,
        bool useSsl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var trimmedTarget = target.Trim();
        if (string.IsNullOrWhiteSpace(trimmedTarget))
        {
            return "target is required";
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(trimmedTarget, cancellationToken);
        }
        catch (SocketException ex)
        {
            return BuildWinRmEndpointFailureMessage(trimmedTarget, port, useSsl, $"target could not be resolved: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuildWinRmEndpointFailureMessage(trimmedTarget, port, useSsl, $"target could not be resolved: {ex.Message}");
        }

        if (addresses.Length == 0)
        {
            return BuildWinRmEndpointFailureMessage(trimmedTarget, port, useSsl, "target could not be resolved");
        }

        var connectTimeout = TimeSpan.FromSeconds(
            Math.Min(3, Math.Max(1, timeout.TotalSeconds / 4)));
        SocketException? lastSocketError = null;

        foreach (var address in addresses)
        {
            using var client = new TcpClient(address.AddressFamily);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(connectTimeout);

            try
            {
                await client.ConnectAsync(address, port, connectCts.Token);
                if (client.Connected)
                {
                    return null;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return BuildWinRmEndpointFailureMessage(
                    trimmedTarget,
                    port,
                    useSsl,
                    $"WinRM did not respond within {connectTimeout.TotalSeconds:0.#} seconds");
            }
            catch (SocketException ex)
            {
                lastSocketError = ex;
            }
        }

        return BuildWinRmEndpointFailureMessage(trimmedTarget, port, useSsl, lastSocketError?.Message);
    }

    private static bool IsRemoteConnectivityFailure(string message)
    {
        return message.Contains("MI_RESULT_FAILED", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Connecting to remote server", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("WinRM", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("WSMan", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("SPNEGO", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("The client cannot connect", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unreachable", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRemoteFailureHint(string message, string target, int port, bool useSsl)
    {
        if (message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            return $"WinRM to {target}:{port} reached the host, but the credentials were rejected. Check the Windows username and password and whether the account is allowed for PowerShell Remoting.";
        }

        if (message.Contains("SPNEGO", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("acquiring creds with username only failed", StringComparison.OrdinalIgnoreCase))
        {
            return "The Matmon container is missing SPNEGO/NTLM support for WinRM authentication. Rebuild the image with gss-ntlmssp installed, then retry.";
        }

        if (IsRemoteConnectivityFailure(message))
        {
            return BuildWinRmEndpointFailureMessage(target, port, useSsl, message);
        }

        return message;
    }

    private static string BuildWinRmEndpointFailureMessage(string target, int port, bool useSsl, string? reason)
    {
        var protocol = useSsl ? "HTTPS" : "HTTP";
        var baseMessage = $"WinRM on {target}:{port} is not reachable. Enable PowerShell Remoting on the Windows host and make sure port {port} ({protocol}) is listening.";
        var setupHint = "If this is a Windows client and the active network is Public, run PowerShell as Administrator and use Enable-PSRemoting -Force -SkipNetworkProfileCheck, or switch the network profile to Private/Domain.";

        if (string.IsNullOrWhiteSpace(reason))
        {
            return $"{baseMessage} {setupHint}";
        }

        if (reason.Contains("could not be resolved", StringComparison.OrdinalIgnoreCase))
        {
            return $"Target '{target}' could not be resolved from the Matmon container. Check DNS, hosts entries, or Docker host mapping.";
        }

        var result = $"{baseMessage} Details: {reason.Trim()}";
        if (reason.Contains("public", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("öffentlich", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("skipnetworkprofilecheck", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("network connection type", StringComparison.OrdinalIgnoreCase))
        {
            result += $" {setupHint}";
        }

        return result;
    }

    internal static ParsedPowerShellOutput ParseOutput(
        string stdout,
        string outputFormat,
        string regexPattern,
        string defaultChannelKey)
    {
        var trimmed = stdout.Trim();
        var channels = new List<SensorChannelValue>();
        SensorState? stateHint = TryParseState(trimmed, out _);
        string? message = null;

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new ParsedPowerShellOutput(channels, stateHint, "script returned no output");
        }

        var effectiveFormat = outputFormat switch
        {
            "json" => "json",
            "xml" => "xml",
            "regex" => "regex",
            "text" => "text",
            _ => DetectOutputFormat(trimmed, regexPattern)
        };

        switch (effectiveFormat)
        {
            case "json":
                if (!TryParseJsonChannels(trimmed, channels, ref stateHint))
                {
                    throw new InvalidOperationException("script output is not valid JSON");
                }
                break;
            case "xml":
                if (!TryParseXmlChannels(trimmed, channels, ref stateHint))
                {
                    throw new InvalidOperationException("script output is not valid XML");
                }
                break;
            case "regex":
                if (string.IsNullOrWhiteSpace(regexPattern))
                {
                    throw new InvalidOperationException("regex pattern is required for regex output");
                }

                if (!TryParseRegexChannels(trimmed, regexPattern, channels, ref stateHint))
                {
                    throw new InvalidOperationException("regex did not produce any numeric channels");
                }
                break;
            default:
                if (!TryParseTextChannel(trimmed, channels, ref stateHint))
                {
                    throw new InvalidOperationException("script output did not contain any numeric values");
                }
                break;
        }

        if (channels.Count == 0)
        {
            throw new InvalidOperationException("script output did not produce any numeric channels");
        }

        if (!string.IsNullOrWhiteSpace(defaultChannelKey))
        {
            var hasConfiguredDefault = channels.Any(channel =>
                string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase));

            if (!hasConfiguredDefault)
            {
                throw new InvalidOperationException($"default channel '{defaultChannelKey}' was not found in the script output");
            }
        }

        return new ParsedPowerShellOutput(channels, stateHint, message);
    }

    private static string DetectOutputFormat(string stdout, string regexPattern)
    {
        var trimmed = stdout.TrimStart();

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return "json";
        }

        if (trimmed.StartsWith('<'))
        {
            return "xml";
        }

        if (!string.IsNullOrWhiteSpace(regexPattern))
        {
            return "regex";
        }

        return "text";
    }

    private static bool TryParseJsonChannels(
        string json,
        List<SensorChannelValue> channels,
        ref SensorState? stateHint)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        FlattenJsonElement(document.RootElement, string.Empty, channels, ref stateHint);
        return true;
    }

    private static void FlattenJsonElement(
        JsonElement element,
        string path,
        ICollection<SensorChannelValue> channels,
        ref SensorState? stateHint)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsPowerShellRemotingMetadataKey(property.Name))
                    {
                        continue;
                    }

                    var childPath = CombinePath(path, property.Name);
                    if (IsStateKey(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var parsedState = TryParseState(property.Value.GetString(), out _);
                        if (parsedState.HasValue && stateHint is null)
                        {
                            stateHint = parsedState.Value;
                        }
                    }

                    FlattenJsonElement(property.Value, childPath, channels, ref stateHint);
                }
                break;
            case JsonValueKind.Array:
                var countPath = CombinePath(path, "count");
                channels.Add(new SensorChannelValue
                {
                    Key = NormalizeKey(countPath),
                    Label = $"{FormatLabel(path)} Count",
                    Value = element.GetArrayLength()
                });
                break;
            case JsonValueKind.Number:
                AddChannel(channels, path, element.GetDouble());
                break;
            case JsonValueKind.String:
                if (TryParseNumber(element.GetString(), out var stringNumber))
                {
                    AddChannel(channels, path, stringNumber);
                }
                else
                {
                    var parsedState = TryParseState(element.GetString(), out _);
                    if (parsedState.HasValue && stateHint is null)
                    {
                        stateHint = parsedState.Value;
                    }
                }
                break;
            case JsonValueKind.True:
                AddChannel(channels, path, 1);
                break;
            case JsonValueKind.False:
                AddChannel(channels, path, 0);
                break;
        }
    }

    private static bool TryParseXmlChannels(
        string xml,
        List<SensorChannelValue> channels,
        ref SensorState? stateHint)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        FlattenXmlElement(document.Root, string.Empty, channels, ref stateHint);
        return true;
    }

    private static void FlattenXmlElement(
        XElement? element,
        string path,
        ICollection<SensorChannelValue> channels,
        ref SensorState? stateHint)
    {
        if (element is null)
        {
            return;
        }

        var currentPath = CombinePath(path, element.Name.LocalName);
        var attributes = element.Attributes().ToArray();
        if (attributes.Length > 0)
        {
            foreach (var attribute in attributes)
            {
                if (IsStateKey(attribute.Name.LocalName))
                {
                    var parsedState = TryParseState(attribute.Value, out _);
                    if (parsedState.HasValue && stateHint is null)
                    {
                        stateHint = parsedState.Value;
                    }
                }

                if (TryParseNumber(attribute.Value, out var attributeNumber))
                {
                    AddChannel(channels, CombinePath(currentPath, attribute.Name.LocalName), attributeNumber);
                }
            }
        }

        var childElements = element.Elements().GroupBy(child => child.Name.LocalName, StringComparer.OrdinalIgnoreCase);
        if (!childElements.Any())
        {
            if (TryParseNumber(element.Value, out var numericValue))
            {
                AddChannel(channels, currentPath, numericValue);
            }
            else
            {
                var parsedState = TryParseState(element.Value, out _);
                if (parsedState.HasValue && stateHint is null)
                {
                    stateHint = parsedState.Value;
                }
            }

            return;
        }

        foreach (var group in childElements)
        {
            var groupElements = group.ToArray();
            var groupPath = CombinePath(currentPath, group.Key);

            if (groupElements.Length > 1)
            {
                var countPath = CombinePath(groupPath, "count");
                channels.Add(new SensorChannelValue
                {
                    Key = NormalizeKey(countPath),
                    Label = $"{FormatLabel(groupPath)} Count",
                    Value = groupElements.Length
                });

                continue;
            }

            FlattenXmlElement(groupElements[0], currentPath, channels, ref stateHint);
        }
    }

    private static bool TryParseRegexChannels(
        string text,
        string pattern,
        List<SensorChannelValue> channels,
        ref SensorState? stateHint)
    {
        var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
        var match = regex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var groupNames = regex.GetGroupNames();
        foreach (var groupName in groupNames)
        {
            if (groupName == "0" || int.TryParse(groupName, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            var group = match.Groups[groupName];
            if (!group.Success)
            {
                continue;
            }

            if (IsStateKey(groupName))
            {
                var parsedState = TryParseState(group.Value, out _);
                if (parsedState.HasValue && stateHint is null)
                {
                    stateHint = parsedState.Value;
                }
            }

            if (TryParseNumber(group.Value, out var numericValue))
            {
                AddChannel(channels, groupName, numericValue);
            }
            else if (bool.TryParse(group.Value, out var boolValue))
            {
                AddChannel(channels, groupName, boolValue ? 1 : 0);
            }
        }

        if (channels.Count > 0)
        {
            return true;
        }

        for (var index = 1; index < match.Groups.Count; index++)
        {
            var group = match.Groups[index];
            if (!group.Success)
            {
                continue;
            }

            var key = $"group{index}";
            if (TryParseNumber(group.Value, out var numericValue))
            {
                AddChannel(channels, key, numericValue);
            }
            else if (bool.TryParse(group.Value, out var boolValue))
            {
                AddChannel(channels, key, boolValue ? 1 : 0);
            }
        }

        return channels.Count > 0;
    }

    private static bool TryParseTextChannel(
        string text,
        List<SensorChannelValue> channels,
        ref SensorState? stateHint)
    {
        var parsedTextState = TryParseState(text, out _);
        if (parsedTextState.HasValue && stateHint is null)
        {
            stateHint = parsedTextState.Value;
        }

        if (TryParseNumber(text, out var numericValue))
        {
            AddChannel(channels, "value", numericValue, isDefault: true);
            return true;
        }

        var firstNumberMatch = Regex.Match(text, @"[-+]?\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant);
        if (firstNumberMatch.Success && TryParseNumber(firstNumberMatch.Value, out numericValue))
        {
            AddChannel(channels, "value", numericValue, isDefault: true);
            return true;
        }

        return false;
    }

    private static void AddChannel(
        ICollection<SensorChannelValue> channels,
        string path,
        double value,
        bool isDefault = false)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(path) ? "value" : path;
        channels.Add(new SensorChannelValue
        {
            Key = NormalizeKey(normalizedPath),
            Label = FormatLabel(normalizedPath),
            Value = value,
            IsDefault = isDefault
        });
    }

    internal static List<SensorChannelValue> MarkDefault(
        IReadOnlyList<SensorChannelValue> channels,
        string defaultChannelKey)
    {
        var selectedChannel = SelectDefaultChannel(channels, defaultChannelKey)
            ?? channels.FirstOrDefault(channel => channel.Value.HasValue);

        return channels
            .Select(channel => channel with
            {
                IsDefault = selectedChannel is not null &&
                    string.Equals(channel.Key, selectedChannel.Key, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
    }

    internal static SensorChannelValue? SelectDefaultChannel(
        IReadOnlyList<SensorChannelValue> channels,
        string defaultChannelKey)
    {
        if (!string.IsNullOrWhiteSpace(defaultChannelKey))
        {
            var configuredChannel = channels.FirstOrDefault(channel =>
                string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase));

            if (configuredChannel is not null)
            {
                return configuredChannel;
            }
        }

        return channels.FirstOrDefault(channel => channel.IsDefault && channel.Value.HasValue)
            ?? channels.FirstOrDefault(channel => channel.Value.HasValue)
            ?? channels.FirstOrDefault();
    }

    internal static SensorState ResolveState(
        int exitCode,
        bool failOnStderr,
        string stderr,
        SensorState? stateHint)
    {
        if (exitCode != 0)
        {
            return SensorState.Critical;
        }

        if (failOnStderr && !string.IsNullOrWhiteSpace(stderr))
        {
            return SensorState.Critical;
        }

        return stateHint ?? SensorState.Healthy;
    }

    internal static string BuildMessage(
        SensorChannelValue defaultChannel,
        string? parsedMessage,
        string stderr,
        int channelCount)
    {
        if (!string.IsNullOrWhiteSpace(parsedMessage))
        {
            return parsedMessage;
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            return stderr.Trim();
        }

        var label = string.IsNullOrWhiteSpace(defaultChannel.Label) ? defaultChannel.Key : defaultChannel.Label;
        return channelCount > 1 && defaultChannel.Value.HasValue
            ? $"{label} {FormatNumber(defaultChannel.Value.Value)}"
            : $"{label} {FormatNumber(defaultChannel.Value ?? 0)}";
    }

    private static string BuildParseFailureMessage(string stdout, string stderr, int exitCode)
    {
        var parts = new List<string>();

        if (exitCode != 0)
        {
            parts.Add($"remote execution exited with code {exitCode}");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            parts.Add(stderr.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            parts.Add($"output: {Truncate(stdout.Trim(), 240)}");
        }

        return parts.Count > 0
            ? string.Join(" | ", parts)
            : "script returned no usable values";
    }

    private static string NormalizeRemoteStderr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return string.Empty;
        }

        var trimmed = stderr.Trim();

        if ((trimmed.Contains("Preparing modules for first use.", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("progress", StringComparison.OrdinalIgnoreCase)) &&
            !trimmed.Contains("Write-Error", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.Contains("Exception", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.Contains("FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return trimmed;
    }

    private static bool TryParseNumber(string? rawValue, out double value)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            value = default;
            return false;
        }

        var normalized = rawValue.Trim();
        if (double.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (bool.TryParse(normalized, out var booleanValue))
        {
            value = booleanValue ? 1 : 0;
            return true;
        }

        value = default;
        return false;
    }

    private static SensorState? TryParseState(string? rawValue, out string? normalizedMessage)
    {
        normalizedMessage = null;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var normalized = rawValue.Trim().ToLowerInvariant();
        normalizedMessage = normalized;

        return normalized switch
        {
            "ok" or "healthy" or "success" or "good" => SensorState.Healthy,
            "warning" or "warn" => SensorState.Warning,
            "error" or "critical" or "failed" or "fail" => SensorState.Critical,
            "disabled" => SensorState.Disabled,
            "paused" => SensorState.Paused,
            "unknown" or "nodata" or "no data" => SensorState.Unknown,
            _ => null
        };
    }

    private static bool IsStateKey(string key)
    {
        return key.Equals("state", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("status", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("severity", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPowerShellRemotingMetadataKey(string key)
    {
        return key.Equals("PSComputerName", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("RunspaceId", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("PSShowComputerName", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombinePath(string prefix, string segment)
    {
        var normalizedSegment = segment.Trim();
        return string.IsNullOrWhiteSpace(prefix)
            ? normalizedSegment
            : $"{prefix}.{normalizedSegment}";
    }

    private static string NormalizeKey(string key)
    {
        var builder = new StringBuilder(key.Length);

        foreach (var character in key)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (character is '.' or '-' or '_')
            {
                builder.Append(character);
                continue;
            }

            builder.Append('_');
        }

        return TrimSeparators(builder.ToString());
    }

    private static string FormatLabel(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Value";
        }

        var label = path.Replace('.', ' ')
            .Replace('_', ' ')
            .Replace('-', ' ');
        label = Regex.Replace(label, "([a-z0-9])([A-Z])", "$1 $2");
        label = Regex.Replace(label, "\\s+", " ").Trim();

        if (label.Length == 0)
        {
            return "Value";
        }

        return char.ToUpperInvariant(label[0]) + label[1..];
    }

    private static string TrimSeparators(string value)
    {
        return value.Trim('.', '-', '_');
    }

    private static string FormatNumber(double value)
    {
        return value % 1 == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 1)] + "...";
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    internal sealed record ParsedPowerShellOutput(
        IReadOnlyList<SensorChannelValue> Channels,
        SensorState? StateHint,
        string? Message);
}
