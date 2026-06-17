using Matmon.Core.Domain;

namespace Matmon.Tests;

public sealed class SensorScheduleDefaultsTests
{
    [Theory]
    [InlineData("ping", 30)]
    [InlineData("ssl-certificate", 6 * 60 * 60)]
    [InlineData("backup-job", 30 * 60)]
    [InlineData("disk-smart", 15 * 60)]
    public void Resolve_returns_type_specific_interval(string key, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SensorScheduleDefaults.Resolve(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("totally-unknown-type")]
    public void Resolve_falls_back_to_default_for_unknown(string? key)
    {
        Assert.Equal(SensorScheduleDefaults.Default, SensorScheduleDefaults.Resolve(key));
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        Assert.Equal(SensorScheduleDefaults.Resolve("ping"), SensorScheduleDefaults.Resolve("PING"));
    }
}
