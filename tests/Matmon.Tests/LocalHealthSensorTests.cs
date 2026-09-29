using Matmon.Core.Domain;
using Matmon.Core.Telemetry;

namespace Matmon.Tests;

// The agent's own machine, read from the OS. The parsers are pinned with real /proc samples; the executor is
// run for real on whatever machine runs the tests - it is a local read, so it can be.
public sealed class LocalHealthSensorTests
{
    [Fact]
    public void ProcStatIsReadAsIdleAndTotalWithIowaitAsIdle()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice
        var parsed = LocalHealthSensorExecutor.ParseProcStat("cpu  4705 356 584 3699176 23060 0 277 0 0 0");

        Assert.Equal((3699176UL + 23060UL, 4705UL + 356 + 584 + 3699176 + 23060 + 0 + 277 + 0), parsed);
        Assert.Null(LocalHealthSensorExecutor.ParseProcStat("cpu0 1 2 3 4"));
        Assert.Null(LocalHealthSensorExecutor.ParseProcStat("intr 12345"));
        Assert.Null(LocalHealthSensorExecutor.ParseProcStat(null));
    }

    [Theory]
    [InlineData(100, 1000, 150, 1100, 50)]   // 100 jiffies passed, 50 of them idle
    [InlineData(0, 0, 100, 100, 0)]          // fully idle
    [InlineData(0, 0, 0, 100, 100)]          // fully busy
    public void CpuPercentIsTheBusyShareBetweenTwoReadings(ulong idle1, ulong total1, ulong idle2, ulong total2, double expected)
    {
        Assert.Equal(expected, LocalHealthSensorExecutor.CpuPercent((idle1, total1), (idle2, total2)));
    }

    [Fact]
    public void NoTimePassedIsNoReading()
    {
        Assert.Null(LocalHealthSensorExecutor.CpuPercent((10, 100), (10, 100)));
    }

    [Fact]
    public void MemInfoUsesAvailableNotFree()
    {
        var parsed = LocalHealthSensorExecutor.ParseMemInfo("""
            MemTotal:       16303412 kB
            MemFree:          403908 kB
            MemAvailable:    9733116 kB
            Buffers:          612216 kB
            """);

        Assert.Equal((16303412UL * 1024, 9733116UL * 1024), parsed);
        Assert.Null(LocalHealthSensorExecutor.ParseMemInfo("MemTotal: 100 kB"));
    }

    [Theory]
    [InlineData(@"C:\", "c")]
    [InlineData("/", "root")]
    [InlineData("/var/lib/docker", "var_lib_docker")]
    [InlineData("/mnt/Data Disk/", "mnt_data_disk")]
    public void DiskKeysCarryNoSeparators(string root, string expected)
    {
        Assert.Equal(expected, LocalHealthSensorExecutor.DiskKey(root));
    }

    [Fact]
    public async Task ItMeasuresTheMachineItRunsOn()
    {
        var result = await new LocalHealthSensorExecutor().ExecuteAsync(new SensorExecutionContext("local-health", string.Empty, new MonitoringSettings()));

        var cpu = Assert.Single(result.Channels, channel => channel.Key == "cpu");
        Assert.InRange(cpu.Value!.Value, 0, 100);
        Assert.InRange(Assert.Single(result.Channels, channel => channel.Key == "memoryUsedPercent").Value!.Value, 0, 100);
        Assert.Contains(result.Channels, channel => channel.Key.StartsWith("disk.", StringComparison.Ordinal));
        Assert.Equal("cpu", result.DefaultChannelKey);
        Assert.Contains(Environment.MachineName, result.Message);

        // The whole point of the key choice: the channel families find these without a mapping table.
        Assert.Equal("cpu", ChannelFamilies.Pick(result.Channels, ChannelFamilies.Parse("family:cpu")!)?.Key);
        Assert.Equal("memoryUsedPercent", ChannelFamilies.Pick(result.Channels, ChannelFamilies.Parse("family:memory")!)?.Key);
    }

    [Fact]
    public void ItHasSensibleDefaultsAndACategory()
    {
        Assert.Equal("Probe", SensorTypeCategories.Resolve("local-health"));
        Assert.Equal(TimeSpan.FromMinutes(1), SensorScheduleDefaults.Resolve("local-health"));
        Assert.True(SensorThresholdDefaults.TryResolve("local-health", "cpu", "warning", out _));
    }
}
