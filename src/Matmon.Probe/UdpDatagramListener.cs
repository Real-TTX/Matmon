using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Matmon.Probe;

/// <summary>
/// A UDP socket that hands every datagram to a handler and sends back what the handler returns (an SNMP inform's
/// response) - dual-stack where the machine has IPv6, IPv4 only where it does not. Used by the primary's trap receiver
/// and by the trap collector plugin on probes, so both behave the same.
/// </summary>
public sealed class UdpDatagramListener : IDisposable
{
    private readonly Socket _socket;
    private readonly Func<ReadOnlyMemory<byte>, IPAddress, byte[]?> _handler;
    private readonly ILogger _logger;
    private readonly string _what;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    private UdpDatagramListener(Socket socket, Func<ReadOnlyMemory<byte>, IPAddress, byte[]?> handler, ILogger logger, string what)
    {
        _socket = socket;
        _handler = handler;
        _logger = logger;
        _what = what;
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public int Port { get; }

    /// <summary>Binds and starts receiving (port 0 picks a free one, for tests). Throws <see cref="SocketException"/> when the
    /// port is taken or needs rights this process does not have.</summary>
    public static UdpDatagramListener Start(int port, Func<ReadOnlyMemory<byte>, IPAddress, byte[]?> handler, ILogger logger, string what = "UDP")
    {
        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.OperationNotSupported)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Windows reports an ICMP "port unreachable" from an earlier send (an inform's answer) as a reset on the next receive.
                socket.IOControl((IOControlCode)(-1744830452), [0], null);
            }
            catch (SocketException)
            {
            }
        }

        var listener = new UdpDatagramListener(socket, handler, logger, what);
        listener._loop = Task.Run(listener.ReceiveLoopAsync);
        return listener;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65535];
        var remote = _socket.AddressFamily == AddressFamily.InterNetworkV6 ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, _stopping.Token).ConfigureAwait(false);
                if (result.RemoteEndPoint is not IPEndPoint sender)
                {
                    continue;
                }

                var answer = _handler(buffer.AsMemory(0, result.ReceivedBytes), sender.Address);
                if (answer is not null)
                {
                    await _socket.SendToAsync(answer, SocketFlags.None, sender, _stopping.Token).ConfigureAwait(false);
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
                // see Start: not a failure of ours
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "{What} receive failed; carrying on", _what);
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
        }

        _stopping.Dispose();
    }
}
