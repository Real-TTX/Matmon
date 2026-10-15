using System.Net;
using System.Net.Sockets;
using System.Text;
using Matmon.Core.Syslog;

namespace Matmon.Host.Services;

/// <summary>
/// Keeps the syslog sockets in line with the settings. The settings are read every few seconds and the listeners are
/// rebuilt only when something that decides which sockets exist (or who may use them) changed - so enabling the receiver,
/// moving a port or editing the allow-list takes effect within seconds and without a restart, and changing the retention
/// does not drop a single connection. It also runs the housekeeping: old messages are deleted hourly.
/// </summary>
public sealed class SyslogReceiverService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly IMonitoringWorkspaceStore _workspace;
    private readonly ISyslogStore _store;
    private readonly SyslogIngest _ingest;
    private readonly SyslogReceiverState _state;
    private readonly ILogger<SyslogReceiverService> _logger;
    private SyslogUdpListener? _udp;
    private SyslogTcpListener? _tcp;
    private string? _listenerSignature;
    private string? _retentionSignature;
    private DateTimeOffset _nextPrune = DateTimeOffset.UtcNow.AddSeconds(30);

    public SyslogReceiverService(
        IMonitoringWorkspaceStore workspace,
        ISyslogStore store,
        SyslogIngest ingest,
        SyslogReceiverState state,
        ILogger<SyslogReceiverService> logger)
    {
        _workspace = workspace;
        _store = store;
        _ingest = ingest;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var settings = _workspace.GetSyslogSettings();
                    Reconcile(settings);
                    PruneIfDue(settings);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "The syslog receiver could not apply its settings; trying again");
                }

                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            StopListeners();
        }
    }

    /// <summary>Opens, closes or rebuilds the listeners so they match <paramref name="settings"/>. Cheap when nothing changed.</summary>
    public void Reconcile(SyslogSettings settings)
    {
        var normalized = settings.Normalized();
        var signature = normalized.ListenerSignature();
        if (signature == _listenerSignature)
        {
            return;
        }

        StopListeners();
        _listenerSignature = signature;

        if (!normalized.Enabled)
        {
            _state.SetWarning(null);
            _state.SetUdp(SyslogListenerStatus.Off);
            _state.SetTcp(SyslogListenerStatus.Off);
            return;
        }

        var allowList = IpAllowList.Parse(normalized.AllowedSources, out var invalid);
        if (!string.IsNullOrWhiteSpace(normalized.AllowedSources) && allowList.IsEmpty)
        {
            // An allow-list that parses to nothing would read as "allow everybody". It stays off instead, so a typo
            // cannot open the receiver to the whole network.
            var message = $"None of the entries in Allowed sources is an IP address or range ({string.Join(", ", invalid)}), so the receiver stays off.";
            _state.SetWarning(message);
            var refused = new SyslogListenerStatus("error", "Not started: " + message);
            _state.SetUdp(normalized.UdpEnabled ? refused : SyslogListenerStatus.Off);
            _state.SetTcp(normalized.TcpEnabled ? refused : SyslogListenerStatus.Off);
            return;
        }

        _ingest.SetAllowList(allowList);
        _state.SetWarning(invalid.Count > 0 ? $"Ignored in Allowed sources (not an IP address or range): {string.Join(", ", invalid)}." : null);

        if (normalized.UdpEnabled)
        {
            try
            {
                _udp = SyslogUdpListener.Start(normalized.UdpPort, _ingest, _logger);
                _state.SetUdp(new SyslogListenerStatus("listening", $"Listening on UDP port {normalized.UdpPort}."));
                _logger.LogInformation("Syslog UDP receiver listening on port {Port}", normalized.UdpPort);
            }
            catch (SocketException exception)
            {
                _state.SetUdp(new SyslogListenerStatus("error", Describe(exception, "UDP", normalized.UdpPort)));
                _logger.LogWarning("Syslog UDP receiver could not start on port {Port}: {Message}", normalized.UdpPort, exception.Message);
            }
        }
        else
        {
            _state.SetUdp(SyslogListenerStatus.Off);
        }

        if (normalized.TcpEnabled)
        {
            try
            {
                _tcp = SyslogTcpListener.Start(normalized.TcpPort, _ingest, _logger);
                _state.SetTcp(new SyslogListenerStatus("listening", $"Listening on TCP port {normalized.TcpPort}."));
                _logger.LogInformation("Syslog TCP receiver listening on port {Port}", normalized.TcpPort);
            }
            catch (SocketException exception)
            {
                _state.SetTcp(new SyslogListenerStatus("error", Describe(exception, "TCP", normalized.TcpPort)));
                _logger.LogWarning("Syslog TCP receiver could not start on port {Port}: {Message}", normalized.TcpPort, exception.Message);
            }
        }
        else
        {
            _state.SetTcp(SyslogListenerStatus.Off);
        }
    }

    private void PruneIfDue(SyslogSettings settings)
    {
        var normalized = settings.Normalized();
        var retentionSignature = $"{normalized.RetentionDays}|{normalized.MaxMessages}";
        var now = DateTimeOffset.UtcNow;

        // A tighter limit than before should bite now, not at the next full hour.
        if (retentionSignature != _retentionSignature)
        {
            _retentionSignature = retentionSignature;
            _nextPrune = now;
        }

        if (now < _nextPrune)
        {
            return;
        }

        _nextPrune = now + PruneInterval;
        var deleted = _store.Prune(now - TimeSpan.FromDays(normalized.RetentionDays), normalized.MaxMessages);
        if (deleted > 0)
        {
            _logger.LogInformation("Syslog housekeeping removed {Count} messages (kept {Days} days, at most {Max})", deleted, normalized.RetentionDays, normalized.MaxMessages);
        }
    }

    private static string Describe(SocketException exception, string protocol, int port) => exception.SocketErrorCode switch
    {
        SocketError.AddressAlreadyInUse => $"{protocol} port {port} is already used by another program on this machine.",
        SocketError.AccessDenied => $"{protocol} port {port} needs more rights than this process has - choose a port above 1023, or let Matmon bind low ports.",
        _ => $"{protocol} port {port} could not be opened: {exception.Message}"
    };

    private void StopListeners()
    {
        _udp?.Dispose();
        _udp = null;
        _tcp?.Dispose();
        _tcp = null;
    }

    public override void Dispose()
    {
        StopListeners();
        base.Dispose();
    }
}

