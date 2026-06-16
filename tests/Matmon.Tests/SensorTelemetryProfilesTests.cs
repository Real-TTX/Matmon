using Matmon.Core.Telemetry;

namespace Matmon.Tests;

public sealed class SensorTelemetryProfilesTests
{
    [Theory]
    [InlineData("ping")]
    [InlineData("http")]
    [InlineData("dns")]
    [InlineData("snmp-interface")]
    public void Latency_sensors_use_responsive_profile(string key)
    {
        Assert.Same(SensorTelemetryProfiles.Responsive, SensorTelemetryProfiles.Resolve(key));
    }

    [Theory]
    [InlineData("tcp-port")]
    [InlineData("ssl-certificate")]
    [InlineData("docker-container")]
    [InlineData("windows-service")]
    public void Availability_sensors_use_availability_profile(string key)
    {
        Assert.Same(SensorTelemetryProfiles.Availability, SensorTelemetryProfiles.Resolve(key));
    }

    [Theory]
    [InlineData("probe-heartbeat")]
    [InlineData("probe-health")]
    public void Probe_sensors_use_infrastructure_profile(string key)
    {
        Assert.Same(SensorTelemetryProfiles.Infrastructure, SensorTelemetryProfiles.Resolve(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("totally-unknown-type")]
    public void Unknown_keys_fall_back_to_general(string? key)
    {
        Assert.Same(SensorTelemetryProfiles.General, SensorTelemetryProfiles.Resolve(key));
    }

    [Fact]
    public void Resolution_is_case_insensitive()
    {
        Assert.Same(SensorTelemetryProfiles.Responsive, SensorTelemetryProfiles.Resolve("PING"));
        Assert.Same(SensorTelemetryProfiles.Availability, SensorTelemetryProfiles.Resolve("TCP-Port"));
    }

    [Fact]
    public void Responsive_keeps_hourly_buckets_for_a_year()
    {
        var profile = SensorTelemetryProfiles.Responsive;
        Assert.Equal(60, profile.StatisticsBucketMinutes);
        Assert.Equal(365, profile.StatisticsRetentionDays);
    }
}
