using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Matmon.Core.Syslog;

namespace Matmon.Host.Services;

/// <summary>
/// Keeps the central log within its limits: messages older than the retention, and the oldest past the message cap, are
/// deleted hourly - and at once when the limits are tightened. Receiving is the syslog collector plugin's job (on the main
/// probe, or on a probe near the devices); this service only looks after what they collected.
/// </summary>
public sealed class SyslogHousekeepingService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly IMonitoringWorkspaceStore _workspace;
    private readonly ISyslogStore _store;
    private readonly ILogger<SyslogHousekeepingService> _logger;
    private string? _retentionSignature;
    private DateTimeOffset _nextPrune = DateTimeOffset.UtcNow.AddSeconds(30);

    public SyslogHousekeepingService(IMonitoringWorkspaceStore workspace, ISyslogStore store, ILogger<SyslogHousekeepingService> logger)
    {
        _workspace = workspace;
        _store = store;
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
                    PruneIfDue(_workspace.GetSyslogSettings());
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "The syslog housekeeping failed; trying again");
                }

                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
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
}

/// <summary>
/// "Send a test message": sends one real syslog line to the collector on this machine (the main probe's) and checks that it
/// was stored. It is the honest answer to "is this working" - it goes through the collector's socket and allow-list, the
/// parser and the database, the same way a switch's message would, and says which of them stopped it.
/// </summary>
public static class SyslogSelfTest
{
    /// <summary>Sends over UDP when the collector listens there (<paramref name="udpPort"/>), else over TCP.</summary>
    public static async Task<(bool Ok, string Message)> RunAsync(
        int? udpPort,
        int? tcpPort,
        ISyslogStore store,
        CancellationToken cancellationToken = default)
    {
        if (udpPort is null && tcpPort is null)
        {
            return (false, "The collector on this machine listens on neither UDP nor TCP.");
        }

        // RFC 3164 time stamp, which is English whatever the language of this machine is ("Okt." is not a month a
        // syslog parser knows): the test must send what a real device sends, or it proves nothing about the parser.
        var useUdp = udpPort is not null;
        var marker = Guid.NewGuid().ToString("N")[..8];
        var local = DateTime.Now;
        var stamp = string.Create(CultureInfo.InvariantCulture, $"{local:MMM} {local.Day,2} {local:HH:mm:ss}");
        var line = $"<14>{stamp} matmon matmon-test: syslog self test {marker}";
        var bytes = Encoding.UTF8.GetBytes(line);
        var sentAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        try
        {
            if (useUdp)
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, udpPort!.Value), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var tcp = new TcpClient(AddressFamily.InterNetwork);
                await tcp.ConnectAsync(IPAddress.Loopback, tcpPort!.Value, cancellationToken).ConfigureAwait(false);
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

        var storage = store.GetStorageInfo();
        return (false, storage.Available
            ? "The test message was sent but did not show up in the log - is 127.0.0.1 in the collector's Allowed sources? (The collector on this machine shows how many it refused.)"
            : "The test message arrived but the log database is not usable: " + storage.Error);
    }
}
