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
    [InlineData("dns")]
    [InlineData("windows-health")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("totally-unknown-type")]
    public void Current_data_sensors_use_the_5_minute_default(string? key)
    {
        Assert.Equal(SensorScheduleDefaults.Default, SensorScheduleDefaults.Resolve(key));
    }

    [Theory]
    [InlineData("synology-disk")]
    [InlineData("windows-disk")]
    [InlineData("linux-disk")]
    [InlineData("proxmox-disk")]
    [InlineData("backup-job")]
    public void Slow_changing_infra_polls_every_six_hours(string key)
    {
        Assert.Equal(TimeSpan.FromHours(6), SensorScheduleDefaults.Resolve(key));
    }

    [Theory]
    [InlineData("windows-update")]
    [InlineData("linux-update")]
    [InlineData("synology-update")]
    [InlineData("ssl-certificate")]
    [InlineData("certificate-chain")]
    public void Rarely_changing_sensors_poll_once_a_day(string key)
    {
        Assert.Equal(TimeSpan.FromHours(24), SensorScheduleDefaults.Resolve(key));
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
        Assert.Equal(SensorScheduleDefaults.Resolve("windows-update"), SensorScheduleDefaults.Resolve("WINDOWS-UPDATE"));
    }
}
