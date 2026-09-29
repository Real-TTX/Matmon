using Matmon.Core.Domain;

namespace Matmon.Tests;

// On a Linux probe (an agent) whose target is its own machine the SSH sensors run their script in a local
// shell - no SSH server, key or username. Live only on Linux; a no-op elsewhere.
public sealed class LocalLinuxShellTests
{
    [Fact]
    public async Task LinuxHealthRunsLocallyWithoutSshOrUsername()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var settings = new MonitoringSettings { Timeout = TimeSpan.FromSeconds(30) };

        var result = await new LinuxSshHealthSensorExecutor().ExecuteAsync(new SensorExecutionContext("linux-ssh-health", "localhost", settings));

        Assert.True(result.State != SensorState.Critical, $"{result.State}: {result.Message} / channels: {string.Join(",", result.Channels.Select(c => c.Key))}");
        Assert.Contains(result.Channels, channel => channel.Key.Contains("mem", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("ssh", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AScriptWithWindowsLineEndsStillRuns()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Exactly what a browser posts from a textarea: CRLF line ends.
        var settings = new MonitoringSettings { Timeout = TimeSpan.FromSeconds(30) };
        settings.Parameters["local.shell"] = "bash";
        settings.Parameters["outputFormat"] = "text";
        settings.Parameters["script"] = string.Join("\r\n", "total=0", "for i in 1 2 3; do", "  total=$((total + i))", "done", "echo \"sum=$total\"");

        var result = await new LocalScriptSensorExecutor().ExecuteAsync(new SensorExecutionContext("local-script", string.Empty, settings));

        Assert.True(result.State != SensorState.Critical, result.Message);
        Assert.Contains(result.Channels, channel => channel.Value == 6);
    }

    [Fact]
    public async Task ARemoteTargetStillNeedsAUsername()
    {
        var result = await new LinuxSshHealthSensorExecutor().ExecuteAsync(
            new SensorExecutionContext("linux-ssh-health", "nas.example.invalid", new MonitoringSettings()));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("username", result.Message);
    }
}
