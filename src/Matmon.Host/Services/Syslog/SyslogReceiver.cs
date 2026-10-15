using System.Net;
using System.Net.Sockets;
using Matmon.Core.Syslog;

namespace Matmon.Host.Services;

/// <summary>What one listener is doing, for the settings page: "off", "listening" or "error" plus a human sentence.</summary>
public sealed record SyslogListenerStatus(string State, string? Detail)
{
    public static SyslogListenerStatus Off { get; } = new("off", null);

    public bool IsListening => State == "listening";
}

public sealed record SyslogReceiverSnapshot(
    SyslogListenerStatus Udp,
    SyslogListenerStatus Tcp,
    long Received,
    long Rejected,
    int TcpConnections,
    DateTimeOffset? LastReceivedUtc,
    string? Warning);

/// <summary>The receiver's live state: the sockets' status and a few counters. Process-local, resets on restart - the
/// store has the messages, this has the weather.</summary>
public sealed class SyslogReceiverState
{
    private long _received;
    private long _rejected;
    private int _tcpConnections;
    private long _lastReceivedTicks;
    private volatile SyslogListenerStatus _udp = SyslogListenerStatus.Off;
    private volatile SyslogListenerStatus _tcp = SyslogListenerStatus.Off;
    private volatile string? _warning;

    public void SetUdp(SyslogListenerStatus status) => _udp = status;

    public void SetTcp(SyslogListenerStatus status) => _tcp = status;

    public void SetWarning(string? warning) => _warning = warning;

    public void CountReceived(DateTimeOffset at)
    {
        Interlocked.Increment(ref _received);
        Interlocked.Exchange(ref _lastReceivedTicks, at.UtcTicks);
    }

    public void CountRejected() => Interlocked.Increment(ref _rejected);

    public int ConnectionOpened() => Interlocked.Increment(ref _tcpConnections);

    public void ConnectionClosed() => Interlocked.Decrement(ref _tcpConnections);

    public SyslogReceiverSnapshot Snapshot()
    {
        var ticks = Interlocked.Read(ref _lastReceivedTicks);
        return new SyslogReceiverSnapshot(
            _udp,
            _tcp,
            Interlocked.Read(ref _received),
            Interlocked.Read(ref _rejected),
            Volatile.Read(ref _tcpConnections),
            ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero),
            _warning);
    }
}

/// <summary>
/// What happens to one datagram or one framed message once a listener has it: who may send, parse, store. The sockets
/// call this and nothing else, so the whole path can be exercised without opening one.
/// </summary>
public sealed class SyslogIngest
{
    private readonly ISyslogStore _store;
    private readonly TimeProvider _time;
    private volatile IpAllowList _allowList = IpAllowList.Parse(null, out _);

    public SyslogIngest(ISyslogStore store, SyslogReceiverState state, TimeProvider? time = null)
    {
        _store = store;
        State = state;
        _time = time ?? TimeProvider.System;
    }

    public SyslogReceiverState State { get; }

    public void SetAllowList(IpAllowList allowList) => _allowList = allowList;

    public bool IsAllowed(IPAddress sender) => _allowList.Allows(sender);

    public void Accept(ReadOnlySpan<byte> payload, IPAddress sender)
    {
        if (!_allowList.Allows(sender))
        {
            State.CountRejected();
            return;
        }

        var now = _time.GetUtcNow();
        var message = SyslogParser.Parse(payload, now, FormatAddress(sender));
        if (message is null)
        {
            return;
        }

        State.CountReceived(now);
        _store.TryEnqueue(message); // a full queue is counted by the store, which is where the page reads it
    }

    /// <summary>The address the way an admin would type it: IPv4 stays IPv4 on a dual-mode socket, and an IPv6 link-local
    /// address loses its interface suffix.</summary>
    public static string FormatAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var text = address.ToString();
        var scope = text.IndexOf('%');
        return scope < 0 ? text : text[..scope];
    }
}

