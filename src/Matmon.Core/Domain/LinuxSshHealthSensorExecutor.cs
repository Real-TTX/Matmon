using System.Diagnostics;
using System.Globalization;

namespace Matmon.Core.Domain;

public sealed class LinuxSshHealthSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "linux-ssh-health",
        DisplayName = "Linux SSH Health",
        Description = "Checks Linux load, memory, root filesystem usage and uptime via SSH.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "ssh.username",
                Label = "SSH username",
                Kind = SensorParameterKind.Text,
                Description = "SSH account on the Linux host",
                Required = true,
                Placeholder = "monitor",
                CredentialKind = MonitoringCredentialKind.Linux
            },
            new SensorParameterDefinition
            {
                Key = "ssh.password",
                Label = "SSH password",
                Kind = SensorParameterKind.Secret,
                Description = "Stored for inheritance. This sensor currently uses key/agent based SSH from the probe.",
                CredentialKind = MonitoringCredentialKind.Linux
            },
            new SensorParameterDefinition
            {
                Key = "ssh.port",
                Label = "SSH port",
                Kind = SensorParameterKind.Integer,
                Description = "SSH port",
                DefaultValue = "22",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "ssh.identityFile",
                Label = "Identity file",
                Kind = SensorParameterKind.Text,
                Description = "Optional key path inside the probe container.",
                Placeholder = "/app/data/id_ed25519"
            }
        ]
    };

    private const string RemoteScript = """
load1=$(awk '{print $1}' /proc/loadavg 2>/dev/null || echo 0)
mem_used=$(awk '/MemTotal/{t=$2}/MemAvailable/{a=$2}END{if(t>0) printf "%.2f", (t-a)*100/t; else print 0}' /proc/meminfo 2>/dev/null)
root_used=$(df -P / 2>/dev/null | awk 'NR==2{gsub("%","",$5); print $5}')
uptime_hours=$(awk '{printf "%.2f", $1/3600}' /proc/uptime 2>/dev/null)
smart=-1; smart_ok=0; smart_bad=0
if command -v smartctl >/dev/null 2>&1; then
  for d in $(lsblk -dno NAME,TYPE 2>/dev/null | awk '$2=="disk"{print $1}'); do
    out=$(smartctl -H "/dev/$d" 2>/dev/null)
    echo "$out" | grep -iqE 'PASSED|OK' && smart_ok=$((smart_ok+1))
    echo "$out" | grep -iqE 'FAILED|FAILING' && smart_bad=$((smart_bad+1))
  done
  if [ "$smart_bad" -gt 0 ]; then smart=2; elif [ "$smart_ok" -gt 0 ]; then smart=0; fi
fi
printf 'load1=%s\n' "${load1:-0}"
printf 'memoryUsedPercent=%s\n' "${mem_used:-0}"
printf 'rootUsedPercent=%s\n' "${root_used:-0}"
printf 'smartStatus=%s\n' "${smart:--1}"
printf 'uptimeHours=%s\n' "${uptime_hours:-0}"
""";

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!context.OpenTcpPorts.Contains(22))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["ssh.port"] = "22";
        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Linux SSH Health",
                    context.Host,
                    settings,
                    "SSH port 22 is open. Linux credentials can be inherited from the parent.",
                    75)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target is required");
        }

        if (!MonitoringSettings.TryReadParameter(context.Settings, "ssh.username", out var username) ||
            string.IsNullOrWhiteSpace(username))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "ssh username is required");
        }

        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "ssh.port", out var configuredPort)
            ? configuredPort
            : 22;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(10);
        var watch = Stopwatch.StartNew();

        try
        {
            using var process = StartSshProcess(context.Target.Trim(), username.Trim(), port, context.Settings, timeout);
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            watch.Stop();

            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(stderr) ? $"ssh exited with code {process.ExitCode}" : stderr.Trim();
                return SensorExecutionResult.Critical(watch.Elapsed, message);
            }

            var channels = ParseKeyValueChannels(stdout);
            if (channels.Count == 0)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "ssh command returned no numeric channels");
            }

            var defaultChannel = channels.FirstOrDefault(channel => channel.Key == "load1") ?? channels[0];
            var result = SensorExecutionResult.Healthy(
                watch.Elapsed,
                $"load {Format(defaultChannel.Value)}",
                defaultChannel.Value,
                defaultChannel.Key,
                MarkDefault(channels, defaultChannel.Key));

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (TimeoutException)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "ssh timeout");
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "ssh cancelled");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static Process StartSshProcess(
        string target,
        string username,
        int port,
        MonitoringSettings settings,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("BatchMode=yes");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add($"ConnectTimeout={Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds))}");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(Math.Clamp(port, 1, 65535).ToString(CultureInfo.InvariantCulture));

        if (MonitoringSettings.TryReadParameter(settings, "ssh.identityFile", out var identityFile) &&
            !string.IsNullOrWhiteSpace(identityFile))
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(identityFile.Trim());
        }

        startInfo.ArgumentList.Add($"{username}@{target}");
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(RemoteScript);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start ssh");
    }

    private static List<SensorChannelValue> ParseKeyValueChannels(string stdout)
    {
        var channels = new List<SensorChannelValue>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var rawValue = line[(separator + 1)..].Trim();
            if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            var isSmart = string.Equals(key, "smartStatus", StringComparison.OrdinalIgnoreCase);
            if (isSmart && value < 0)
            {
                // SMART unavailable (no smartctl, or disks not readable without root) —
                // skip rather than show a confusing "-1" channel.
                continue;
            }

            channels.Add(new SensorChannelValue
            {
                Key = key,
                Label = isSmart ? "SMART status" : Humanize(key),
                Value = value,
                Unit = key.EndsWith("Percent", StringComparison.OrdinalIgnoreCase) ? "%" : null,
                // SMART is a 0=healthy / 2=critical status summary — opt it out of
                // statistics logging by default like other status flags.
                LogByDefault = !isSmart
            });
        }

        return channels;
    }

    private static IReadOnlyList<SensorChannelValue> MarkDefault(IReadOnlyList<SensorChannelValue> channels, string key)
    {
        return channels.Select(channel => channel with
        {
            IsDefault = string.Equals(channel.Key, key, StringComparison.OrdinalIgnoreCase)
        }).ToArray();
    }

    private static string Humanize(string key)
    {
        return key
            .Replace("Percent", " %", StringComparison.OrdinalIgnoreCase)
            .Replace("Used", " used", StringComparison.OrdinalIgnoreCase)
            .Replace("Hours", " hours", StringComparison.OrdinalIgnoreCase);
    }

    private static string Format(double? value)
    {
        return value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-";
    }
}
