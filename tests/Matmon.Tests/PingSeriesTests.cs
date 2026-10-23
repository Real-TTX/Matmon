using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>A ping check sends several packets: loss, latency, min/max and jitter are worked out from what came back - and
/// a real check against the loopback address shows the channels a sensor reports.</summary>
public sealed class PingSeriesTests
{
    [Fact]
    public void Loss_latency_and_jitter_come_from_the_packets_that_came_back()
    {
        var series = PingSeries.From([10, null, 14, 12]);

        Assert.Equal(4, series.Sent);
        Assert.Equal(3, series.Received);
        Assert.Equal(25, series.LossPercent);
        Assert.Equal(12, series.Average);
        Assert.Equal(10, series.Min);
        Assert.Equal(14, series.Max);
        Assert.Equal(3, series.Jitter); // |14-10| and |12-14|, consecutive ANSWERED packets: (4 + 2) / 2
        Assert.Equal("latency 12 ms, 1 of 4 lost, jitter 3 ms", series.Describe());
    }

    [Fact]
    public void One_answer_has_no_jitter_and_nothing_back_has_no_latency()
    {
        var single = PingSeries.From([null, 20, null]);
        Assert.Null(single.Jitter);
        Assert.Equal(66.7, single.LossPercent);
        Assert.Equal("latency 20 ms, 2 of 3 lost", single.Describe());

        var none = PingSeries.From([null, null, null]);
        Assert.Equal(0, none.Received);
        Assert.Equal(100, none.LossPercent);
        Assert.Null(none.Average);

        Assert.Equal("latency 5 ms", PingSeries.From([5]).Describe());
    }

    [Fact]
    public async Task A_check_of_the_loopback_address_reports_latency_loss_jitter_and_the_extremes()
    {
        var settings = new MonitoringSettings { Timeout = TimeSpan.FromSeconds(2) };
        var result = await new PingSensorExecutor().ExecuteAsync(new SensorExecutionContext("ping", "127.0.0.1", settings));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal("latency", result.DefaultChannelKey);
        Assert.Equal(["latency", "packetLoss", "jitter", "latencyMin", "latencyMax"], result.Channels.Select(channel => channel.Key));
        Assert.Equal(0, result.Channels.Single(channel => channel.Key == "packetLoss").Value);
    }

    [Fact]
    public async Task One_packet_per_check_is_the_old_ping_just_reachability_and_latency()
    {
        var settings = new MonitoringSettings { Timeout = TimeSpan.FromSeconds(2) };
        settings.Parameters[PingSensorExecutor.CountParameter] = "1";

        var result = await new PingSensorExecutor().ExecuteAsync(new SensorExecutionContext("ping", "127.0.0.1", settings));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal(["latency"], result.Channels.Select(channel => channel.Key));
    }

    [Fact]
    public void A_new_ping_sensor_alarms_on_loss_by_default()
    {
        var settings = new MonitoringSettings();
        SensorThresholdDefaults.Apply("ping", settings);

        Assert.True(SensorThresholdDefaults.TryResolve("ping", "packetLoss", "warning", out var warning));
        Assert.Equal(30, warning.Value);
        Assert.Contains(settings.Thresholds.Keys, key => key.Contains("packetLoss", StringComparison.OrdinalIgnoreCase));
    }
}
