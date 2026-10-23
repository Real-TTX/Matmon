using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Matmon.Core.Syslog;
using Matmon.Host.Services;
using Matmon.Host.Services.Plugins;
using Matmon.Probe.Plugins;
using Matmon.Probe.Syslog;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The receiver over real sockets: what arrives by UDP and TCP is parsed and stored, who may send is enforced, and the
/// syslog collector plugin - run by the plugin runtime the way the primary runs it on the main probe, its events going
/// through the dispatcher into the store - opens, moves and closes the listeners as its settings change. Every test uses
/// free ports, never 514.
/// </summary>
public sealed class SyslogReceiverTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-syslogrx-").FullName;
    private readonly SyslogStore _store;
    private readonly SyslogReceiverState _state = new();
    private readonly SyslogIngest _ingest;
    private readonly PluginRuntime _runtime;
    private readonly Guid _assignment = Guid.NewGuid();
    private readonly List<IDisposable> _disposables = [];

    public SyslogReceiverTests()
    {
        _store = new SyslogStore(Path.Combine(_dir, "syslog.db"), NullLogger<SyslogStore>.Instance);
        _ingest = new SyslogIngest(_store, _state);
        var catalog = new PluginCatalog([new SyslogCollectorPlugin()], NullLogger<PluginCatalog>.Instance);
        var dispatcher = new PluginEventDispatcher([new SyslogPluginEventHandler(_store)], NullLogger<PluginEventDispatcher>.Instance);
        _runtime = new PluginRuntime(catalog, new DispatchingSink(dispatcher), NullLoggerFactory.Instance, _dir, probeId: null);
    }

    private sealed class DispatchingSink(PluginEventDispatcher dispatcher) : IPluginEventSink
    {
        public void Publish(PluginEvent pluginEvent) => dispatcher.Dispatch(pluginEvent, probeId: null);
    }

    public void Dispose()
    {
        _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _store.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // temp cleanup must not fail the run
        }
    }

    // A free port per protocol, handed out by the system for that protocol - a number taken from TCP's ephemeral range
    // and then tried on UDP (what the old receiver needed: one number for both) failed often enough under a busy parallel
    // run to fail a test now and then. Bound the way the collector binds it (dual-stack), so free here means free there.
    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static int FreeTcpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private async Task<IReadOnlyList<SyslogMessage>> WaitForMessages(int count, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            await _store.FlushAsync();
            var found = _store.Search(new SyslogQuery(), 1000);
            if (found.Count >= count || DateTime.UtcNow > deadline)
            {
                return found;
            }

            await Task.Delay(25);
        }
    }

    /// <summary>A collector's settings as System > Plugins stores them, on free ports.</summary>
    private sealed record Collector(bool Udp, int UdpPort, bool Tcp, int TcpPort, string? Allowed)
    {
        public Dictionary<string, string> ToSettings()
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["udpEnabled"] = Udp ? "true" : "false",
                ["udpPort"] = UdpPort.ToString(CultureInfo.InvariantCulture),
                ["tcpEnabled"] = Tcp ? "true" : "false",
                ["tcpPort"] = TcpPort.ToString(CultureInfo.InvariantCulture)
            };
            if (Allowed is not null)
            {
                settings["allowedSources"] = Allowed;
            }

            return settings;
        }
    }

    private static Collector Settings(bool udp = true, bool tcp = true, string? allowed = null) => new(udp, FreeUdpPort(), tcp, FreeTcpPort(), allowed);

    /// <summary>What the primary does every few seconds: the runtime made to match the assignment (or none).</summary>
    private async Task<PluginStatus?> Run(Collector? collector, bool enabled = true)
    {
        await _runtime.ReconcileAsync(
            collector is null ? [] : [new PluginAssignmentSpec(_assignment, SyslogCollectorPlugin.Id, enabled, collector.ToSettings())],
            DateTimeOffset.UtcNow, default);
        return _runtime.Statuses().Select(status => status.Status).SingleOrDefault();
    }

    private static async Task SendUdp(int port, string text)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        await client.SendAsync(Bytes(text), new IPEndPoint(IPAddress.Loopback, port));
    }

    // ---- what happens to one message --------------------------------------------------------------------------------

    [Fact]
    public async Task AMessageIsParsedAndStoredWithTheAddressItCameFrom_AnIpv4SenderOnADualModeSocketStaysIpv4()
    {
        _ingest.Accept(Bytes("<34>Oct 11 22:14:15 mymachine su: 'su root' failed"), IPAddress.Parse("::ffff:10.0.0.5"));

        var stored = Assert.Single(await WaitForMessages(1));

        Assert.Equal("10.0.0.5", stored.SourceAddress);
        Assert.Equal("mymachine", stored.Hostname);
        Assert.Equal("su", stored.AppName);
        Assert.Equal(2, stored.Severity);
        Assert.Equal(1, _state.Snapshot().Received);
        Assert.NotNull(_state.Snapshot().LastReceivedUtc);
    }

    [Fact]
    public void AnIpv6LinkLocalAddressLosesItsInterfaceSuffix()
    {
        Assert.Equal("fe80::1", SyslogIngest.FormatAddress(IPAddress.Parse("fe80::1%5")));
        Assert.Equal("10.1.2.3", SyslogIngest.FormatAddress(IPAddress.Parse("::ffff:10.1.2.3")));
        Assert.Equal("2001:db8::7", SyslogIngest.FormatAddress(IPAddress.Parse("2001:db8::7")));
    }

    [Fact]
    public async Task ASenderOutsideTheAllowListIsRefusedAndCounted()
    {
        _ingest.SetAllowList(IpAllowList.Parse("10.0.0.0/8", out _));

        _ingest.Accept(Bytes("<13>Oct 15 10:11:12 outsider app: no"), IPAddress.Parse("192.168.1.1"));
        _ingest.Accept(Bytes("<13>Oct 15 10:11:12 insider app: yes"), IPAddress.Parse("10.1.1.1"));

        var stored = Assert.Single(await WaitForMessages(1));
        Assert.Equal("insider", stored.Hostname);
        Assert.Equal(1, _state.Snapshot().Rejected);
        Assert.Equal(1, _state.Snapshot().Received);
    }

    [Fact]
    public async Task AnEmptyDatagramIsNothing()
    {
        _ingest.Accept(Bytes("\r\n"), IPAddress.Loopback);
        _ingest.Accept([], IPAddress.Loopback);

        Assert.Empty(await WaitForMessages(1, timeoutMs: 300));
        Assert.Equal(0, _state.Snapshot().Received);
    }

    // ---- UDP --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UdpDatagramsAreStored()
    {
        var listener = SyslogUdpListener.Start(0, _ingest, NullLogger.Instance);
        _disposables.Add(listener);

        await SendUdp(listener.Port, "<189>12345: *Mar  1 00:00:12.071: %LINK-3-UPDOWN: Interface Gi0/1, changed state to down");

        var stored = Assert.Single(await WaitForMessages(1));
        Assert.Equal("127.0.0.1", stored.SourceAddress);
        Assert.Equal("LINK-3-UPDOWN", stored.MsgId);
        Assert.Equal(5, stored.Severity);
    }

    [Fact]
    public async Task ABurstOfDatagramsIsAbsorbed()
    {
        var listener = SyslogUdpListener.Start(0, _ingest, NullLogger.Instance);
        _disposables.Add(listener);

        using var client = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(IPAddress.Loopback, listener.Port);
        for (var index = 0; index < 400; index++)
        {
            await client.SendAsync(Bytes($"<13>Oct 15 10:11:12 sw1 app: burst {index}"), target);
        }

        var stored = await WaitForMessages(400, timeoutMs: 8000);
        Assert.Equal(400, stored.Count);
    }

    // ---- TCP --------------------------------------------------------------------------------------------------------

    private static async Task SendTcp(int port, params string[] chunks)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        foreach (var chunk in chunks)
        {
            await stream.WriteAsync(Bytes(chunk));
            await stream.FlushAsync();
            await Task.Delay(20); // separate reads on the other side
        }
    }

    private static string Counted(string message) => $"{Encoding.UTF8.GetByteCount(message)} {message}";

    [Fact]
    public async Task TcpReadsBothFramings()
    {
        var listener = SyslogTcpListener.Start(0, _ingest, NullLogger.Instance);
        _disposables.Add(listener);

        await SendTcp(listener.Port, "<13>Oct 15 10:11:12 lines app: first\n", "<13>Oct 15 10:11:12 lines app: sec", "ond\r\n");
        await SendTcp(listener.Port, Counted("<13>Oct 15 10:11:12 counted app: one"), Counted("<13>Oct 15 10:11:12 counted app: two\nwith a second line"));

        var stored = await WaitForMessages(4);

        Assert.Equal(4, stored.Count);
        Assert.Equal(["first", "second"], stored.Where(m => m.Hostname == "lines").OrderBy(m => m.Id).Select(m => m.Message));
        Assert.Equal(["one", "two\nwith a second line"], stored.Where(m => m.Hostname == "counted").OrderBy(m => m.Id).Select(m => m.Message));
        Assert.All(stored, m => Assert.Equal("127.0.0.1", m.SourceAddress));
    }

    [Fact]
    public async Task ALastLineWithoutANewLineIsKeptWhenTheConnectionCloses()
    {
        var listener = SyslogTcpListener.Start(0, _ingest, NullLogger.Instance);
        _disposables.Add(listener);

        await SendTcp(listener.Port, "<13>Oct 15 10:11:12 host app: no newline at the end");

        var stored = Assert.Single(await WaitForMessages(1));
        Assert.Equal("no newline at the end", stored.Message);
    }

    [Fact]
    public async Task TcpRefusesASenderOutsideTheAllowList()
    {
        _ingest.SetAllowList(IpAllowList.Parse("10.0.0.0/8", out _));
        var listener = SyslogTcpListener.Start(0, _ingest, NullLogger.Instance);
        _disposables.Add(listener);

        await SendTcp(listener.Port, "<13>Oct 15 10:11:12 host app: should not arrive\n");
        await Task.Delay(300);

        Assert.Empty(_store.Search(new SyslogQuery(), 10));
        Assert.True(_state.Snapshot().Rejected >= 1);
    }

    [Fact]
    public async Task BrokenFramingEndsTheConnection()
    {
        var listener = SyslogTcpListener.Start(0, _ingest, NullLogger.Instance);
        _disposables.Add(listener);

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, listener.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Bytes(Counted("<13>Oct 15 10:11:12 host app: fine")));
        await stream.WriteAsync(Bytes("0 nonsense"));

        var read = await stream.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(0, read); // the server closed it
        Assert.Single(await WaitForMessages(1));
    }

    [Fact]
    public async Task TheOpenConnectionsAreCounted()
    {
        var listener = SyslogTcpListener.Start(0, _ingest, NullLogger.Instance);
        _disposables.Add(listener);

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, listener.Port);
        await WaitUntil(() => _state.Snapshot().TcpConnections == 1);
        Assert.Equal(1, _state.Snapshot().TcpConnections);

        client.Dispose();
        await WaitUntil(() => _state.Snapshot().TcpConnections == 0);
        Assert.Equal(0, _state.Snapshot().TcpConnections);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    // ---- the collector plugin ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SwitchingTheCollectorOnOpensBothListeners_AndTheStatusSaysSo()
    {
        var settings = Settings();

        var status = await Run(settings);

        Assert.Equal("running", status?.State);
        Assert.Contains($"UDP {settings.UdpPort}", status!.Detail);
        Assert.Contains($"TCP {settings.TcpPort}", status.Detail);
        await SendUdp(settings.UdpPort, "<13>Oct 15 10:11:12 sw1 app: over udp");
        await SendTcp(settings.TcpPort, "<13>Oct 15 10:11:12 sw1 app: over tcp\n");
        Assert.Equal(2, (await WaitForMessages(2)).Count);
    }

    [Fact]
    public async Task NothingListensUntilACollectorIsSwitchedOn()
    {
        var settings = Settings();

        Assert.Null(await Run(settings, enabled: false));

        await SendUdp(settings.UdpPort, "<13>Oct 15 10:11:12 sw1 app: nobody is listening");
        Assert.Empty(await WaitForMessages(1, timeoutMs: 300));
    }

    [Fact]
    public async Task EachProtocolCanBeSwitchedOffOnItsOwn()
    {
        var settings = Settings(udp: false, tcp: true);

        var status = await Run(settings);

        Assert.DoesNotContain("UDP", status!.Detail);
        Assert.Contains($"TCP {settings.TcpPort}", status.Detail);
        await SendUdp(settings.UdpPort, "<13>Oct 15 10:11:12 sw1 app: nobody is listening");
        Assert.Empty(await WaitForMessages(1, timeoutMs: 300));
    }

    [Fact]
    public async Task MovingAPortMovesTheListener()
    {
        var first = Settings(tcp: false);
        await Run(first);
        var moved = first with { UdpPort = FreeUdpPort() };

        await Run(moved);

        await SendUdp(moved.UdpPort, "<13>Oct 15 10:11:12 sw1 app: on the new port");
        await SendUdp(first.UdpPort, "<13>Oct 15 10:11:12 sw1 app: on the old port");
        var stored = await WaitForMessages(1);
        Assert.Equal("on the new port", Assert.Single(stored).Message);
    }

    [Fact]
    public async Task RemovingTheCollectorClosesTheSockets()
    {
        var settings = Settings();
        await Run(settings);

        Assert.Null(await Run(null));

        await SendUdp(settings.UdpPort, "<13>Oct 15 10:11:12 sw1 app: too late");
        Assert.Empty(await WaitForMessages(1, timeoutMs: 300));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, settings.TcpPort));
    }

    [Fact]
    public async Task AnAllowListThatParsesToNothingKeepsTheCollectorOff_SoATypoCannotOpenItToEveryone()
    {
        var settings = Settings(allowed: "switch-1, 10.0.0.300");

        var status = await Run(settings);

        Assert.Equal("error", status?.State);
        Assert.Contains("stays off", status!.Detail);
        Assert.Contains("switch-1", status.Detail);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, settings.TcpPort));
    }

    [Fact]
    public async Task APartlyValidAllowListWorks_AndNamesTheEntriesItIgnored()
    {
        var settings = Settings(udp: true, tcp: false, allowed: "127.0.0.0/8, typo");

        var status = await Run(settings);

        Assert.Equal("running", status?.State);
        Assert.Contains("typo", status!.Detail);
        await SendUdp(settings.UdpPort, "<13>Oct 15 10:11:12 sw1 app: allowed");
        Assert.Single(await WaitForMessages(1));
    }

    [Fact]
    public async Task APortAnotherProgramHoldsIsReportedInWords()
    {
        var settings = Settings();
        using var blocker = OccupyUdp(settings.UdpPort);

        var status = await Run(settings);

        Assert.Equal("error", status?.State);
        Assert.Contains("in use", status!.Detail);
    }

    private static Socket OccupyUdp(int port)
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return socket;
        }
        catch (SocketException)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }
    }

    // Holds a TCP port the way the collector itself would take it (dual-stack): an IPv4-only listener next to it would
    // not stop a dual-stack bind on Windows, which is not what a "port in use" test is about.
    private static Socket OccupyTcp(int port)
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            socket.Listen(1);
            return socket;
        }
        catch (SocketException)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            socket.Listen(1);
            return socket;
        }
    }

    [Fact]
    public async Task ANarrowerAllowListKeepsOutWhatTheOldOneLetIn()
    {
        var open = Settings(udp: true, tcp: false);
        await Run(open);
        // UDP may lose a datagram - Windows does drop one on loopback under a busy test run - so the "open" message is
        // sent again until it is stored; what this test is about is the CLOSED one never getting in.
        for (var send = 0; send < 5 && _store.Search(new SyslogQuery { Contains = "while open" }, 10).Count == 0; send++)
        {
            await SendUdp(open.UdpPort, "<13>Oct 15 10:11:12 sw1 app: while open");
            await WaitForMessages(1, timeoutMs: 1000);
        }

        Assert.NotEmpty(_store.Search(new SyslogQuery { Contains = "while open" }, 10));

        await Run(open with { Allowed = "10.0.0.0/8" });
        await SendUdp(open.UdpPort, "<13>Oct 15 10:11:12 sw1 app: while closed");
        await Task.Delay(300);
        await _store.FlushAsync();

        Assert.Empty(_store.Search(new SyslogQuery { Contains = "while closed" }, 10));
    }

    // ---- "Send a test message" --------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSelfTestGoesThroughTheRealSocketAndTheDatabase()
    {
        var settings = Settings();
        await Run(settings);

        var (ok, message) = await SyslogSelfTest.RunAsync(settings.UdpPort, settings.TcpPort, _store);

        Assert.True(ok, message);
        Assert.Contains("syslog self test", message);
    }

    [Fact]
    public async Task TheSelfTestUsesTcpWhenTheCollectorHasNoUdp()
    {
        var settings = Settings(udp: false);
        await Run(settings);

        var (ok, message) = await SyslogSelfTest.RunAsync(null, settings.TcpPort, _store);

        Assert.True(ok, message);
        Assert.Contains("TCP", message);
    }

    [Fact]
    public async Task TheSelfTestSendsAProperlyDatedLineWhateverLanguageTheMachineSpeaks()
    {
        // The first version stamped the line with the machine's own month name - "Okt." on a German one - which no
        // syslog parser reads as a time stamp, so the stored message had no host and no program.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var settings = Settings();
            await Run(settings);

            var (ok, message) = await SyslogSelfTest.RunAsync(settings.UdpPort, settings.TcpPort, _store);

            Assert.True(ok, message);
            var stored = Assert.Single(await WaitForMessages(1));
            Assert.Equal("matmon", stored.Hostname);
            Assert.Equal("matmon-test", stored.AppName);
            Assert.StartsWith("syslog self test", stored.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task TheSelfTestSaysWhenTheAllowListKeptItOut()
    {
        var settings = Settings(allowed: "10.0.0.0/8");
        await Run(settings);

        var (ok, message) = await SyslogSelfTest.RunAsync(settings.UdpPort, settings.TcpPort, _store);

        Assert.False(ok);
        Assert.Contains("Allowed sources", message);
    }

    [Fact]
    public async Task TheSelfTestSaysWhenThereIsNothingToSendTo()
    {
        var (ok, message) = await SyslogSelfTest.RunAsync(null, null, _store);

        Assert.False(ok);
        Assert.Contains("neither UDP nor TCP", message);
    }
}
