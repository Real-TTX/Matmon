using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>Interface traffic as a rate: the counters' change since the last check, in Mbit/s and as a share of the link -
/// found by OID whatever the counters are called, with resets thrown away and a 32-bit wrap taken into account.</summary>
public sealed class InterfaceTrafficTests
{
    private static readonly DateTimeOffset Then = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private const string DefaultOids = """
        1.3.6.1.2.1.2.2.1.8.3|Oper Status
        1.3.6.1.2.1.31.1.1.1.6.3|In Octets
        1.3.6.1.2.1.31.1.1.1.10.3|Out Octets
        1.3.6.1.2.1.31.1.1.1.15.3|Speed
        """;

    private static SensorExecutionResult Read(params (string Key, double Value)[] channels) =>
        SensorExecutionResult.Healthy(TimeSpan.FromMilliseconds(20), "Oper Status 1", 1, "oper_status",
            channels.Select(channel => new SensorChannelValue { Key = channel.Key, Label = channel.Key, Value = channel.Value }).ToList());

    private static SensorObservation Before(DateTimeOffset at, params (string Key, double Value)[] channels) => new()
    {
        TimestampUtc = at,
        State = SensorState.Healthy,
        Channels = channels.Select(channel => new SensorChannelValue { Key = channel.Key, Value = channel.Value }).ToList()
    };

    [Fact]
    public void The_counters_change_since_the_last_check_is_the_traffic_and_against_the_link_the_utilisation()
    {
        var now = Read(("oper_status", 1), ("in_octets", 2_000_000_000), ("out_octets", 75_000_000), ("speed", 1000));
        var before = Before(Then, ("in_octets", 1_000_000_000), ("out_octets", 0));

        var result = InterfaceTraffic.AddRates(DefaultOids, now, before, Then.AddSeconds(60));

        double? Value(string key) => result.Channels.Single(channel => channel.Key == key).Value;
        Assert.Equal(133.333, Value(InterfaceTraffic.TrafficIn));   // 1e9 octets x 8 / 60 s
        Assert.Equal(13.3, Value(InterfaceTraffic.UtilizationIn));  // of 1000 Mbit/s
        Assert.Equal(10, Value(InterfaceTraffic.TrafficOut));
        Assert.Equal(1, Value(InterfaceTraffic.UtilizationOut));
        Assert.Equal("Oper Status 1 - in 133.33 Mbit/s, out 10 Mbit/s", result.Message);
        Assert.Equal("Mbit/s", result.Channels.Single(channel => channel.Key == InterfaceTraffic.TrafficIn).Unit);
    }

    [Fact]
    public void The_counters_are_found_by_their_oid_whatever_they_are_called()
    {
        const string oids = """
            1.3.6.1.2.1.31.1.1.1.6.7|WAN down
            1.3.6.1.2.1.31.1.1.1.10.7|WAN up
            """;
        var result = InterfaceTraffic.AddRates(oids, Read(("wan_down", 1_250_000), ("wan_up", 0)), Before(Then, ("wan_down", 0), ("wan_up", 0)), Then.AddSeconds(10));

        Assert.Equal(1, result.Channels.Single(channel => channel.Key == InterfaceTraffic.TrafficIn).Value);
        Assert.DoesNotContain(result.Channels, channel => channel.Key == InterfaceTraffic.UtilizationIn); // no speed read
    }

    [Fact]
    public void Without_a_previous_result_or_with_a_reset_there_is_no_rate()
    {
        var now = Read(("in_octets", 500), ("out_octets", 500), ("speed", 1000));

        Assert.Same(now, InterfaceTraffic.AddRates(DefaultOids, now, null, Then));
        Assert.Same(now, InterfaceTraffic.AddRates(DefaultOids, now, Before(Then, ("in_octets", 9_000_000), ("out_octets", 9_000_000)), Then.AddSeconds(60)));
        Assert.Same(now, InterfaceTraffic.AddRates(DefaultOids, now, Before(Then, ("in_octets", 100), ("out_octets", 100)), Then.AddMilliseconds(500)));
    }

    [Fact]
    public void A_32_bit_counter_wraps_once_and_a_rate_faster_than_the_link_is_thrown_away()
    {
        // 4 294 966 296 -> 1 000 is 2 000 octets on: 16 000 bit in 1 s.
        Assert.Equal(16_000, InterfaceTraffic.Rate(1_000, 4_294_966_296, is32Bit: true, seconds: 1, linkMbps: 100));
        Assert.Null(InterfaceTraffic.Rate(1_000, 4_294_966_296, is32Bit: false, seconds: 1, linkMbps: 100));
        Assert.Null(InterfaceTraffic.Rate(2_000_000_000, 0, is32Bit: false, seconds: 1, linkMbps: 100)); // 16 Gbit/s on a 100 Mbit/s link
    }

    [Fact]
    public void An_older_list_with_the_64_bit_counters_gets_the_speed_asked_for_too()
    {
        const string old = """
            1.3.6.1.2.1.2.2.1.8.3|Oper Status
            1.3.6.1.2.1.31.1.1.1.6.3|In Octets
            1.3.6.1.2.1.31.1.1.1.10.3|Out Octets
            """;
        Assert.EndsWith("1.3.6.1.2.1.31.1.1.1.15.3|Speed", InterfaceTraffic.WithSpeed(old));
        Assert.Equal(DefaultOids, InterfaceTraffic.WithSpeed(DefaultOids));

        const string legacy32 = "1.3.6.1.2.1.2.2.1.10.3|In Octets"; // maybe an SNMPv1 device: an unknown OID would fail the whole request
        Assert.Equal(legacy32, InterfaceTraffic.WithSpeed(legacy32));
    }

    [Theory]
    [InlineData(940, "0.9 kbit/s")]
    [InlineData(12_500_000, "12.5 Mbit/s")]
    [InlineData(2_340_000_000, "2.34 Gbit/s")]
    public void Traffic_reads_in_the_unit_that_fits(double bitsPerSecond, string expected) =>
        Assert.Equal(expected, InterfaceTraffic.FormatBits(bitsPerSecond));
}
