using Matmon.Core.Domain;

namespace Matmon.Tests;

public class MonitoringSettingsInheritanceTests
{
    [Fact]
    public void ApplyFrom_overlays_source_values_over_existing()
    {
        var target = new MonitoringSettings { Enabled = true, RetryCount = 1 };
        var source = new MonitoringSettings { Enabled = false, Timeout = TimeSpan.FromSeconds(5) };

        target.ApplyFrom(source);

        Assert.False(target.Enabled);                       // source wins when present
        Assert.Equal(1, target.RetryCount);                 // untouched by source
        Assert.Equal(TimeSpan.FromSeconds(5), target.Timeout);
    }

    [Fact]
    public void ApplyFrom_interval_and_schedule_are_mutually_exclusive()
    {
        var target = new MonitoringSettings
        {
            PollingSchedule = new MonitoringSchedule { Mode = MonitoringScheduleMode.Daily }
        };
        var source = new MonitoringSettings { PollingInterval = TimeSpan.FromSeconds(30) };

        target.ApplyFrom(source);

        Assert.Equal(TimeSpan.FromSeconds(30), target.PollingInterval);
        Assert.Null(target.PollingSchedule);
    }

    [Fact]
    public void ApplyFrom_merges_parameters_with_source_priority()
    {
        var target = new MonitoringSettings();
        target.Parameters["a"] = "1";
        target.Parameters["shared"] = "old";

        var source = new MonitoringSettings();
        source.Parameters["b"] = "2";
        source.Parameters["shared"] = "new";

        target.ApplyFrom(source);

        Assert.Equal("1", target.Parameters["a"]);
        Assert.Equal("2", target.Parameters["b"]);
        Assert.Equal("new", target.Parameters["shared"]);
    }

    [Fact]
    public void StripInheritedValues_removes_values_equal_to_inherited()
    {
        var inherited = new MonitoringSettings { Enabled = true, Timeout = TimeSpan.FromSeconds(10) };
        var target = new MonitoringSettings { Enabled = true, Timeout = TimeSpan.FromSeconds(5) };

        MonitoringSettings.StripInheritedValues(target, inherited);

        Assert.Null(target.Enabled);                        // equal -> stripped
        Assert.Equal(TimeSpan.FromSeconds(5), target.Timeout); // different -> kept
    }

    [Fact]
    public void StripInheritedValues_strips_only_matching_thresholds()
    {
        var inherited = new MonitoringSettings();
        inherited.Thresholds["channel:cpu:warning"] = ">= 80";

        var target = new MonitoringSettings();
        target.Thresholds["channel:cpu:warning"] = ">= 80";  // same -> stripped
        target.Thresholds["channel:cpu:critical"] = ">= 95"; // unique -> kept

        MonitoringSettings.StripInheritedValues(target, inherited);

        Assert.False(target.Thresholds.ContainsKey("channel:cpu:warning"));
        Assert.True(target.Thresholds.ContainsKey("channel:cpu:critical"));
    }

    [Fact]
    public void Clone_produces_independent_copy()
    {
        var original = new MonitoringSettings { Enabled = true };
        original.Parameters["host"] = "example";

        var clone = original.Clone();
        clone.Parameters["host"] = "changed";

        Assert.Equal("example", original.Parameters["host"]);
        Assert.True(clone.Enabled);
    }
}
