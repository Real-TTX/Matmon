using System.Diagnostics;

namespace Matmon.Core.Domain;

/// <summary>
/// Available DSM update on a Synology NAS over SSH (<c>synoupgrade --check</c>). Reuses the Linux
/// SSH Health runner. Requires SSH enabled on DSM and an account allowed to run synoupgrade
/// (typically the admin / a sudo-capable user).
/// </summary>
public sealed class SynologyUpdateSensorExecutor : ISensorExecutor
{
    private const string UpdateScript = """
res=$( (synoupgrade --check 2>&1) || (/usr/syno/bin/synoupgrade --check 2>&1) )
avail=0
printf '%s' "$res" | grep -iqE 'available update|can be updated|update is available' && avail=1
printf '%s' "$res" | grep -iqE 'no available|latest version|up.to.date|no upgrade' && avail=0
printf 'updatesAvailable=%s\n' "$avail"
""";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "synology-update",
        DisplayName = "Synology Update",
        Description = "Checks for an available DSM update on a Synology NAS over SSH (synoupgrade --check). SSH must be enabled.",
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
            var available = channels.FirstOrDefault(channel => channel.Key.Equals("updatesAvailable", StringComparison.OrdinalIgnoreCase))?.Value;
            if (available is null)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "no update data reported (is synoupgrade available to this account?)");
            }

            var state = available >= 1 ? SensorState.Warning : SensorState.Healthy;
            var message2 = available >= 1 ? "DSM update available" : "DSM up to date";
            var marked = channels.Select(channel => channel with
            {
                IsDefault = channel.Key.Equals("updatesAvailable", StringComparison.OrdinalIgnoreCase)
            }).ToArray();

            var result = state == SensorState.Warning
                ? SensorExecutionResult.Warning(watch.Elapsed, message2, available, "updatesAvailable", marked)
                : SensorExecutionResult.Healthy(watch.Elapsed, message2, available, "updatesAvailable", marked);

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked CTS fired via CancelAfter — a timeout, not a caller cancellation.
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
}
