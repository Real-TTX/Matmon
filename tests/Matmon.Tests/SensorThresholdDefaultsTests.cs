using Matmon.Core.Domain;

namespace Matmon.Tests;

public sealed class SensorThresholdDefaultsTests
{
    [Fact]
    public void Resolves_a_known_percent_default()
    {
        Assert.True(SensorThresholdDefaults.TryResolve("windows-health", "cpuLoad", "warning", out var warn));
        Assert.Equal(ThresholdDirection.Above, warn.Direction);
        Assert.Equal(85, warn.Value);

        Assert.True(SensorThresholdDefaults.TryResolve("windows-health", "cpuLoad", "critical", out var crit));
        Assert.Equal(95, crit.Value);
    }

    [Fact]
    public void Resolves_a_below_direction_default()
    {
        Assert.True(SensorThresholdDefaults.TryResolve("ups-snmp", "battery_charge", "warning", out var warn));
        Assert.Equal(ThresholdDirection.Below, warn.Direction);
        Assert.Equal(50, warn.Value);
    }

    [Fact]
    public void Is_case_insensitive_on_type()
    {
        Assert.True(SensorThresholdDefaults.TryResolve("PING", "latency", "critical", out var rule));
        Assert.Equal(200, rule.Value);
    }

    [Fact]
    public void Unknown_type_or_channel_resolves_nothing()
    {
        Assert.False(SensorThresholdDefaults.TryResolve("no-such-type", "latency", "warning", out _));
        Assert.False(SensorThresholdDefaults.TryResolve("ping", "no-such-channel", "warning", out _));
        Assert.False(SensorThresholdDefaults.TryResolve(null, "latency", "warning", out _));
    }

    [Fact]
    public void Apply_seeds_the_types_default_thresholds()
    {
        var settings = new MonitoringSettings();

        SensorThresholdDefaults.Apply("windows-health", settings);

        Assert.True(MonitoringSettings.TryReadChannelThreshold(settings, "cpuLoad", "warning", out var warn));
        Assert.Equal(85, warn.Value);
        Assert.True(MonitoringSettings.TryReadChannelThreshold(settings, "diskUsedPercent", "critical", out var crit));
        Assert.Equal(95, crit.Value);
    }

    [Fact]
    public void Apply_never_overwrites_an_existing_threshold()
    {
        var settings = new MonitoringSettings();
        MonitoringSettings.SetChannelThreshold(settings, "cpuLoad", "warning",
            new ThresholdRule(ThresholdDirection.Above, 70));

        SensorThresholdDefaults.Apply("windows-health", settings);

        Assert.True(MonitoringSettings.TryReadChannelThreshold(settings, "cpuLoad", "warning", out var warn));
        Assert.Equal(70, warn.Value); // user's value preserved, not overwritten with the default 85
    }

    [Fact]
    public void Apply_on_unknown_type_is_a_no_op()
    {
        var settings = new MonitoringSettings();
        SensorThresholdDefaults.Apply("no-such-type", settings);
        SensorThresholdDefaults.Apply(null, settings);
        // nothing added - no throw, no thresholds
        Assert.False(MonitoringSettings.TryReadChannelThreshold(settings, "cpuLoad", "warning", out _));
    }
}
