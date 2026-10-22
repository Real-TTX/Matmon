using System.Net;
using System.Net.Sockets;
using Matmon.Core.SnmpTraps;
using Matmon.Core.Syslog;
using Matmon.Host.Pages;
using Matmon.Host.Services;
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
    public void The_settings_form_refuses_a_bad_port_or_allow_list_entry_instead_of_correcting_it()
    {
        Assert.False(SystemSnmpTrapsModel.TryBuildSettings(new SnmpTrapSettingsInput { Port = 70000 }, out _, out var portError));
        Assert.Contains("port", portError);
        Assert.False(SystemSnmpTrapsModel.TryBuildSettings(new SnmpTrapSettingsInput { AllowedSources = "10.0.0.0/8, nas" }, out _, out var listError));
        Assert.Contains("nas", listError);
        Assert.True(SystemSnmpTrapsModel.TryBuildSettings(new SnmpTrapSettingsInput { Enabled = true, Port = 1162, Communities = " public , ups " }, out var settings, out _));
        Assert.Equal(["public", "ups"], settings.CommunityList());
    }

    // --- the real UDP path -----------------------------------------------------------------------------------

    private (SyslogStore Store, SnmpTrapReceiverState State, SnmpTrapIngest Ingest) Pipeline()
    {
        var store = new SyslogStore(Path.Combine(_dir, $"log-{Guid.NewGuid():N}.db"), NullLogger<SyslogStore>.Instance);
        var state = new SnmpTrapReceiverState();
        return (store, state, new SnmpTrapIngest(store, state, NullLogger<SnmpTrapIngest>.Instance));
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
        var (store, state, ingest) = Pipeline();
        using (store)
        using (var listener = SnmpTrapUdpListener.Start(0, ingest, NullLogger.Instance))
        {
            await SendAsync(listener.Port, SnmpTrapParser.BuildV2Trap("public", OnBattery, TimeSpan.FromSeconds(9), [("1.3.6.1.2.1.33.1.2.3.0", "23")]));

            var message = Assert.Single(await WaitForAsync(store, 1));
            Assert.StartsWith("SNMP trap upsTrapOnBattery (1.3.6.1.2.1.33.2.1)", message.Message);
            Assert.Contains("1.3.6.1.2.1.33.1.2.3.0 = 23", message.Message);
            Assert.Equal(SyslogSeverity.Warning, message.Severity);
            Assert.Equal(SnmpTrapIngest.AppName, message.AppName);
            Assert.Equal("upsTrapOnBattery", message.MsgId);
            Assert.Equal(1, state.Snapshot().Received);
        }
    }

    [Fact]
    public async Task A_wrong_community_or_an_address_outside_the_allow_list_is_refused()
    {
        var (store, state, ingest) = Pipeline();
        using (store)
        using (var listener = SnmpTrapUdpListener.Start(0, ingest, NullLogger.Instance))
        {
            // UDP may lose a datagram (Windows does drop one on loopback under a busy test run), so each case is sent
            // again until its refusal is counted - what matters is that nothing gets into the log.
            async Task RefusedAsync(long expected)
            {
                for (var send = 0; send < 5 && state.Snapshot().Rejected < expected; send++)
                {
                    await SendAsync(listener.Port, SnmpTrapParser.BuildV2Trap("public", OnBattery, TimeSpan.Zero, []));
                    for (var attempt = 0; attempt < 40 && state.Snapshot().Rejected < expected; attempt++)
                    {
                        await Task.Delay(50);
                    }
                }
            }

            ingest.Configure(IpAllowList.Parse(null, out _), ["ups-only"], pollOnTrap: false);
            await RefusedAsync(1);

            ingest.Configure(IpAllowList.Parse("10.0.0.0/8", out _), [], pollOnTrap: false);
            await RefusedAsync(2);

            Assert.True(state.Snapshot().Rejected >= 2);
            Assert.Equal(0, state.Snapshot().Received);
            Assert.Empty(await WaitForAsync(store, 0));
        }
    }

    [Fact]
    public async Task An_inform_is_acknowledged_over_the_wire_so_the_sender_stops_repeating_it()
    {
        var (store, _, ingest) = Pipeline();
        using (store)
        using (var listener = SnmpTrapUdpListener.Start(0, ingest, NullLogger.Instance))
        {
            var answer = await SendAsync(listener.Port, SnmpTrapParser.BuildV2Trap("public", OnBattery, TimeSpan.FromSeconds(1), [], requestId: 31, inform: true), expectAnswer: true);

            Assert.NotNull(answer);
            Assert.False(SnmpTrapParser.TryParse(answer, out _, out var error));
            Assert.Contains("0xA2", error);
            Assert.Single(await WaitForAsync(store, 1));
        }
    }

    [Fact]
    public async Task An_unreadable_packet_is_counted_and_said_not_stored()
    {
        var (store, state, ingest) = Pipeline();
        using (store)
        using (var listener = SnmpTrapUdpListener.Start(0, ingest, NullLogger.Instance))
        {
            await SendAsync(listener.Port, [0x30, 0x03, 0x02, 0x01, 0x03]);

            for (var attempt = 0; attempt < 200 && state.Snapshot().Unreadable == 0; attempt++)
            {
                await Task.Delay(50);
            }

            Assert.Equal(1, state.Snapshot().Unreadable);
            Assert.Contains("SNMPv3", state.Snapshot().Warning);
        }
    }
}
