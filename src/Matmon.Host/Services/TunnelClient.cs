using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Matmon.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Matmon.Host.Services;

/// <summary>
/// Full Access client (Primary-only): when enabled + connected to the cloud, keeps one outbound WebSocket
/// open to the cloud tunnel, receives browser HTTP requests, replays them against this instance's own UI,
/// and returns the responses. This is what lets a browser drive the local UI through the cloud without any
/// inbound port - the connection is always instance→cloud.
/// </summary>
public sealed class TunnelClient : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A single replayed response is fully buffered + base64'd into one WebSocket frame, and the cloud tears the
    // whole tunnel down past 32 MB/message - so cap the response here and return 413 instead, so one large download
    // through Full Access (e.g. a backup snapshot) can't disconnect every console user of the instance.
    private const long MaxReplayBodyBytes = 20L * 1024 * 1024;

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly IServer _server;
    private readonly ILogger<TunnelClient> _logger;
    private readonly TunnelAuthSecret _tunnelSecret;
    private readonly TunnelState _tunnelState;
    // Decompress the local response so the tunnel always carries plain bytes: the cloud rewrites text
    // bodies and the browser gets a decodable stream (the static-asset handler otherwise returns brotli/gzip
    // that, once Content-Encoding is dropped in transit, the browser can't decode → "CSS doesn't load").
    private readonly HttpClient _local = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public TunnelClient(
        IMonitoringWorkspaceStore workspaceStore,
        MatmonRuntimeOptions runtimeOptions,
        IServer server,
        ILogger<TunnelClient> logger,
        TunnelAuthSecret tunnelSecret,
        TunnelState tunnelState)
    {
        _workspaceStore = workspaceStore;
        _runtimeOptions = runtimeOptions;
        _server = server;
        _logger = logger;
        _tunnelSecret = tunnelSecret;
        _tunnelState = tunnelState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtimeOptions.Mode != AppMode.Primary)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _workspaceStore.GetCloudConnectionSettings();
            var token = _workspaceStore.GetCloudConnectionToken();
            _tunnelState.SetEnabled(settings.FullAccessEnabled && settings.Enabled);
            var ready = settings.FullAccessEnabled && settings.Enabled &&
                !string.IsNullOrWhiteSpace(settings.Url) && !string.IsNullOrWhiteSpace(settings.InstanceId) && !string.IsNullOrWhiteSpace(token);

            if (!ready)
            {
                await DelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            try
            {
                await RunTunnelAsync(settings.Url!, settings.InstanceId!, token!, stoppingToken);
                // A clean return (settings changed / orderly close) is not a failure - reset the backoff.
                _tunnelState.MarkDisconnected(null, failure: false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                var reason = DescribeConnectFailure(ex);
                _tunnelState.MarkDisconnected(reason, failure: true);
                var failures = _tunnelState.ConsecutiveFailures;
                // First failure at Information, escalate to Warning once it clearly isn't a blip - so an admin who
                // enabled Full Access but sees "not connected" in the cloud has something to diagnose with (a proxy
                // that drops WS upgrades, or a 401/403 handshake) instead of silence at Debug.
                if (failures >= 3)
                {
                    _logger.LogWarning(ex, "Full Access tunnel failing ({Reason}); {Failures} attempts in a row", reason, failures);
                }
                else
                {
                    _logger.LogInformation("Full Access tunnel dropped ({Reason}); reconnecting", reason);
                }
            }

            // Exponential backoff with jitter (5→10→20→40→60s cap), reset once a connection succeeds. A tight 5s
            // retry against a cloud that keeps refusing the handshake (401/403, proxy) is just noise.
            await DelayAsync(BackoffFor(_tunnelState.ConsecutiveFailures), stoppingToken);
        }
    }

    private static TimeSpan BackoffFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.FromSeconds(5);
        }
        var seconds = Math.Min(60, 5 * Math.Pow(2, Math.Min(consecutiveFailures - 1, 4))); // 5,10,20,40,60
        var jitter = (Environment.TickCount64 % 1000) / 1000.0; // 0..1s, no RNG dependency
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromSeconds(jitter);
    }

    private static string DescribeConnectFailure(Exception ex)
    {
        if (ex is WebSocketException wse)
        {
            // The upgrade's HTTP status (when the cloud refused the handshake) is the most useful signal.
            return wse.Message.Contains("401") ? "cloud refused the tunnel: unauthorized (check the instance token)"
                : wse.Message.Contains("403") ? "cloud refused the tunnel: Full Access not licensed / instance blocked"
                : $"connection error: {wse.Message}";
        }
        return ex.Message;
    }

    private async Task RunTunnelAsync(string cloudUrl, string instanceId, string token, CancellationToken stoppingToken)
    {
        var wsUrl = cloudUrl.Trim().TrimEnd('/')
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase);
        var uri = new Uri($"{wsUrl}/api/instances/{instanceId.Trim()}/tunnel");

        // Linked token so we can tear the tunnel down promptly when the settings change - not just when the
        // socket happens to drop. Without this, an already-open socket keeps serving after Full Access is
        // switched off or the cloud is disconnected.
        using var link = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = link.Token;

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Matmon-Instance-Token", token);
        // Detect a silently half-open tunnel (NAT table flush, LB/cloud restart without a FIN) in ~20-40s instead
        // of waiting for TCP retransmits to exhaust (many minutes). The cloud sets a matching keep-alive.
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        _tunnelState.MarkAttempt();
        await socket.ConnectAsync(uri, ct);
        _tunnelState.MarkConnected();
        _logger.LogInformation("Full Access tunnel connected -> {Uri}", uri);

        // Watchdog: close the tunnel as soon as it should no longer be open (Full Access off, cloud
        // disconnected, or the url/id/token changed). Cancelling the linked token unblocks the receive loop.
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    var s = _workspaceStore.GetCloudConnectionSettings();
                    var stillReady = s.FullAccessEnabled && s.Enabled
                        && string.Equals(s.Url?.Trim().TrimEnd('/'), cloudUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(s.InstanceId?.Trim(), instanceId.Trim(), StringComparison.Ordinal)
                        && string.Equals(_workspaceStore.GetCloudConnectionToken(), token, StringComparison.Ordinal);
                    if (!stillReady)
                    {
                        _logger.LogInformation("Full Access tunnel closing (link disabled or changed)");
                        link.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* torn down elsewhere */ }
        }, ct);

        try
        {
            var sendLock = new SemaphoreSlim(1, 1);
            var buffer = new ArrayBufferWriter<byte>();
            var chunk = new byte[16 * 1024];

            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                buffer.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(chunk, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    buffer.Write(chunk.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                var request = JsonSerializer.Deserialize<TunnelRequest>(buffer.WrittenSpan, Json);
                if (request is null)
                {
                    continue;
                }

                // Handle each request without blocking the receive loop (multiplexed responses).
                _ = Task.Run(() => HandleRequestAsync(socket, sendLock, request, ct), ct);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The watchdog closed the tunnel (settings changed) - return normally so the service loop idles
            // and reconnects if Full Access is re-enabled, instead of propagating to a hard stop.
        }
        finally
        {
            link.Cancel(); // stop the watchdog if we exited for another reason (e.g. the socket dropped)
            if (socket.State == WebSocketState.Open)
            {
                // Best-effort clean close so the cloud unregisters the tunnel immediately.
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", closeCts.Token);
                }
                catch { /* ignore */ }
            }
        }
    }

    private async Task HandleRequestAsync(ClientWebSocket socket, SemaphoreSlim sendLock, TunnelRequest request, CancellationToken cancellationToken)
    {
        TunnelResponse response;
        try
        {
            response = await ReplayAsync(request, cancellationToken);
            _tunnelState.MarkRequestServed();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Full Access replay failed for {Path}", request.Path);
            response = new TunnelResponse(request.Id, 502, new(), Convert.ToBase64String("Full Access replay failed."u8.ToArray()));
        }

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, Json);
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            finally
            {
                sendLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Full Access response send failed");
        }
    }

    private async Task<TunnelResponse> ReplayAsync(TunnelRequest request, CancellationToken cancellationToken)
    {
        // Only ever replay against OURSELVES. A network-path ("//evil.com/x") or absolute reference resolved against
        // the self base would dial a third party - carrying the operator's cookies, the identity assertion and our
        // tunnel secret (which then forges Admin auto-logins here). The cloud normalises paths too; this is the
        // instance-side guard, since the instance is what actually dials out.
        var path = string.IsNullOrEmpty(request.Path) ? "/" : request.Path;
        var self = new Uri(SelfBaseUrl());
        if (path.StartsWith("//", StringComparison.Ordinal)
            || path.StartsWith("/\\", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal)
            || !Uri.TryCreate(self, path, out var target)
            || !string.Equals(target.Host, self.Host, StringComparison.OrdinalIgnoreCase)
            || target.Port != self.Port)
        {
            return new TunnelResponse(request.Id, 400, new(), Convert.ToBase64String("Invalid Full Access path."u8.ToArray()));
        }

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), target);

        if (!string.IsNullOrEmpty(request.Body))
        {
            message.Content = new ByteArrayContent(Convert.FromBase64String(request.Body));
        }

        foreach (var (key, values) in request.Headers)
        {
            // Host: let HttpClient set it from the local base. Accept-Encoding: let AutomaticDecompression
            // manage it (we forward decompressed plain bytes), else the browser's br/gzip pref leaks through.
            // X-Matmon-Tunnel-Auth: never trust an inbound value - only we (below) may set the real secret.
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
                key.Equals(TunnelAutoLogin.TunnelAuthHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(key, values) && message.Content is not null)
            {
                message.Content.Headers.TryAddWithoutValidation(key, values);
            }
        }

        // Prove to the local pipeline that this request came through our own tunnel (in-process secret), so the
        // auto-login middleware may trust the cloud's X-Matmon-Cloud-User identity assertion carried above.
        message.Headers.TryAddWithoutValidation(TunnelAutoLogin.TunnelAuthHeader, _tunnelSecret.Value);

        using var reply = await _local.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Cap the response before buffering it: one over-large frame would tear the whole tunnel down for every
        // console user of this instance. Refuse early (declared length) or while reading (chunked/unknown length).
        if (reply.Content.Headers.ContentLength is > MaxReplayBodyBytes)
        {
            return TooLargeResponse(request.Id);
        }

        var selfBase = SelfBaseUrl();
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in reply.Headers)
        {
            // The local app can emit absolute self-URLs (e.g. the cookie-auth login redirect). Make them
            // root-relative so the cloud can re-prefix them into the /instances/{id}/app path.
            headers[header.Key] = header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)
                ? header.Value.Select(v => StripSelfBase(v, selfBase)).ToArray()
                : header.Value.ToArray();
        }
        foreach (var header in reply.Content.Headers)
        {
            headers[header.Key] = header.Value.ToArray();
        }

        var body = await ReadCappedAsync(reply.Content, cancellationToken);
        if (body is null)
        {
            return TooLargeResponse(request.Id);
        }
        return new TunnelResponse(request.Id, (int)reply.StatusCode, headers, body.Length == 0 ? null : Convert.ToBase64String(body));
    }

    /// <summary>Reads the response body but aborts (returns null) once it exceeds <see cref="MaxReplayBodyBytes"/>,
    /// so a chunked/unknown-length response can't be buffered without bound.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxReplayBodyBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static TunnelResponse TooLargeResponse(string requestId)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = ["text/plain; charset=utf-8"]
        };
        return new TunnelResponse(requestId, 413, headers,
            Convert.ToBase64String("This response is too large for Full Access. Download it directly on the instance."u8.ToArray()));
    }

    private static string StripSelfBase(string location, string selfBase)
    {
        if (location.StartsWith(selfBase, StringComparison.OrdinalIgnoreCase))
        {
            var rest = location[selfBase.Length..];
            return rest.StartsWith('/') ? rest : "/" + rest;
        }

        return location;
    }

    private string SelfBaseUrl()
    {
        var address = _server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address))
        {
            return "http://localhost:8099";
        }

        // Normalise wildcard binds to a loopback address the client can dial.
        return address
            .Replace("://+", "://localhost", StringComparison.Ordinal)
            .Replace("://[::]", "://localhost", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record TunnelRequest(string Id, string Method, string Path, Dictionary<string, string[]> Headers, string? Body);

    private sealed record TunnelResponse(string Id, int Status, Dictionary<string, string[]> Headers, string? Body);
}