/// <summary>
/// "Send a test message": sends one real syslog line to this instance's own receiver and checks that it was stored. It is
/// the honest answer to "is this working" - it goes through the socket, the allow-list, the parser and the database, the
/// same way a switch's message would, and says which of them stopped it.
/// </summary>
public static class SyslogSelfTest
{
    public static async Task<(bool Ok, string Message)> RunAsync(
        SyslogSettings settings,
        ISyslogStore store,
        SyslogReceiverState state,
        CancellationToken cancellationToken = default)
    {
        var normalized = settings.Normalized();
        if (!normalized.Enabled)
        {
            return (false, "The receiver is switched off - enable it first.");
        }

        var snapshot = state.Snapshot();
        var useUdp = normalized.UdpEnabled && snapshot.Udp.IsListening;
        var useTcp = !useUdp && normalized.TcpEnabled && snapshot.Tcp.IsListening;
        if (!useUdp && !useTcp)
        {
            return (false, "Neither the UDP nor the TCP listener is running: " + (snapshot.Udp.Detail ?? snapshot.Tcp.Detail ?? "see the status above."));
        }

        var marker = Guid.NewGuid().ToString("N")[..8];
        var line = $"<14>{DateTime.Now:MMM d HH:mm:ss} matmon matmon-test: syslog self test {marker}";
        var bytes = Encoding.UTF8.GetBytes(line);
        var rejectedBefore = snapshot.Rejected;
        var sentAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        try
        {
            if (useUdp)
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, normalized.UdpPort), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var tcp = new TcpClient(AddressFamily.InterNetwork);
                await tcp.ConnectAsync(IPAddress.Loopback, normalized.TcpPort, cancellationToken).ConfigureAwait(false);
                await tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return (false, $"The test message could not be sent to this machine's own {(useUdp ? "UDP" : "TCP")} port: {exception.Message}");
        }

        // The message is queued, then written in a batch: give both a moment.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            await store.FlushAsync(cancellationToken).ConfigureAwait(false);
            var found = store.Search(new SyslogQuery { FromUtc = sentAt, Contains = marker }, 1);
            if (found.Count > 0)
            {
                return (true, $"Received and stored over {(useUdp ? "UDP" : "TCP")}: \"{found[0].Message}\".");
            }
        }

        if (state.Snapshot().Rejected > rejectedBefore)
        {
            return (false, "The receiver refused the test message because this machine (127.0.0.1) is not in Allowed sources. Add it to test, or trust that devices in the list will get through.");
        }

        var storage = store.GetStorageInfo();
        return (false, storage.Available
            ? "The test message was sent but did not show up in the log."
            : "The test message arrived but the log database is not usable: " + storage.Error);
    }
}
