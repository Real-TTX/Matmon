using Matmon.Core.Domain;

namespace Matmon.Tests;

public class SensorThresholdEvaluatorTests
{
    private static MonitoringSettings SettingsWithCriticalCpu(double threshold)
    {
        var settings = new MonitoringSettings();
        MonitoringSettings.SetChannelThreshold(
            settings, "cpu", "critical", new ThresholdRule(ThresholdDirection.AboveOrEqual, threshold));
        return settings;
    }

    private static SensorExecutionResult HealthyWithCpu(double value)
    {
        return new SensorExecutionResult(SensorState.Healthy, TimeSpan.Zero, value)
        {
            DefaultChannelKey = "cpu",
            Channels = new[]
            {
                new SensorChannelValue { Key = "cpu", Label = "CPU", Value = value, State = SensorState.Healthy }
            }
        };
    }

    [Fact]
    public void Breaching_critical_threshold_escalates_sensor_and_channel()
    {
        var settings = SettingsWithCriticalCpu(90);
        var result = HealthyWithCpu(95);

        var adjusted = SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);

        Assert.Equal(SensorState.Critical, adjusted.State);
        var channel = Assert.Single(adjusted.Channels);
        Assert.Equal(SensorState.Critical, channel.State);
        Assert.Contains("error", channel.Message);
    }

    [Fact]
    public void Warning_threshold_escalates_only_to_warning()
    {
        var settings = new MonitoringSettings();
        MonitoringSettings.SetChannelThreshold(
            settings, "cpu", "warning", new ThresholdRule(ThresholdDirection.AboveOrEqual, 80));
        var result = HealthyWithCpu(85);

        var adjusted = SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);

        Assert.Equal(SensorState.Warning, adjusted.State);
    }

    [Fact]
    public void Value_below_threshold_stays_healthy()
    {
        var settings = SettingsWithCriticalCpu(90);
        var result = HealthyWithCpu(40);

        var adjusted = SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);

        Assert.Equal(SensorState.Healthy, adjusted.State);
        Assert.Equal(SensorState.Healthy, Assert.Single(adjusted.Channels).State);
    }

    [Fact]
    public void Paused_sensor_is_not_escalated_even_when_channel_breaches()
    {
        var settings = SettingsWithCriticalCpu(90);
        var result = new SensorExecutionResult(SensorState.Paused, TimeSpan.Zero, 95)
        {
            DefaultChannelKey = "cpu",
            Channels = new[]
            {
                new SensorChannelValue { Key = "cpu", Value = 95, State = SensorState.Healthy }
            }
        };

        var adjusted = SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);

        Assert.Equal(SensorState.Paused, adjusted.State);
    }

    [Fact]
    public void Result_without_channels_is_returned_unchanged()
    {
        var settings = SettingsWithCriticalCpu(90);
        var result = new SensorExecutionResult(SensorState.Healthy, TimeSpan.Zero, 95);

        var adjusted = SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);

        Assert.Same(result, adjusted);
    }
}
