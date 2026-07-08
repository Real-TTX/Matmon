using System.Diagnostics;

namespace Matmon.Core.Domain;

/// <summary>
/// Pending package updates on a Linux host over SSH. Reuses the Linux SSH Health runner with a
/// cross-distro script (apt / dnf / yum / zypper) that counts upgradable packages from the
/// already-refreshed package cache (it does not run a privileged cache refresh).
/// </summary>
public sealed class LinuxUpdateSensorExecutor : ISensorExecutor
{
    private const string UpdateScript = """
upd=0; sec=0
if command -v apt-get >/dev/null 2>&1; then
  sim=$(apt-get -s -o Debug::NoLocking=true upgrade 2>/dev/null)
  upd=$(printf '%s\n' "$sim" | grep -c '^Inst ')
  sec=$(printf '%s\n' "$sim" | grep '^Inst ' | grep -ciE 'security')
elif command -v dnf >/dev/null 2>&1; then
  upd=$(dnf -q check-update 2>/dev/null | grep -cE '^[A-Za-z0-9]')
  sec=$(dnf -q --security check-update 2>/dev/null | grep -cE '^[A-Za-z0-9]')
elif command -v yum >/dev/null 2>&1; then
  upd=$(yum -q check-update 2>/dev/null | grep -cE '^[A-Za-z0-9]')
elif command -v zypper >/dev/null 2>&1; then
  upd=$(zypper -q list-updates 2>/dev/null | grep -c '^v |')
fi
printf 'pendingUpdates=%s\n' "$upd"
printf 'securityUpdates=%s\n' "$sec"
printf 'updatesAvailable=%s\n' "$([ "$upd" -gt 0 ] 2>/dev/null && echo 1 || echo 0)"
""";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "linux-update",
        DisplayName = "Linux Update",
        Description = "Counts pending package updates on a Linux host via SSH (apt / dnf / yum / zypper).",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters = LinuxSshHealthSensorExecutor.Definition.Parameters
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
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(30);
        var watch = Stopwatch.StartNew();

        try
        {
            using var process = LinuxSshHealthSensorExecutor.StartSshProcess(
                context.Target.Trim(), username.Trim(), port, context.Settings, timeout, UpdateScript);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            // Fires on both caller cancellation and the timeout deadline, so a hung ssh child is
            // always killed (Dispose alone does not terminate a still-running process).
            using var registration = timeoutCts.Token.Register(() =>
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

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
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
                return SensorExecutionResult.Critical(watch.Elapsed, "no update data reported (unsupported package manager?)");
            }

            var pending = (int)(ChannelValue(channels, "pendingUpdates") ?? 0);
            var security = (int)(ChannelValue(channels, "securityUpdates") ?? 0);
            var state = pending > 0 ? SensorState.Warning : SensorState.Healthy;
            var message2 = pending > 0
                ? $"{pending} update(s) pending" + (security > 0 ? $" ({security} security)" : string.Empty)
                : "up to date";

            var marked = MarkDefault(channels, "pendingUpdates");
            var defaultValue = ChannelValue(channels, "pendingUpdates");
            var result = state == SensorState.Warning
                ? SensorExecutionResult.Warning(watch.Elapsed, message2, defaultValue, "pendingUpdates", marked)
                : SensorExecutionResult.Healthy(watch.Elapsed, message2, defaultValue, "pendingUpdates", marked);

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked CTS fired via CancelAfter - a timeout, not a caller cancellation.
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

    private static double? ChannelValue(IReadOnlyList<SensorChannelValue> channels, string key) =>
        channels.FirstOrDefault(channel => channel.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;

    private static IReadOnlyList<SensorChannelValue> MarkDefault(IReadOnlyList<SensorChannelValue> channels, string key) =>
        channels.Select(channel => channel with
        {
            IsDefault = string.Equals(channel.Key, key, StringComparison.OrdinalIgnoreCase)
        }).ToArray();
}
