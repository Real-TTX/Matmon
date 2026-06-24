using Matmon.Core.Domain;

namespace Matmon.Tests;

public sealed class SensorScheduleDefaultsTests
{
    [Fact]
    public void Ping_polls_every_30_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), SensorScheduleDefaults.Resolve("ping"));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("snmp")]
    [InlineData("ssl-certificate")]
    [InlineData("backup-job")]
    [InlineData("disk-smart")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("totally-unknown-type")]
    public void Everything_except_ping_uses_the_5_minute_default(string? key)
    {
        Assert.Equal(SensorScheduleDefaults.Default, SensorScheduleDefaults.Resolve(key));
    }

    [Fact]
    public void Default_is_five_minutes_and_minimum_is_fifteen_seconds()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), SensorScheduleDefaults.Default);
        Assert.Equal(TimeSpan.FromSeconds(15), SensorScheduleDefaults.Minimum);
    }

    [Fact]
    public void Resolve_never_returns_below_the_minimum()
    {
        Assert.True(SensorScheduleDefaults.Resolve("ping") >= SensorScheduleDefaults.Minimum);
        Assert.True(SensorScheduleDefaults.Resolve("anything") >= SensorScheduleDefaults.Minimum);
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        Assert.Equal(SensorScheduleDefaults.Resolve("ping"), SensorScheduleDefaults.Resolve("PING"));
    }
}
