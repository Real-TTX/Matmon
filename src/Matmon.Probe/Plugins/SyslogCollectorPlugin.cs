using System.Net.Sockets;
using System.Text.Json;
using Matmon.Core.Syslog;
using Matmon.Probe.Syslog;

namespace Matmon.Probe.Plugins;

/// <summary>
/// The syslog receiver as a plugin - on the primary (the main probe) or on a probe or agent where the devices are (a
/// branch office, a site behind NAT). It parses with the same code everywhere and sends every message to the central log
/// as a <c>syslog</c> event, with the time it ARRIVED and the address it came from, so a message reads the same whichever
/// collector took it. On a probe the messages wait in its on-disk queue while the primary is unreachable. It brings the
/// System > Syslog page and the Syslog tab of Events with it: neither is there while no collector runs.
/// </summary>
public sealed class SyslogCollectorPlugin : IMatmonPlugin
{
    public const string Id = "syslog-collector";
    public const string EventKind = "syslog";

    public PluginDefinition Definition { get; } = new(
        Id,
        "Syslog collector",
        "Receives the syslog messages of your devices - on the primary or on a probe or agent near them - and puts them into the central log, after an outage of the primary too, with their real arrival times.",
        PluginPlacement.Both,
        [
            new("udpEnabled", "Receive over UDP", PluginSettingKind.Boolean, "true", "What most switches, routers and firewalls send."),
            new("udpPort", "UDP port", PluginSettingKind.Port, "514", "514 is the standard; on Linux a port below 1024 needs root or CAP_NET_BIND_SERVICE."),
            new("tcpEnabled", "Receive over TCP", PluginSettingKind.Boolean, "true", "For devices that cannot afford to lose a message."),
            new("tcpPort", "TCP port", PluginSettingKind.Port, "514"),
            new("allowedSources", "Allowed sources", PluginSettingKind.AddressList, null,
                "IP addresses and ranges, separated by commas or new lines. Empty = anyone who can reach the port - syslog has no login, so this list is what decides who may write to the log.")
        ])
    {
        Links =
        [
            new(PluginUiArea.System, "syslog", "Syslog", "/system/syslog"),
            new(PluginUiArea.Events, "syslog", "Syslog", "/Events#syslog")
        ]
    };

    public Task<IPluginInstance> StartAsync(PluginContext context, CancellationToken cancellationToken)
    {
        var allowText = context.Setting("allowedSources");
        var allowList = IpAllowList.Parse(allowText, out var invalid);
        if (!string.IsNullOrWhiteSpace(allowText) && allowList.IsEmpty)
        {
            // Fail closed, like the primary: a list that parses to nothing must not read as "everybody".
            throw new InvalidOperationException($"No valid address in Allowed sources ({string.Join(", ", invalid)}) - the collector stays off.");
        }

        var udp = context.BoolSetting("udpEnabled", true);
        var tcp = context.BoolSetting("tcpEnabled", true);
        if (!udp && !tcp)
        {
            throw new InvalidOperationException("Neither UDP nor TCP is switched on.");
        }

        var state = new SyslogReceiverState();
        var ingest = new SyslogIngest(new EventSink(context.Events), state);
        ingest.SetAllowList(allowList);

        SyslogUdpListener? udpListener = null;
        SyslogTcpListener? tcpListener = null;
        try
        {
            if (udp)
            {
                udpListener = SyslogUdpListener.Start(context.IntSetting("udpPort", 514), ingest, context.Logger);
            }

            if (tcp)
            {
                tcpListener = SyslogTcpListener.Start(context.IntSetting("tcpPort", 514), ingest, context.Logger);
            }
        }
        catch (SocketException exception)
        {
            udpListener?.Dispose();
            tcpListener?.Dispose();
            throw new InvalidOperationException(exception.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => "The port is in use by another program on this machine - pick another one.",
                SocketError.AccessDenied => "Not allowed to open the port (below 1024 on Linux needs root or CAP_NET_BIND_SERVICE) - pick a port above 1024.",
                _ => $"Could not open the port: {exception.Message}"
            });
        }

        var warning = invalid.Count > 0 ? $" Ignored in Allowed sources: {string.Join(", ", invalid)}." : string.Empty;
        return Task.FromResult<IPluginInstance>(new Instance(state, udpListener, tcpListener, warning));
    }

    private sealed class EventSink(IPluginEventSink events) : ISyslogSink
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public bool TryEnqueue(SyslogMessage message)
        {
            events.Publish(new PluginEvent(Id, EventKind, message.ReceivedUtc, message.SourceAddress, JsonSerializer.Serialize(message, Json)));
            return true;
        }
    }

    private sealed class Instance(SyslogReceiverState state, SyslogUdpListener? udp, SyslogTcpListener? tcp, string warning) : IPluginInstance
    {
        public PluginStatus Status
        {
            get
            {
                var snapshot = state.Snapshot();
                var where = string.Join(" and ", new[] { udp is null ? null : $"UDP {udp.Port}", tcp is null ? null : $"TCP {tcp.Port}" }.OfType<string>());
                var last = snapshot.LastReceivedUtc is { } at ? $", last {at:u}" : string.Empty;
                return PluginStatus.Running($"Listening on {where} - {snapshot.Received:N0} received, {snapshot.Rejected:N0} refused{last}.{warning}");
            }
        }

        public ValueTask DisposeAsync()
        {
            udp?.Dispose();
            tcp?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
