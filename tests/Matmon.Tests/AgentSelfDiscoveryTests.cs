using Matmon.Core.Domain;
using Matmon.Probe;

namespace Matmon.Tests;

/// <summary>
/// "Discover sensors" on a host with an agent runs ON the agent, against its own machine: the Windows / Linux sensors
/// are suggested without WinRM or SSH being open (they run locally there, without a credential) and target the machine
/// itself; a ping of oneself is not suggested. Without the flag nothing changes - a remote scan still needs the port.
/// </summary>
public sealed class AgentSelfDiscoveryTests
{
    private static readonly ISensorExecutor[] Executors =
    [
        new PingSensorExecutor(),
        new WindowsHealthSensorExecutor(), new WindowsDiskSensorExecutor(), new WindowsEventLogSensorExecutor(), new WindowsUpdateSensorExecutor(),
        new LinuxSshHealthSensorExecutor(), new LinuxDiskSensorExecutor(), new LinuxUpdateSensorExecutor()
    ];

    private static readonly string[] WindowsTypes = ["windows-health", "windows-disk", "windows-eventlog", "windows-update"];
    private static readonly string[] LinuxTypes = ["linux-ssh-health", "linux-disk", "linux-update"];

    [Fact]
    public async Task An_agent_scanning_its_own_machine_gets_the_local_sensors_of_its_system()
    {
        var results = await new NetworkDiscoveryService(Executors).DiscoverAsync(Request(onThisMachine: true));

        var suggestions = Assert.Single(results).SuggestedSensors;
        var types = suggestions.Select(suggestion => suggestion.SensorTypeKey).ToArray();
        if (OperatingSystem.IsWindows())
        {
            Assert.All(WindowsTypes, type => Assert.Contains(type, types));
            Assert.DoesNotContain(types, type => LinuxTypes.Contains(type));
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.All(LinuxTypes, type => Assert.Contains(type, types));
            Assert.DoesNotContain(types, type => WindowsTypes.Contains(type));
        }

        Assert.DoesNotContain("ping", types); // pinging oneself says nothing
        Assert.All(suggestions, suggestion =>
        {
            Assert.Equal(SensorDiscoveryContext.ThisMachine, suggestion.Target); // runs locally whatever the host's address
            Assert.DoesNotContain(suggestion.Settings.Parameters.Keys, key => key.StartsWith("winrm.", StringComparison.Ordinal) || key.StartsWith("ssh.", StringComparison.Ordinal));
        });
        Assert.True(LocalTarget.Is(SensorDiscoveryContext.ThisMachine));
    }

    [Fact]
    public async Task A_remote_scan_still_needs_the_port()
    {
        var results = await new NetworkDiscoveryService(Executors).DiscoverAsync(Request(onThisMachine: false));

        var types = Assert.Single(results).SuggestedSensors.Select(suggestion => suggestion.SensorTypeKey).ToArray();
        Assert.DoesNotContain(types, type => WindowsTypes.Contains(type) || LinuxTypes.Contains(type));
    }

    // Loopback, no TCP ports checked: whatever is suggested comes from the flag alone, not from a port that happens to be open here.
    private static NetworkDiscoveryRequest Request(bool onThisMachine) => new(
        Guid.NewGuid(),
        "127.0.0.1",
        new NetworkDiscoveryOptions(
            UsePing: true, PingFirst: false, UseTcpPorts: false, TcpPorts: [], UseSnmp: false, SnmpCommunity: "public",
            SnmpVersion: "v2c", SnmpPort: 161, UseReverseDns: false, TimeoutMs: 1000, MaxHosts: 1, Parallelism: 1)
        { OnThisMachine = onThisMachine });
}
