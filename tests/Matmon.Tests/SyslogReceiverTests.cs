using System.Net;
using System.Net.Sockets;
using System.Text;
using Matmon.Core.Syslog;
using Matmon.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The receiver over real sockets: what arrives by UDP and TCP is parsed and stored, who may send is enforced, and the
/// supervisor opens, moves and closes the listeners as the settings change. Every test uses free ports, never 514.
/// </summary>
public sealed class SyslogReceiverTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-syslogrx-").FullName;
    private readonly SyslogStore _store;
    private readonly SyslogReceiverState _state = new();
    private readonly SyslogIngest _ingest;
    private readonly SyslogReceiverService _service;
    private readonly List<IDisposable> _disposables = [];

    public SyslogReceiverTests()
    {
        _store = new SyslogStore(Path.Combine(_dir, "syslog.db"), NullLogger<SyslogStore>.Instance);
        _ingest = new SyslogIngest(_store, _state);
        // Reconcile never touches the workspace store - only the polling loop does - so the test hands it nothing.
        _service = new SyslogReceiverService(null!, _store, _ingest, _state, NullLogger<SyslogReceiverService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
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

    // The receiver opens the same number on UDP and on TCP, so a port is only usable when it is free on BOTH. A number
    // that was free for TCP can still be somebody's ephemeral UDP port - a DNS query from another test class running at
    // the same time is enough - so each candidate is tried the way the service binds it, on both protocols.
    private static int FreePort()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            try
            {
                using var udp = OccupyUdp(port);
                using var tcp = OccupyTcp(port);
                return port;
            }
            catch (SocketException)
            {
                // taken on the other protocol (or in the other address family): try another number
            }
        }

        throw new InvalidOperationException("No port is free on both TCP and UDP.");
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

    private SyslogSettings Settings(bool udp = true, bool tcp = true, string? allowed = null) => new()
    {
        Enabled = true,
        UdpEnabled = udp,
        UdpPort = FreePort(),
        TcpEnabled = tcp,
        TcpPort = FreePort(),
        AllowedSources = allowed
    };

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

    // ---- the supervisor ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SwitchingTheReceiverOnOpensBothListeners_AndTheStatusSaysSo()
    {
        var settings = Settings();

        _service.Reconcile(settings);

        var status = _state.Snapshot();
        Assert.True(status.Udp.IsListening);
        Assert.Contains(settings.UdpPort.ToString(), status.Udp.Detail);
        Assert.True(status.Tcp.IsListening);
        await SendUdp(settings.UdpPort, "<13>Oct 15 10:11:12 sw1 app: over udp");
        await SendTcp(settings.TcpPort, "<13>Oct 15 10:11:12 sw1 app: over tcp\n");
        Assert.Equal(2, (await WaitForMessages(2)).Count);
    }

    [Fact]
    public void ItStaysOffUntilSwitchedOn()
    {
        _service.Reconcile(new SyslogSettings { Enabled = false });

        Assert.Equal("off", _state.Snapshot().Udp.State);
        Assert.Equal("off", _state.Snapshot().Tcp.State);
    }

    [Fact]
    public async Task EachProtocolCanBeSwitchedOffOnItsOwn()
    {
        var settings = Settings(udp: false, tcp: true);

        _service.Reconcile(settings);

        Assert.Equal("off", _state.Snapshot().Udp.State);
        Assert.True(_state.Snapshot().Tcp.IsListening);
        await SendUdp(settings.UdpPort, "<13>Oct 15 10:11:12 sw1 app: nobody is listening");
        Assert.Empty(await WaitForMessages(1, timeoutMs: 300));
    }

    [Fact]
    public async Task MovingAPortMovesTheListener()
    {
        var first = Settings(tcp: false);
        _service.Reconcile(first);
        var moved = first.Clone();
        moved.UdpPort = FreePort();

        _service.Reconcile(moved);

        await SendUdp(moved.UdpPort, "<13>Oct 15 10:11:12 sw1 app: on the new port");
        await SendUdp(first.UdpPort, "<13>Oct 15 10:11:12 sw1 app: on the old port");
        var stored = await WaitForMessages(1);
        Assert.Equal("on the new port", Assert.Single(stored).Message);
    }

    [Fact]
    public async Task SwitchingItOffClosesTheSockets()
    {
        var settings = Settings();
        _service.Reconcile(settings);

        _service.Reconcile(new SyslogSettings { Enabled = false, UdpPort = settings.UdpPort, TcpPort = settings.TcpPort });

        Assert.Equal("off", _state.Snapshot().Udp.State);
        await SendUdp(settings.UdpPort, "<13>Oct 15 10:11:12 sw1 app: too late");
        Assert.Empty(await WaitForMessages(1, timeoutMs: 300));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, settings.TcpPort));
    }

    [Fact]
    public async Task ChangingOnlyTheRetentionKeepsTheConnectionsOpen()
    {
        var settings = Settings(udp: false);
        _service.Reconcile(settings);

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, settings.TcpPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Bytes("<13>Oct 15 10:11:12 sw1 app: before\n"));
        await WaitForMessages(1);

        var changed = settings.Clone();
        changed.RetentionDays = 90;
        changed.MaxMessages = 5_000_000;
        _service.Reconcile(changed);
        await stream.WriteAsync(Bytes("<13>Oct 15 10:11:12 sw1 app: after\n"));

        Assert.Equal(2, (await WaitForMessages(2)).Count);
    }

    [Fact]
    public void AnAllowListThatParsesToNothingKeepsTheReceiverOff_SoATypoCannotOpenItToEveryone()
    {
        var settings = Settings(allowed: "switch-1, 10.0.0.300");

        _service.Reconcile(settings);

        var status = _state.Snapshot();
        Assert.Equal("error", status.Udp.State);
        Assert.Equal("error", status.Tcp.State);
        Assert.Contains("stays off", status.Warning);
        Assert.Contains("switch-1", status.Warning);
    }

    [Fact]
    public async Task APartlyValidAllowListWorks_AndNamesTheEntriesItIgnored()
    {
        var settings = Settings(udp: true, tcp: false, allowed: "127.0.0.0/8, typo");

        _service.Reconcile(settings);

        Assert.True(_state.Snapshot().Udp.IsListening);
        Assert.Contains("typo", _state.Snapshot().Warning);
        await SendUdp(settings.UdpPort, "<13>Oct 15 10:11:12 sw1 app: allowed");
        Assert.Single(await WaitForMessages(1));
    }

    [Fact]
    public void APortAnotherProgramHoldsIsReported_AndTheOtherProtocolStillWorks()
    {
        var settings = Settings();
        using var blocker = OccupyUdp(settings.UdpPort);

        _service.Reconcile(settings);

        var status = _state.Snapshot();
        Assert.Equal("error", status.Udp.State);
        Assert.Contains("already used", status.Udp.Detail);
        Assert.True(status.Tcp.IsListening);
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

    // Holds a TCP port the way the service itself would take it (dual-stack): an IPv4-only listener next to it would
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
    public async Task TheAllowListChangesLiveWithoutDroppingTheListener()
    {
        var open = Settings(udp: true, tcp: false);
        _service.Reconcile(open);
        await SendUdp(open.UdpPort, "<13>Oct 15 10:11:12 sw1 app: while open");
        Assert.Single(await WaitForMessages(1));

        var closed = open.Clone();
        closed.AllowedSources = "10.0.0.0/8";
        _service.Reconcile(closed);
        await SendUdp(closed.UdpPort, "<13>Oct 15 10:11:12 sw1 app: while closed");
        await Task.Delay(300);

        Assert.Single(_store.Search(new SyslogQuery(), 10));
    }

    // ---- "Send a test message" --------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSelfTestGoesThroughTheRealSocketAndTheDatabase()
    {
        var settings = Settings();
        _service.Reconcile(settings);

        var (ok, message) = await SyslogSelfTest.RunAsync(settings, _store, _state);

        Assert.True(ok, message);
        Assert.Contains("syslog self test", message);
    }

    [Fact]
    public async Task TheSelfTestSaysWhenTheAllowListKeptItOut()
    {
        var settings = Settings(allowed: "10.0.0.0/8");
        _service.Reconcile(settings);

        var (ok, message) = await SyslogSelfTest.RunAsync(settings, _store, _state);

        Assert.False(ok);
        Assert.Contains("Allowed sources", message);
    }

    [Fact]
    public async Task TheSelfTestSaysWhenTheReceiverIsOff()
    {
        var (ok, message) = await SyslogSelfTest.RunAsync(new SyslogSettings { Enabled = false }, _store, _state);

        Assert.False(ok);
        Assert.Contains("switched off", message);
    }

    [Fact]
    public async Task TheSelfTestSaysWhenNoListenerIsRunning()
    {
        var settings = Settings();
        using var udpBlocker = OccupyUdp(settings.UdpPort);
        using var tcpBlocker = OccupyTcp(settings.TcpPort);
        _service.Reconcile(settings);

        var (ok, message) = await SyslogSelfTest.RunAsync(settings, _store, _state);

        Assert.False(ok);
        Assert.Contains("Neither", message);
    }
}
