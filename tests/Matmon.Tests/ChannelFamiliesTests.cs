using Matmon.Core.Domain;
using Matmon.Core.Telemetry;

namespace Matmon.Tests;

// "CPU of these five machines" only works if each machine's own CPU channel is found, whatever its sensor
// type calls it - and if nothing with a different unit sneaks onto the same scale.
public sealed class ChannelFamiliesTests
{
    private static readonly ChannelFamily Cpu = ChannelFamilies.Parse("family:cpu")!;
    private static readonly ChannelFamily Memory = ChannelFamilies.Parse("family:memory")!;

    private static SensorChannelValue Channel(string key, double value, string? unit = null, SensorMeasurementKind kind = SensorMeasurementKind.Unknown, bool isVirtual = false) =>
        new() { Key = key, Value = value, Unit = unit, MeasurementKind = kind, IsVirtual = isVirtual };

    [Theory]
    [InlineData("cpuLoad")]          // Windows health
    [InlineData("cpu")]              // Proxmox, VMware
    [InlineData("cpuUtilization")]   // Synology
    [InlineData("CPU_Usage")]        // a script sensor
    public void EachSensorTypesOwnNameIsFound(string key)
    {
        var picked = ChannelFamilies.Pick([Channel("latency", 3, "ms"), Channel(key, 42, "%")], Cpu);

        Assert.Equal(key, picked?.Key);
    }

    [Fact]
    public void TheMachinesOwnValueBeatsOneOfItsGuests()
    {
        // A Proxmox node reports its own CPU and one per VM - the node is the line you asked for.
        var picked = ChannelFamilies.Pick([Channel("vm.101.cpu", 90, "%"), Channel("cpu", 12, "%"), Channel("ct.200.cpu", 50, "%")], Cpu);

        Assert.Equal("cpu", picked?.Key);
    }

    [Fact]
    public void AGuestsValueIsStillBetterThanNothing()
    {
        Assert.Equal("vm.101.cpu", ChannelFamilies.Pick([Channel("vm.101.cpu", 90, "%")], Cpu)?.Key);
    }

    [Fact]
    public void ADifferentUnitNeverJoinsTheScale()
    {
        Assert.Null(ChannelFamilies.Pick([Channel("memory", 8_000_000_000, "B", SensorMeasurementKind.Bytes)], Memory));
        Assert.Null(ChannelFamilies.Pick([Channel("memory", 8192, "MB")], Memory));
        Assert.Equal("memoryUsedPercent", ChannelFamilies.Pick(
            [Channel("memory", 8192, "MB"), Channel("memoryUsedPercent", 61, "%", SensorMeasurementKind.Percent)], Memory)?.Key);
    }

    [Fact]
    public void ABareNumberIsGivenTheBenefitOfTheDoubt()
    {
        Assert.Equal("cpuLoad", ChannelFamilies.Pick([Channel("cpuLoad", 11)], Cpu)?.Key);
    }

    [Fact]
    public void VirtualAndEmptyChannelsAreSkipped()
    {
        Assert.Null(ChannelFamilies.Pick([Channel("cpu", 1, isVirtual: true), new SensorChannelValue { Key = "cpuLoad", Unit = "%" }], Cpu));
    }

    [Fact]
    public void FreeSpaceStandsInForUsedSpaceInverted()
    {
        var disk = ChannelFamilies.Parse("family:disk")!;

        // Windows health reports only the free share of the system drive.
        var derived = ChannelFamilies.Match([Channel("systemdrivefreepercent", 5.2), Channel("cpuload", 30)], disk);
        Assert.NotNull(derived);
        Assert.True(derived.Complement);
        Assert.Equal(94.8, ChannelFamilies.ValueOf(derived, 5.2), 6);

        // A real "used" channel always wins over the derived one.
        var direct = ChannelFamilies.Match([Channel("storageFreePercent", 40, "%"), Channel("diskUsedPercent", 61, "%")], disk);
        Assert.False(direct!.Complement);
        Assert.Equal("diskUsedPercent", direct.Channel.Key);

        // Pick stays direct-only; families without a complement never derive.
        Assert.Null(ChannelFamilies.Pick([Channel("systemdrivefreepercent", 5.2)], disk));
        Assert.Null(ChannelFamilies.Match([Channel("memoryFreePercent", 40, "%")], Memory));
    }

    [Fact]
    public void ACpuFanIsNotACpu()
    {
        Assert.Null(ChannelFamilies.Pick([Channel("cpuFanStatusOk", 1, kind: SensorMeasurementKind.Boolean)], Cpu));
    }

    [Theory]
    [InlineData("family:cpu", "cpu")]
    [InlineData(" FAMILY:Memory ", "memory")]
    [InlineData("cpuLoad", null)]
    [InlineData("family:nope", null)]
    [InlineData(null, null)]
    public void TokensParse(string? token, string? expected)
    {
        Assert.Equal(expected, ChannelFamilies.Parse(token)?.Key);
    }

    [Fact]
    public void TimeLinePlacesPointsByTimeNotByIndex()
    {
        // Two points in the LAST quarter of the window must not be stretched across the whole chart.
        var path = SparklineGeometry.TimeLine([(0.75, 10), (1.0, 20)], 0, 40);

        Assert.StartsWith("M 75 ", path);
        Assert.Contains(" L 100 ", path);
        Assert.Null(SparklineGeometry.TimeLine([(0.5, 1)], 0, 1));
    }

    [Theory]
    [InlineData(3.2, 52.6, true, 0, 60)]
    [InlineData(12, 13, true, 12, 13)]
    [InlineData(80, 99.5, true, 80, 100)]
    [InlineData(1.2, 380, false, 0, 400)]
    [InlineData(-4, 17, false, -5, 20)]
    public void NiceScaleRoundsToReadableBounds(double min, double max, bool percent, double expectedMin, double expectedMax)
    {
        var (niceMin, niceMax) = SparklineGeometry.NiceScale(min, max, percent);

        Assert.Equal(expectedMin, niceMin, 6);
        Assert.Equal(expectedMax, niceMax, 6);
    }

    [Fact]
    public void DownsampleAveragesTimeSlicesAndKeepsASpikesWeight()
    {
        // 1000 readings at 10 with one 1000-spike: 10 slices of 100 points; the spike's slice must show it.
        var points = Enumerable.Range(0, 1000).Select(i => (X: i / 1000d, Value: i == 505 ? 1000d : 10d)).ToArray();

        var down = SparklineGeometry.Downsample(points, 10);

        Assert.Equal(10, down.Count);
        Assert.True(down[5].Value > 10, "averaging keeps the spike in its slice rather than dropping it");
        Assert.Equal(10, down[0].Value, 6);
        Assert.True(down.Zip(down.Skip(1)).All(pair => pair.First.X < pair.Second.X));
        Assert.Same(points, SparklineGeometry.Downsample(points, 5000));
    }

    [Fact]
    public void AFlatPercentLineStillGetsARangeInsideZeroToHundred()
    {
        var (min, max) = SparklineGeometry.NiceScale(100, 100, percent: true);

        Assert.True(max > min);
        Assert.True(min >= 0 && max <= 100);
    }
}
