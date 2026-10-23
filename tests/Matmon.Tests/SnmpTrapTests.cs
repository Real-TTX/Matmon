using System.Net;
using System.Net.Sockets;
using Matmon.Core.Domain;
using Matmon.Core.SnmpTraps;
using Matmon.Core.Syslog;
using Matmon.Core.Telemetry;
using Matmon.Host.Pages;
using Matmon.Host.Services;
using Matmon.Host.Services.Plugins;
using Matmon.Probe.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The trap receiver: the parser per version (a v1 linkDown and a v2 linkDown must end up as the same trap), the answer
/// to an inform, and the real UDP path into the central log - with the allow-list and the community deciding what counts.
/// </summary>
public sealed class SnmpTrapTests : IDisposable
{
    private const string OnBattery = "1.3.6.1.2.1.33.2.1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-traps-" + Guid.NewGuid().ToString("N"));

    public SnmpTrapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    // --- parser ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_v2c_trap_is_read_with_its_oid_uptime_and_bindings()
    {
        var bytes = SnmpTrapParser.BuildV2Trap("public", OnBattery, TimeSpan.FromSeconds(1234.5), [("1.3.6.1.2.1.33.1.2.3.0", "23")], requestId: 77);

        Assert.True(SnmpTrapParser.TryParse(bytes, out var trap, out var error), error);
        Assert.Equal(2, trap!.Version);
        Assert.Equal("public", trap.Community);
        Assert.Equal(SnmpTrapPduType.TrapV2, trap.PduType);
        Assert.Equal(77, trap.RequestId);
        Assert.Equal(OnBattery, trap.TrapOid);
        Assert.Equal(1234.5, trap.Uptime!.Value.TotalSeconds, 2);
        var binding = Assert.Single(trap.VarBinds); // sysUpTime and snmpTrapOID are not repeated as bindings
        Assert.Equal("1.3.6.1.2.1.33.1.2.3.0", binding.Oid);
        Assert.Equal("23", binding.Value);
    }

    [Fact]
    public void A_v1_generic_trap_maps_to_the_same_oid_as_its_v2_twin()
    {
        var bytes = SnmpTrapParser.BuildV1Trap("public", "1.3.6.1.4.1.9", "10.0.0.5", generic: 2, specific: 0, TimeSpan.FromSeconds(10), [("1.3.6.1.2.1.2.2.1.1.3", 3)]);

        Assert.True(SnmpTrapParser.TryParse(bytes, out var trap, out var error), error);
        Assert.Equal(1, trap!.Version);
        Assert.Equal("1.3.6.1.6.3.1.1.5.3", trap.TrapOid); // linkDown
        Assert.Equal("10.0.0.5", trap.AgentAddress);
        Assert.Equal("3", Assert.Single(trap.VarBinds).Value);
    }

    [Fact]
    public void A_v1_enterprise_specific_trap_is_enterprise_dot_zero_dot_specific()
    {
        var bytes = SnmpTrapParser.BuildV1Trap("public", "1.3.6.1.4.1.318", "10.0.0.6", generic: 6, specific: 5, TimeSpan.Zero, []);

        Assert.True(SnmpTrapParser.TryParse(bytes, out var trap, out _));
        Assert.Equal("1.3.6.1.4.1.318.0.5", trap!.TrapOid);
    }

    [Fact]
    public void An_snmpv3_message_is_refused_in_words()
    {
        byte[] v3 = [0x30, 0x03, 0x02, 0x01, 0x03]; // SEQUENCE { version 3 }

        Assert.False(SnmpTrapParser.TryParse(v3, out _, out var error));
        Assert.Contains("SNMPv3", error);
    }

    [Fact]
    public void Garbage_is_refused_without_throwing()
    {
        Assert.False(SnmpTrapParser.TryParse("hello"u8, out _, out var error));
        Assert.NotNull(error);
        Assert.False(SnmpTrapParser.TryParse([0x30, 0x7F, 0x01], out _, out _));
    }

    [Fact]
    public void An_inform_is_answered_with_a_response_pdu()
    {
        var inform = SnmpTrapParser.BuildV2Trap("public", OnBattery, TimeSpan.FromSeconds(5), [], requestId: 4242, inform: true);
        Assert.True(SnmpTrapParser.TryParse(inform, out var trap, out _));
        Assert.Equal(SnmpTrapPduType.Inform, trap!.PduType);

        var response = SnmpTrapParser.BuildInformResponse(trap);

        // Our own parser reads traps, not responses - which says the answer is a Response PDU (0xA2).
        Assert.False(SnmpTrapParser.TryParse(response, out _, out var error));
        Assert.Contains("0xA2", error);
    }

    // --- the settings form -----------------------------------------------------------------------------------

    [Fact]
    public void The_processing_form_keeps_the_communities_and_the_poll_switch()
    {
        Assert.True(SystemSnmpTrapsModel.TryBuildSettings(new SnmpTrapSettingsInput { Communities = " public , ups ", PollOnTrap = false }, out var settings, out _));
        Assert.Equal(["public", "ups"], settings.CommunityList());
        Assert.False(settings.PollOnTrap);
    }

    // --- the real UDP path: the collector plugin, its event, the primary's processing ---------------------------

    private sealed class Pipeline : IAsyncDisposable
    {
        private readonly SqliteTelemetryRepository _telemetry;
        private readonly PluginRuntime _runtime;

        public Pipeline(string dir)
        {
            Store = new SyslogStore(Path.Combine(dir, $"log-{Guid.NewGuid():N}.db"), NullLogger<SyslogStore>.Instance);
            State = new SnmpTrapReceiverState();
            var ingest = new SnmpTrapIngest(Store, State, NullLogger<SnmpTrapIngest>.Instance);
            _telemetry = new SqliteTelemetryRepository(Path.Combine(dir, $"t-{Guid.NewGuid():N}.db"));
            Workspace = new InMemoryMonitoringWorkspaceStore(
                new TrapTestHostEnvironment(dir), new MatmonRuntimeOptions { WorkspacePath = Path.Combine(dir, $"ws-{Guid.NewGuid():N}.json") },
                new MatmonAuthOptions(), new EphemeralDataProtectionProvider(), _telemetry, NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
            var dispatcher = new PluginEventDispatcher([new SnmpTrapPluginEventHandler(ingest, Workspace)], NullLogger<PluginEventDispatcher>.Instance);
            _runtime = new PluginRuntime(new PluginCatalog([new SnmpTrapCollectorPlugin()], NullLogger<PluginCatalog>.Instance),
                new DispatchingSink(dispatcher), NullLoggerFactory.Instance, dir, probeId: null);
        }

        public SyslogStore Store { get; }

        public SnmpTrapReceiverState State { get; }

        public InMemoryMonitoringWorkspaceStore Workspace { get; }

        public int Port { get; private set; }

        /// <summary>Runs the collector the way the primary runs it on the main probe; returns its status.</summary>
        public async Task<PluginStatus> CollectAsync(string? allowedSources = null)
        {
            Port = FreeUdpPort();
            var settings = new Dictionary<string, string> { ["port"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (allowedSources is not null)
            {
                settings["allowedSources"] = allowedSources;
            }

            await _runtime.ReconcileAsync([new PluginAssignmentSpec(Guid.NewGuid(), SnmpTrapCollectorPlugin.Id, true, settings)], DateTimeOffset.UtcNow, default);
            return _runtime.Statuses().Single().Status;
        }

        public async ValueTask DisposeAsync()
        {
            await _runtime.DisposeAsync();
            Workspace.Dispose();
            _telemetry.Dispose();
            Store.Dispose();
        }

        private static int FreeUdpPort()
        {
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        private sealed class DispatchingSink(PluginEventDispatcher dispatcher) : IPluginEventSink
        {
            public void Publish(PluginEvent pluginEvent) => dispatcher.Dispatch(pluginEvent, probeId: null);
        }
    }

    private static async Task<byte[]?> SendAsync(int port, byte[] datagram, bool expectAnswer = false)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, port));
        if (!expectAnswer)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var answer = await udp.ReceiveAsync(timeout.Token);
        return answer.Buffer;
    }

    private static async Task<IReadOnlyList<SyslogMessage>> WaitForAsync(SyslogStore store, int count)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await store.FlushAsync();
            var found = store.Search(new SyslogQuery { Contains = "SNMP trap" }, 10);
            if (found.Count >= count)
            {
                return found;
            }

            await Task.Delay(50);
        }

        return store.Search(new SyslogQuery { Contains = "SNMP trap" }, 10);
    }

    [Fact]
    public async Task A_trap_lands_in_the_central_log_with_its_name_and_a_warning_severity()
    {
        await using var pipeline = new Pipeline(_dir);
        Assert.Equal("running", (await pipeline.CollectAsync()).State);

        await SendAsync(pipeline.Port, SnmpTrapParser.BuildV2Trap("public", OnBattery, TimeSpan.FromSeconds(9), [("1.3.6.1.2.1.33.1.2.3.0", "23")]));

        var message = Assert.Single(await WaitForAsync(pipeline.Store, 1));
        Assert.StartsWith("SNMP trap upsTrapOnBattery (1.3.6.1.2.1.33.2.1)", message.Message);
        Assert.Contains("1.3.6.1.2.1.33.1.2.3.0 = 23", message.Message);
        Assert.Equal(SyslogSeverity.Warning, message.Severity);
        Assert.Equal(SnmpTrapIngest.AppName, message.AppName);
        Assert.Equal("upsTrapOnBattery", message.MsgId);
        Assert.Equal(1, pipeline.State.Snapshot().Received);
    }

    [Fact]
    public async Task A_wrong_community_is_refused_by_the_primarys_settings()
    {
        await using var pipeline = new Pipeline(_dir);
        pipeline.Workspace.UpdateSnmpTrapSettings(new SnmpTrapSettings { Communities = "ups-only", PollOnTrap = false });
        await pipeline.CollectAsync();

        // UDP may lose a datagram (Windows does drop one on loopback under a busy test run), so the trap is sent again
        // until its refusal is counted - what matters is that nothing gets into the log.
        for (var send = 0; send < 5 && pipeline.State.Snapshot().Rejected == 0; send++)
        {
            await SendAsync(pipeline.Port, SnmpTrapParser.BuildV2Trap("public", OnBattery, TimeSpan.Zero, []));
            for (var attempt = 0; attempt < 40 && pipeline.State.Snapshot().Rejected == 0; attempt++)
            {
                await Task.Delay(50);
            }
        }

        Assert.True(pipeline.State.Snapshot().Rejected >= 1);
        Assert.Equal(0, pipeline.State.Snapshot().Received);
        Assert.Empty(await WaitForAsync(pipeline.Store, 0));
    }

    [Fact]
    public async Task An_address_outside_the_collectors_allow_list_never_reaches_the_primary()
    {
        await using var pipeline = new Pipeline(_dir);
        await pipeline.CollectAsync(allowedSources: "10.0.0.0/8");

        await SendAsync(pipeline.Port, SnmpTrapParser.BuildV2Trap("public", OnBattery, TimeSpan.Zero, []));
        await Task.Delay(300);

        Assert.Equal(0, pipeline.State.Snapshot().Received);
        Assert.Empty(await WaitForAsync(pipeline.Store, 0));
    }

    [Fact]
    public async Task An_inform_is_acknowledged_over_the_wire_so_the_sender_stops_repeating_it()
    {
        await using var pipeline = new Pipeline(_dir);
        await pipeline.CollectAsync();

        var answer = await SendAsync(pipeline.Port, SnmpTrapParser.BuildV2Trap("public", OnBattery, TimeSpan.FromSeconds(1), [], requestId: 31, inform: true), expectAnswer: true);

        Assert.NotNull(answer);
        Assert.False(SnmpTrapParser.TryParse(answer, out _, out var error));
        Assert.Contains("0xA2", error);
        Assert.Single(await WaitForAsync(pipeline.Store, 1));
    }

    [Fact]
    public async Task An_unreadable_packet_is_counted_and_said_not_stored()
    {
        await using var pipeline = new Pipeline(_dir);
        await pipeline.CollectAsync();

        await SendAsync(pipeline.Port, [0x30, 0x03, 0x02, 0x01, 0x03]);

        for (var attempt = 0; attempt < 200 && pipeline.State.Snapshot().Unreadable == 0; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, pipeline.State.Snapshot().Unreadable);
        Assert.Contains("SNMPv3", pipeline.State.Snapshot().Warning);
    }
}

file sealed class TrapTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
