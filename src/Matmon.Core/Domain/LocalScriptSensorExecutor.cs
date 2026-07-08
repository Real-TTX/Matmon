using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Matmon.Core.Domain;

/// <summary>
/// Runs a script (PowerShell or shell) <em>locally</em> on the Matmon host/probe itself and
/// extracts numeric channels from its output. It is the local counterpart to the
/// <see cref="PowerShellRemoteSensorExecutor"/> (no WinRM): same JSON/XML/regex/text parsing,
/// but the script executes in the same container/process host as the executor. PowerShell is
/// available because the image ships <c>pwsh</c>; <c>bash</c>/<c>sh</c> work too.
/// </summary>
public sealed class LocalScriptSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "local-script",
        DisplayName = "Local Script",
        Description = "Run a PowerShell or shell script on the Matmon host/probe itself and extract numeric channels from JSON, XML, regex or text output.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "local.shell",
                Label = "Shell",
                Kind = SensorParameterKind.ValueList,
                Description = "Interpreter used to run the script on this host",
                DefaultValue = "pwsh",
                Options =
                [
                    new SensorParameterOption { Value = "pwsh", Label = "PowerShell (pwsh)" },
                    new SensorParameterOption { Value = "bash", Label = "Bash" },
                    new SensorParameterOption { Value = "sh", Label = "sh" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "script",
                Label = "Script",
                Kind = SensorParameterKind.ScriptEditor,
                Description = "Script executed locally. For PowerShell, emit a value/object (it is converted to JSON); for shell, print JSON, key=value/regex-friendly text or a single number. Variables: MATMON_HOST/MATMON_TARGET, and MATMON_USERNAME/MATMON_PASSWORD/MATMON_TOKEN + MATMON_CRED_<FIELD> from the selected credential (PowerShell: $env:MATMON_HOST, shell: $MATMON_HOST).",
                Required = true,
                Placeholder = """
[pscustomobject]@{
    cpuLoad = (Get-Counter '\Processor(_Total)\% Processor Time').CounterSamples.CookedValue
}
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
                Key = "failOnStderr",
                Label = "Fail on stderr",
                Kind = SensorParameterKind.Boolean,
                Description = "Treat stderr output as a sensor error",
                DefaultValue = "true"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    // Local script is configured by hand; nothing to auto-discover.
    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        _ = cancellationToken;
        return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!MonitoringSettings.TryReadParameter(context.Settings, "script", out var script) ||
            string.IsNullOrWhiteSpace(script))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "script is required");
        }

        var shell = MonitoringSettings.TryReadParameter(context.Settings, "local.shell", out var configuredShell) &&
            !string.IsNullOrWhiteSpace(configuredShell)
            ? configuredShell.Trim().ToLowerInvariant()
            : "pwsh";
        var outputFormat = MonitoringSettings.TryReadParameter(context.Settings, "outputFormat", out var configuredFormat) &&
            !string.IsNullOrWhiteSpace(configuredFormat)
            ? configuredFormat.Trim().ToLowerInvariant()
            : "auto";
        var regexPattern = MonitoringSettings.TryReadParameter(context.Settings, "regexPattern", out var configuredRegex)
            ? configuredRegex
            : string.Empty;
        var defaultChannelKey = MonitoringSettings.TryReadParameter(context.Settings, "defaultChannelKey", out var configuredDefault)
            ? configuredDefault.Trim()
            : string.Empty;
        var failOnStderr = !MonitoringSettings.TryReadParameterBool(context.Settings, "failOnStderr", out var configuredFail) ||
            configuredFail;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(30);

        var watch = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            process = StartProcess(shell, script, outputFormat, context);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using var registration = timeoutCts.Token.Register(() => TryKill(process));
            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await stdoutTask;
            var stderr = (await stderrTask).Trim();
            watch.Stop();

            var parse = PowerShellRemoteSensorExecutor.ParseOutput(stdout, outputFormat, regexPattern, defaultChannelKey);
            if (!parse.Channels.Any())
            {
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr : Truncate(stdout.Trim(), 240);
                return SensorExecutionResult.Critical(
                    watch.Elapsed,
                    string.IsNullOrWhiteSpace(detail) ? "script returned no numeric channels" : $"script returned no numeric channels: {detail}");
            }

            var defaultChannel = PowerShellRemoteSensorExecutor.SelectDefaultChannel(parse.Channels, defaultChannelKey);
            if (defaultChannel is null || !defaultChannel.Value.HasValue)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "no numeric default channel could be selected from the script output");
            }

            var state = PowerShellRemoteSensorExecutor.ResolveState(process.ExitCode, failOnStderr, stderr, parse.StateHint);
            if (state == SensorState.Critical && process.ExitCode != 0 && string.IsNullOrWhiteSpace(stderr))
            {
                stderr = $"script exited with code {process.ExitCode}";
            }

            var message = PowerShellRemoteSensorExecutor.BuildMessage(defaultChannel, parse.Message, stderr, parse.Channels.Count);
            var channels = PowerShellRemoteSensorExecutor.MarkDefault(parse.Channels, defaultChannel.Key);

            var result = state switch
            {
                SensorState.Critical => SensorExecutionResult.Critical(watch.Elapsed, message, defaultChannel.Value, defaultChannel.Key, channels),
                SensorState.Warning => SensorExecutionResult.Warning(watch.Elapsed, message, defaultChannel.Value, defaultChannel.Key, channels),
                _ => SensorExecutionResult.Healthy(watch.Elapsed, message, defaultChannel.Value, defaultChannel.Key, channels)
            };

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
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
            return SensorExecutionResult.Critical(watch.Elapsed, $"could not start '{shell}' on this host: {ex.Message}");
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

    private static Process StartProcess(string shell, string script, string outputFormat, SensorExecutionContext context)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        ApplyContextEnvironment(startInfo, context);

        if (shell is "bash" or "sh")
        {
            startInfo.FileName = shell;
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(script);
        }
        else
        {
            // PowerShell: run a small wrapper that executes the user script and converts its
            // result the same way the remote sensor does, so a pscustomobject becomes JSON.
            startInfo.FileName = "pwsh";
            startInfo.Environment["MATMON_OUTPUT_FORMAT"] = outputFormat;
            startInfo.Environment["MATMON_PS_SCRIPT_B64"] = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(LocalPowerShellWrapper)));
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"could not start '{shell}'.");
    }

    /// <summary>
    /// Exposes the monitoring context to the script as environment variables: the host/target,
    /// the sensor type, and - when a credential is selected - its fields. Credential values are
    /// published both verbatim (<c>MATMON_CRED_&lt;FIELD&gt;</c>) and normalized
    /// (<c>MATMON_USERNAME</c>/<c>MATMON_PASSWORD</c>/<c>MATMON_TOKEN</c>) so a script can stay
    /// credential-kind-agnostic. In PowerShell read them via <c>$env:MATMON_HOST</c>; in shell via <c>$MATMON_HOST</c>.
    /// </summary>
    internal static void ApplyContextEnvironment(ProcessStartInfo startInfo, SensorExecutionContext context)
    {
        var target = context.Target ?? string.Empty;
        startInfo.Environment["MATMON_TARGET"] = target;
        startInfo.Environment["MATMON_HOST"] = target;
        startInfo.Environment["MATMON_SENSOR_TYPE"] = context.SensorTypeKey ?? string.Empty;

        if (!MonitoringSettings.TryResolveCredentialBundle(context.Settings, Array.Empty<MonitoringCredentialKind>(), out var credential) ||
            credential is null)
        {
            return;
        }

        startInfo.Environment["MATMON_CRED_KIND"] = credential.Kind.ToString();
        startInfo.Environment["MATMON_CRED_NAME"] = credential.Name ?? string.Empty;
        foreach (var pair in credential.Values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrEmpty(pair.Value))
            {
                continue;
            }

            startInfo.Environment["MATMON_CRED_" + ToEnvKey(pair.Key)] = pair.Value;
        }

        SetNormalized(startInfo, "MATMON_USERNAME", credential.Values,
            key => key.Contains("user", StringComparison.OrdinalIgnoreCase));
        SetNormalized(startInfo, "MATMON_PASSWORD", credential.Values,
            key => key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.EndsWith("secret", StringComparison.OrdinalIgnoreCase));
        SetNormalized(startInfo, "MATMON_TOKEN", credential.Values,
            key => key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                   key.Replace("-", string.Empty).Replace(".", string.Empty).Contains("apikey", StringComparison.OrdinalIgnoreCase));
    }

    private static void SetNormalized(
        ProcessStartInfo startInfo,
        string envName,
        IReadOnlyDictionary<string, string> values,
        Func<string, bool> keyMatches)
    {
        foreach (var pair in values)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value) && keyMatches(pair.Key))
            {
                startInfo.Environment[envName] = pair.Value;
                return;
            }
        }
    }

    private static string ToEnvKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var character in key)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_');
        }

        return builder.ToString().Trim('_');
    }

    private const string LocalPowerShellWrapper = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$outputFormat = ($env:MATMON_OUTPUT_FORMAT ?? 'auto').Trim().ToLowerInvariant()
$script = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($env:MATMON_PS_SCRIPT_B64))
try {
    $result = & ([scriptblock]::Create($script))
    switch ($outputFormat) {
        'json' { $result | ConvertTo-Json -Depth 20 -Compress -ErrorAction Stop }
        'xml'  { $result | ConvertTo-Xml -As String -Depth 20 -ErrorAction Stop }
        default {
            if ($null -eq $result) { '' }
            elseif ($result -is [string]) { $result }
            else { $result | ConvertTo-Json -Depth 20 -Compress -ErrorAction Stop }
        }
    }
}
catch {
    Write-Error $_
    exit 1
}
""";

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 1)] + "…";
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
            // Best effort.
        }
    }
}
