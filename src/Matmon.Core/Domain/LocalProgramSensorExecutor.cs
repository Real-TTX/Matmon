using System.ComponentModel;
using System.Diagnostics;

namespace Matmon.Core.Domain;

/// <summary>
/// Runs a local executable by path on the Matmon host/probe and extracts numeric channels
/// from its output (same parsing as the script sensors). For safety the program path must be
/// on an administrator-controlled allow-list — the <c>Matmon__AllowedProgramPaths</c>
/// environment variable (semicolon/newline separated, each entry an exact file path or a
/// directory whose programs are permitted). With no allow-list configured, nothing runs.
/// </summary>
public sealed class LocalProgramSensorExecutor : ISensorExecutor
{
    /// <summary>Env var holding the allow-list of program paths / directories.</summary>
    public const string AllowListVariable = "Matmon__AllowedProgramPaths";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "local-program",
        DisplayName = "Local Program",
        Description = "Run a local executable (by path) on the Matmon host/probe and read numeric channels from its output. The path must be allowed via the Matmon__AllowedProgramPaths setting.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "program.path",
                Label = "Program path",
                Kind = SensorParameterKind.Text,
                Description = "Absolute path to the executable. Must be allowed in Matmon__AllowedProgramPaths.",
                Required = true,
                Placeholder = "/usr/local/bin/check_thing"
            },
            new SensorParameterDefinition
            {
                Key = "program.arguments",
                Label = "Arguments",
                Kind = SensorParameterKind.Text,
                Description = "Command-line arguments. Supports double-quoted tokens. MATMON_HOST/MATMON_USERNAME/… are available as environment variables.",
                Placeholder = "--host $MATMON_HOST --json"
            },
            new SensorParameterDefinition
            {
                Key = "outputFormat",
                Label = "Output format",
                Kind = SensorParameterKind.ValueList,
                Description = "How the sensor should interpret the program output",
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
                Placeholder = "value"
            },
            new SensorParameterDefinition
            {
                Key = "failOnStderr",
                Label = "Fail on stderr",
                Kind = SensorParameterKind.Boolean,
                Description = "Treat stderr output as a sensor error",
                DefaultValue = "false"
            }
        ]
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

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!MonitoringSettings.TryReadParameter(context.Settings, "program.path", out var path) ||
            string.IsNullOrWhiteSpace(path))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "program path is required");
        }

        path = path.Trim();

        var allowList = ReadAllowList();
        if (allowList.Count == 0)
        {
            return SensorExecutionResult.Critical(
                TimeSpan.Zero,
                $"No program paths are allowed. An administrator must set the {AllowListVariable} setting (semicolon-separated paths) before this sensor can run.");
        }

        if (!IsAllowed(path, allowList))
        {
            return SensorExecutionResult.Critical(
                TimeSpan.Zero,
                $"Program path '{path}' is not in the allow-list ({AllowListVariable}).");
        }

        if (!File.Exists(path))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, $"program not found at '{path}'");
        }

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
        var failOnStderr = MonitoringSettings.TryReadParameterBool(context.Settings, "failOnStderr", out var configuredFail) && configuredFail;
        var arguments = MonitoringSettings.TryReadParameter(context.Settings, "program.arguments", out var configuredArgs)
            ? configuredArgs
            : string.Empty;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(30);

        var watch = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var token in TokenizeArguments(arguments))
            {
                startInfo.ArgumentList.Add(token);
            }

            LocalScriptSensorExecutor.ApplyContextEnvironment(startInfo, context);

            process = Process.Start(startInfo) ?? throw new InvalidOperationException($"could not start '{path}'.");

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
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout.Trim();
                return SensorExecutionResult.Critical(
                    watch.Elapsed,
                    string.IsNullOrWhiteSpace(detail) ? "program returned no numeric channels" : $"program returned no numeric channels: {Truncate(detail, 240)}");
            }

            var defaultChannel = PowerShellRemoteSensorExecutor.SelectDefaultChannel(parse.Channels, defaultChannelKey);
            if (defaultChannel is null || !defaultChannel.Value.HasValue)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "no numeric default channel could be selected from the program output");
            }

            var state = PowerShellRemoteSensorExecutor.ResolveState(process.ExitCode, failOnStderr, stderr, parse.StateHint);
            if (state == SensorState.Critical && process.ExitCode != 0 && string.IsNullOrWhiteSpace(stderr))
            {
                stderr = $"program exited with code {process.ExitCode}";
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
            return SensorExecutionResult.Critical(watch.Elapsed, $"could not start '{path}': {ex.Message}");
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

    /// <summary>The configured allow-list (exact files or directories), or empty.</summary>
    public static IReadOnlyList<string> ReadAllowList()
    {
        var raw = Environment.GetEnvironmentVariable(AllowListVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    public static bool IsAllowed(string path, IReadOnlyList<string> allowList)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var entry in allowList)
        {
            string allowed;
            try
            {
                allowed = Path.GetFullPath(entry);
            }
            catch
            {
                continue;
            }

            if (string.Equals(fullPath, allowed, comparison))
            {
                return true;
            }

            var directoryPrefix = allowed.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(directoryPrefix, comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Splits an argument string into tokens, honouring double quotes.</summary>
    private static IEnumerable<string> TokenizeArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            yield break;
        }

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var character in arguments)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (hasToken)
                {
                    yield return current.ToString();
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(character);
            hasToken = true;
        }

        if (hasToken)
        {
            yield return current.ToString();
        }
    }

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
