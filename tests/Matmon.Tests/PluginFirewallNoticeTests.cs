using Matmon.Host.Ui;
using Matmon.Probe.Plugins;

namespace Matmon.Tests;

/// <summary>A collector that is on but unreachable looks like a quiet network - the page names the ports to let in.</summary>
public sealed class PluginFirewallNoticeTests
{
    [Fact]
    public void The_syslog_collector_names_the_ports_its_settings_open()
    {
        var definition = new SyslogCollectorPlugin().Definition;

        var both = PluginFirewallNotice.For(definition, new Dictionary<string, string>(), "probe-01", isPrimary: false)!;
        Assert.Equal("UDP 514 and TCP 514", both.Ports);
        Assert.Equal("-p 514:514/udp -p 514:514/tcp", both.DockerRun);
        Assert.Contains("probe-01", both.Sentence);

        var udpOnly = PluginFirewallNotice.For(definition, new Dictionary<string, string> { ["tcpEnabled"] = "false", ["udpPort"] = "1514" }, "x", isPrimary: false)!;
        Assert.Equal("UDP 1514", udpOnly.Ports);
    }

    [Fact]
    public void The_trap_collector_opens_its_udp_port_and_a_plugin_without_listeners_needs_no_notice()
    {
        var trap = PluginFirewallNotice.For(new SnmpTrapCollectorPlugin().Definition, new Dictionary<string, string> { ["port"] = "10162" }, "x", isPrimary: false)!;
        Assert.Equal("UDP 10162", trap.Ports);

        var quiet = new PluginDefinition("quiet", "Quiet", "", PluginPlacement.Both, []);
        Assert.Null(PluginFirewallNotice.For(quiet, new Dictionary<string, string>(), "x", isPrimary: true));
    }
}