/// <summary>UDP: one datagram, one message (RFC 5426). Dual-stack where the machine has IPv6, IPv4 only where it does not.</summary>
public sealed class SyslogUdpListener : IDisposable
{
    private readonly Socket _socket;
    private readonly SyslogIngest _ingest;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    private SyslogUdpListener(Socket socket, SyslogIngest ingest, ILogger logger)
    {
        _socket = socket;
        _ingest = ingest;
        _logger = logger;
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public int Port { get; }

    /// <summary>Binds and starts receiving. Port 0 picks a free one (tests). Throws <see cref="SocketException"/> when
    /// the port is taken or needs rights this process does not have.</summary>
    public static SyslogUdpListener Start(int port, SyslogIngest ingest, ILogger logger)
    {
        var socket = CreateSocket(port);
        var listener = new SyslogUdpListener(socket, ingest, logger);
        listener._loop = Task.Run(listener.ReceiveLoopAsync);
        return listener;
    }

    private static Socket CreateSocket(int port)
    {
        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            Configure(socket);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return socket;
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.OperationNotSupported)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Configure(socket);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }
    }

    private static void Configure(Socket socket)
    {
        try
        {
            // A burst (a switch that has just booted) is absorbed by the kernel while the loop catches up.
            socket.ReceiveBufferSize = 4 * 1024 * 1024;
        }
        catch (SocketException)
        {
            // the OS may refuse a buffer this large; the default still works
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Windows reports an ICMP "port unreachable" from an earlier send as a reset on the next receive.
                socket.IOControl((IOControlCode)(-1744830452), [0], null);
            }
            catch (SocketException)
            {
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65535];
        var remote = _socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, _stopping.Token).ConfigureAwait(false);
                if (result.RemoteEndPoint is IPEndPoint sender)
                {
                    _ingest.Accept(buffer.AsSpan(0, result.ReceivedBytes), sender.Address);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted or SocketError.Shutdown)
            {
                return;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
            {
                // see Configure: not a failure of ours
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Syslog UDP receive failed; carrying on");
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _socket.Dispose();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // the loop ends by cancellation or by the socket going away
        }

        _stopping.Dispose();
    }
}

/// <summary>TCP: a connection carries a stream of messages (RFC 6587), framed by <see cref="SyslogFrameReader"/>.</summary>
public sealed class SyslogTcpListener : IDisposable
{
    public const int MaxConnections = 256;

    private readonly TcpListener _listener;
    private readonly SyslogIngest _ingest;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<TcpClient> _clients = [];
    private Task? _loop;

    private SyslogTcpListener(TcpListener listener, SyslogIngest ingest, ILogger logger)
    {
        _listener = listener;
        _ingest = ingest;
        _logger = logger;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>Binds and starts accepting. Port 0 picks a free one (tests). Throws <see cref="SocketException"/> when
    /// the port is taken or needs rights this process does not have.</summary>
    public static SyslogTcpListener Start(int port, SyslogIngest ingest, ILogger logger)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            listener.Start(backlog: 128);
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.OperationNotSupported)
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start(backlog: 128);
        }

        var instance = new SyslogTcpListener(listener, ingest, logger);
        instance._loop = Task.Run(instance.AcceptLoopAsync);
        return instance;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Syslog TCP accept failed; carrying on");
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var state = _ingest.State;
        var open = state.ConnectionOpened();
        lock (_clients)
        {
            _clients.Add(client);
        }

        try
        {
            using (client)
            {
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (open > MaxConnections || remote is null || !_ingest.IsAllowed(remote))
                {
                    if (remote is not null && open <= MaxConnections)
                    {
                        state.CountRejected();
                    }

                    return;
                }

                // Devices hold a connection for days; keep-alive is how a dead one is noticed.
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                var reader = new SyslogFrameReader();
                var buffer = new byte[16 * 1024];
                var stream = client.GetStream();
                while (!_stopping.IsCancellationRequested && !reader.Faulted)
                {
                    var read = await stream.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    foreach (var frame in reader.Push(buffer.AsSpan(0, read)))
                    {
                        _ingest.Accept(frame, remote);
                    }
                }

                if (!reader.Faulted)
                {
                    foreach (var frame in reader.Complete())
                    {
                        _ingest.Accept(frame, remote);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // the peer went away or we are shutting down
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A syslog TCP connection failed");
        }
        finally
        {
            lock (_clients)
            {
                _clients.Remove(client);
            }

            state.ConnectionClosed();
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        lock (_clients)
        {
            foreach (var client in _clients.ToArray())
            {
                client.Dispose();
            }
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // the loop ends by cancellation or by the listener stopping
        }

        _stopping.Dispose();
    }
}
