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

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly IServer _server;
    private readonly ILogger<TunnelClient> _logger;
    private readonly TunnelAuthSecret _tunnelSecret;
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
        TunnelAuthSecret tunnelSecret)
    {
        _workspaceStore = workspaceStore;
        _runtimeOptions = runtimeOptions;
        _server = server;
        _logger = logger;
        _tunnelSecret = tunnelSecret;
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
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Full Access tunnel dropped; reconnecting");
            }

            await DelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
        }
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
        await socket.ConnectAsync(uri, ct);
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
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Full Access replay failed for {Path}", request.Path);
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
        var target = new Uri(new Uri(SelfBaseUrl()), request.Path);
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

        using var reply = await _local.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);
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

        var body = await reply.Content.ReadAsByteArrayAsync(cancellationToken);
        return new TunnelResponse(request.Id, (int)reply.StatusCode, headers, body.Length == 0 ? null : Convert.ToBase64String(body));
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
