using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Matmon.Core.SnmpTraps;
using Matmon.Core.Syslog;

namespace Matmon.Probe.Plugins;

/// <summary>What the trap collector sends home: the datagram exactly as it arrived (base64).</summary>
public sealed record SnmpTrapForward(string Datagram);

/// <summary>
/// The SNMP trap receiver as a plugin - on the primary (the main probe) or on a probe or agent where the devices are: it
/// keeps out senders that are not allowed, answers a v2c inform at once (the device stops repeating it) and hands every
/// trap on as it arrived, with the time and the address. The primary does the rest the same way for every collector -
/// communities, SNMPv3 users, MIB names, the central log, polling the device's sensors - so those are configured in ONE
/// place (System > SNMP traps, a page that comes with this plugin). On a probe the traps wait in its on-disk queue while
/// the primary is unreachable.
/// </summary>
public sealed class SnmpTrapCollectorPlugin : IMatmonPlugin
{
    public const string Id = "snmptrap-collector";
    public const string EventKind = "snmptrap";

    public PluginDefinition Definition { get; } = new(
        Id,
        "SNMP trap collector",
        "Receives the SNMP traps of your devices - on the primary or on a probe or agent near them. The primary logs them with their MIB names and polls the device's sensors, after an outage too, with their real arrival times.",
        PluginPlacement.Both,
        [
            new("port", "UDP port", PluginSettingKind.Port, "162", "162 is the standard (snmptrapd on the same machine uses it too); on Linux a port below 1024 needs root or CAP_NET_BIND_SERVICE."),
            new("allowedSources", "Allowed sources", PluginSettingKind.AddressList, null,
                "IP addresses and ranges, separated by commas or new lines. Empty = anyone who can reach the port. Communities and SNMPv3 users are checked on the primary (System > SNMP traps).")
        ])
    {
        // Traps land in the central log, so they bring its tab on Events along too.
        Links =
        [
            new(PluginUiArea.System, "traps", "SNMP traps", "/system/snmp-traps"),
            new(PluginUiArea.Events, "syslog", "Syslog", "/Events#syslog")
        ]
    };

    public Task<IPluginInstance> StartAsync(PluginContext context, CancellationToken cancellationToken)
    {
        var allowText = context.Setting("allowedSources");
        var allowList = IpAllowList.Parse(allowText, out var invalid);
        if (!string.IsNullOrWhiteSpace(allowText) && allowList.IsEmpty)
        {
            throw new InvalidOperationException($"No valid address in Allowed sources ({string.Join(", ", invalid)}) - the collector stays off.");
        }

        var counters = new Counters();
        UdpDatagramListener listener;
        try
        {
            listener = UdpDatagramListener.Start(context.IntSetting("port", 162),
                (datagram, sender) => Handle(datagram, sender, allowList, counters, context.Events), context.Logger, "SNMP trap collector");
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException(exception.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => "The port is in use by another program on this machine (snmptrapd?) - pick another one.",
                SocketError.AccessDenied => "Not allowed to open the port (below 1024 on Linux needs root or CAP_NET_BIND_SERVICE) - pick a port above 1024.",
                _ => $"Could not open the port: {exception.Message}"
            });
        }

        return Task.FromResult<IPluginInstance>(new Instance(listener, counters));
    }

    /// <summary>One datagram: refused, or forwarded - and for a v2c inform the answer to send back.</summary>
    public static byte[]? Handle(ReadOnlyMemory<byte> datagram, IPAddress sender, IpAllowList allowList, Counters counters, IPluginEventSink events)
    {
        var source = sender.IsIPv4MappedToIPv6 ? sender.MapToIPv4() : sender;
        if (!allowList.IsEmpty && !allowList.Allows(source))
        {
            Interlocked.Increment(ref counters.Refused);
            return null;
        }

        Interlocked.Increment(ref counters.Received);
        counters.LastUtc = DateTimeOffset.UtcNow;
        events.Publish(new PluginEvent(Id, EventKind, DateTimeOffset.UtcNow, source.ToString(),
            JsonSerializer.Serialize(new SnmpTrapForward(Convert.ToBase64String(datagram.Span)), JsonOptions)));

        // The device repeats an inform until it is answered - answering here keeps that local, whatever the primary does.
        return SnmpTrapParser.TryParse(datagram.Span, out var trap, out _) && trap is { PduType: SnmpTrapPduType.Inform }
            ? SnmpTrapParser.BuildInformResponse(trap)
            : null;
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed class Counters
    {
        public long Received;
        public long Refused;
        public DateTimeOffset? LastUtc;
    }

    private sealed class Instance(UdpDatagramListener listener, Counters counters) : IPluginInstance
    {
        public PluginStatus Status => PluginStatus.Running(
            $"Listening on UDP {listener.Port} - {Interlocked.Read(ref counters.Received):N0} received, {Interlocked.Read(ref counters.Refused):N0} refused{(counters.LastUtc is { } at ? $", last {at:u}" : string.Empty)}.");

        public ValueTask DisposeAsync()
        {
            listener.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
