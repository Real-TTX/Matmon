using Matmon.Core.Domain;

namespace Matmon.Tests;

public class MonitoringSettingsThresholdTests
{
    [Theory]
    [InlineData(">= 90", ThresholdDirection.AboveOrEqual, 90d)]
    [InlineData("<= 10", ThresholdDirection.BelowOrEqual, 10d)]
    [InlineData("> 5", ThresholdDirection.Above, 5d)]
    [InlineData("< 5", ThresholdDirection.Below, 5d)]
    [InlineData("= 42", ThresholdDirection.Equal, 42d)]
    [InlineData("== 42", ThresholdDirection.Equal, 42d)]
    [InlineData("<> 7", ThresholdDirection.NotEqual, 7d)]
    [InlineData("!= 7", ThresholdDirection.NotEqual, 7d)]
    public void TryParseThresholdRule_parses_symbol_and_value(string raw, ThresholdDirection direction, double value)
    {
        Assert.True(MonitoringSettings.TryParseThresholdRule(raw, out var rule));
        Assert.Equal(direction, rule.Direction);
        Assert.Equal(value, rule.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notathreshold")]
    [InlineData(">= abc")]
    public void TryParseThresholdRule_rejects_invalid(string? raw)
    {
        Assert.False(MonitoringSettings.TryParseThresholdRule(raw, out _));
    }

    [Fact]
    public void FormatThresholdRule_roundtrips_through_parser()
    {
        var rule = new ThresholdRule(ThresholdDirection.AboveOrEqual, 90.5);
        var formatted = MonitoringSettings.FormatThresholdRule(rule);

        Assert.Equal(">= 90.5", formatted);
        Assert.True(MonitoringSettings.TryParseThresholdRule(formatted, out var parsed));
        Assert.Equal(rule, parsed);
    }

    [Theory]
    [InlineData(ThresholdDirection.Above, 90d, 91d, true)]
    [InlineData(ThresholdDirection.Above, 90d, 90d, false)]
    [InlineData(ThresholdDirection.AboveOrEqual, 90d, 90d, true)]
    [InlineData(ThresholdDirection.Below, 10d, 9d, true)]
    [InlineData(ThresholdDirection.Below, 10d, 10d, false)]
    [InlineData(ThresholdDirection.BelowOrEqual, 10d, 10d, true)]
    [InlineData(ThresholdDirection.Equal, 5d, 5d, true)]
    [InlineData(ThresholdDirection.Equal, 5d, 5.5d, false)]
    [InlineData(ThresholdDirection.NotEqual, 5d, 6d, true)]
    [InlineData(ThresholdDirection.NotEqual, 5d, 5d, false)]
    public void IsThresholdBreached_evaluates_each_direction(
        ThresholdDirection direction,
        double threshold,
        double value,
        bool expected)
    {
        var rule = new ThresholdRule(direction, threshold);
        Assert.Equal(expected, MonitoringSettings.IsThresholdBreached(rule, value));
    }

    [Fact]
    public void Channel_threshold_roundtrips_via_set_and_read()
    {
        var settings = new MonitoringSettings();
        var rule = new ThresholdRule(ThresholdDirection.AboveOrEqual, 80);

        MonitoringSettings.SetChannelThreshold(settings, "cpu", "warning", rule);

        Assert.True(MonitoringSettings.TryReadChannelThreshold(settings, "cpu", "warning", out var read));
        Assert.Equal(rule, read);
        Assert.False(MonitoringSettings.TryReadChannelThreshold(settings, "cpu", "critical", out _));
    }

    [Fact]
    public void BuildChannelThresholdKey_rejects_unknown_severity()
    {
        Assert.Throws<ArgumentException>(() =>
            MonitoringSettings.BuildChannelThresholdKey("cpu", "fatal"));
    }
}
