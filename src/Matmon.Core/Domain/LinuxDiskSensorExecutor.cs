using System.Diagnostics;
using System.Globalization;

namespace Matmon.Core.Domain;

/// <summary>
/// Detailed per-disk health of a Linux host over SSH. Where the compact Linux SSH Health
/// sensor only rolls up a single SMART summary, this emits one SMART status (+ temperature)
/// channel per physical disk and a usage channel per mounted filesystem, plus an overall
/// SMART status and the hottest-disk temperature. Reuses the Linux SSH Health runner.
/// </summary>
public sealed class LinuxDiskSensorExecutor : ISensorExecutor
{
    private const string DiskScript = """
i=0; worst=0; maxtemp=
for d in $(lsblk -dno NAME,TYPE 2>/dev/null | awk '$2=="disk"{print $1}'); do
  i=$((i+1))
  out=$(smartctl -H -A "/dev/$d" 2>/dev/null)
  sev=0
  if echo "$out" | grep -iqE 'FAILED|FAILING'; then sev=2; fi
  printf 'disk%dStatus=%s\n' "$i" "$sev"
  temp=$(echo "$out" | awk '/Temperature_Celsius/{print $10; exit}')
  if [ -z "$temp" ]; then temp=$(echo "$out" | awk -F: '/Current Drive Temperature/{gsub(/[^0-9]/,"",$2); print $2; exit}'); fi
  if [ -n "$temp" ]; then
    printf 'disk%dTemp=%s\n' "$i" "$temp"
    if [ -z "$maxtemp" ] || { [ "$temp" -gt "$maxtemp" ] 2>/dev/null; }; then maxtemp=$temp; fi
  fi
  if [ "$sev" -gt "$worst" ]; then worst=$sev; fi
done
df -P 2>/dev/null | awk 'NR>1 && $1 ~ /^\/dev\// { gsub("%","",$5); m=$6; if(m=="/") m="root"; gsub("[^A-Za-z0-9]","_",m); printf "mount_%s_UsedPercent=%s\n", m, $5 }'
printf 'diskCount=%s\n' "$i"
printf 'smartStatus=%s\n' "$worst"
if [ -n "$maxtemp" ]; then printf 'maxTemperature=%s\n' "$maxtemp"; fi
""";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "linux-disk",
        DisplayName = "Linux Disk",
        Description = "Per-disk SMART status and temperature plus per-filesystem usage of a Linux host via SSH (smartctl + df).",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = LinuxSshHealthSensorExecutor.Definition.Parameters
    };

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

        var settings = new MonitoringSettings { DefaultChannelKey = "smartStatus" };
        settings.Parameters["ssh.port"] = "22";
        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Linux Disk",
                    context.Host,
                    settings,
                    "SSH port 22 is open. Linux credentials can be inherited from the parent.",
                    66)));
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
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(15);
        var watch = Stopwatch.StartNew();

        try
        {
            using var process = LinuxSshHealthSensorExecutor.StartSshProcess(
                context.Target.Trim(), username.Trim(), port, context.Settings, timeout, DiskScript);
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

            var channels = LinuxSshHealthSensorExecutor.ParseKeyValueChannels(stdout);
            if (channels.Count == 0)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "no disks reported (is smartctl installed and the account allowed to read SMART?)");
            }

            var state = DetermineState(channels);
            var message2 = BuildMessage(state, channels);
            var defaultChannel = channels.FirstOrDefault(channel => channel.Key.Equals("maxTemperature", StringComparison.OrdinalIgnoreCase) && channel.Value.HasValue)
                ?? channels.FirstOrDefault(channel => channel.Key.Equals("smartStatus", StringComparison.OrdinalIgnoreCase))
                ?? channels[0];
            var marked = MarkDefault(channels, defaultChannel.Key);

            var result = state switch
            {
                SensorState.Critical => SensorExecutionResult.Critical(watch.Elapsed, message2, defaultChannel.Value, defaultChannel.Key, marked),
                SensorState.Warning => SensorExecutionResult.Warning(watch.Elapsed, message2, defaultChannel.Value, defaultChannel.Key, marked),
                _ => SensorExecutionResult.Healthy(watch.Elapsed, message2, defaultChannel.Value, defaultChannel.Key, marked)
            };

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
            return SensorExecutionResult.Unknown("ssh cancelled");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static SensorState DetermineState(IReadOnlyList<SensorChannelValue> channels)
    {
        var smart = channels.FirstOrDefault(channel => channel.Key.Equals("smartStatus", StringComparison.OrdinalIgnoreCase))?.Value;
        if (smart is >= 2)
        {
            return SensorState.Critical;
        }

        var mountWarning = channels.Any(channel =>
            channel.Key.StartsWith("mount_", StringComparison.OrdinalIgnoreCase) &&
            channel.Key.EndsWith("UsedPercent", StringComparison.OrdinalIgnoreCase) &&
            channel.Value is >= 90);

        if (smart is >= 1 || mountWarning)
        {
            return SensorState.Warning;
        }

        return SensorState.Healthy;
    }

    private static string BuildMessage(SensorState state, IReadOnlyList<SensorChannelValue> channels)
    {
        var diskCount = channels.FirstOrDefault(channel => channel.Key.Equals("diskCount", StringComparison.OrdinalIgnoreCase))?.Value;
        var maxTemp = channels.FirstOrDefault(channel => channel.Key.Equals("maxTemperature", StringComparison.OrdinalIgnoreCase))?.Value;
        var tempText = maxTemp.HasValue ? $", up to {maxTemp.Value:0.#}°C" : string.Empty;
        var count = diskCount.HasValue ? (int)diskCount.Value : channels.Count(channel => channel.Key.EndsWith("Status", StringComparison.OrdinalIgnoreCase));

        return state switch
        {
            SensorState.Critical => $"disks critical - SMART failure reported{tempText}",
            SensorState.Warning => $"disks warning - check SMART or filesystem usage{tempText}",
            _ => $"disks ok - {count} healthy{tempText}"
        };
    }

    private static IReadOnlyList<SensorChannelValue> MarkDefault(IReadOnlyList<SensorChannelValue> channels, string key)
    {
        return channels.Select(channel => channel with
        {
            IsDefault = string.Equals(channel.Key, key, StringComparison.OrdinalIgnoreCase)
        }).ToArray();
    }
}
