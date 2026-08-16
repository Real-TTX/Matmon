using System.Text.Json;
using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>Proxmox reports used/total in two shapes and the node sensor sees both: /nodes/{node}/status nests
/// them ("memory": { used, total }), /cluster/resources uses a flat pair (mem / maxmem). Reading only the flat
/// pair made memory and swap read 0 % on every node and dropped the root-FS channel, while CPU (a flat field
/// on both) looked fine - which is exactly what made it hard to spot.</summary>
public class ProxmoxNodeChannelTests
{
    /// <summary>Trimmed to the fields the sensor reads, but in the real shape of /nodes/{node}/status.</summary>
    private const string NodeStatusJson = """
    {
      "cpu": 0.0625,
      "memory": { "total": 100000000, "used": 42000000, "free": 58000000 },
      "swap":   { "total": 10000000, "used": 1000000, "free": 9000000 },
      "rootfs": { "total": 200000000, "used": 50000000, "avail": 150000000 },
      "loadavg": ["0.50", "0.40", "0.30"],
      "uptime": 7200
    }
    """;

    /// <summary>The flat shape used by /cluster/resources and the node list - must keep working.</summary>
    private const string FlatNodeJson = """
    {
      "cpu": 0.0625,
      "mem": 42000000, "maxmem": 100000000,
      "swap": 1000000, "maxswap": 10000000,
      "uptime": 7200
    }
    """;

    private static IReadOnlyDictionary<string, double?> Channels(string json, double cpuPercent = 6.25)
    {
        using var document = JsonDocument.Parse(json);
        return ProxmoxPveSensorExecutor
            .BuildNodeStatusChannels(document.RootElement, cpuPercent)
            .ToDictionary(channel => channel.Key, channel => channel.Value);
    }

    [Fact]
    public void Nested_status_payload_reports_memory()
    {
        Assert.Equal(42, Channels(NodeStatusJson)["memory"]);
    }

    [Fact]
    public void Nested_status_payload_reports_swap()
    {
        Assert.Equal(10, Channels(NodeStatusJson)["swap"]);
    }

    [Fact]
    public void Nested_status_payload_reports_root_fs()
    {
        var channels = Channels(NodeStatusJson);

        Assert.True(channels.ContainsKey("rootfs"), "the root FS channel must appear when the node reports it");
        Assert.Equal(25, channels["rootfs"]);
    }

    [Fact]
    public void Flat_payload_still_reports_memory_and_swap()
    {
        var channels = Channels(FlatNodeJson);

        Assert.Equal(42, channels["memory"]);
        Assert.Equal(10, channels["swap"]);
        Assert.False(channels.ContainsKey("rootfs"), "the flat payload carries no root FS, so no channel");
    }

    [Fact]
    public void Cpu_and_uptime_are_unaffected()
    {
        var channels = Channels(NodeStatusJson);

        Assert.Equal(6.25, channels["cpu"]);
        Assert.Equal(2, channels["uptimeHours"]);
        Assert.Equal(0.5, channels["load1"]);
    }

    [Fact]
    public void Load_average_is_also_read_from_the_legacy_string_form()
    {
        var channels = Channels("""{ "cpu": 0, "loadavg": "0.50, 0.40, 0.30" }""", cpuPercent: 0);

        Assert.Equal(0.5, channels["load1"]);
        Assert.Equal(0.4, channels["load5"]);
        Assert.Equal(0.3, channels["load15"]);
    }

    [Fact]
    public void Missing_values_stay_zero_instead_of_throwing()
    {
        var channels = Channels("""{ "cpu": 0 }""", cpuPercent: 0);

        Assert.Equal(0, channels["memory"]);
        Assert.Equal(0, channels["swap"]);
    }
}
