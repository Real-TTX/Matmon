using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Matmon.Core.Domain;

namespace Matmon.Host.Services.WebPush;

/// <summary>What a browser sends when it subscribes (PushSubscription.toJSON()).</summary>
public sealed record BrowserPushSubscription(string? Endpoint, BrowserPushKeys? Keys);

public sealed record BrowserPushKeys(string? P256dh, string? Auth);

public enum PushOutcome
{
    /// <summary>The push service took the message; it reaches the device when the device is online.</summary>
    Delivered,

    /// <summary>The subscription is gone (notifications switched off, the browser dropped it): forget the device.</summary>
    Gone,

    /// <summary>Something else went wrong; the subscription may be fine.</summary>
    Failed
}

/// <summary>What a browser notification shows - the service worker (sw.js) reads exactly these fields.</summary>
/// <param name="Tag">A new notification with the same tag replaces the old one on the device (one per alert).</param>
/// <param name="Badge">The number on the app icon (open alerts), where the system shows one.</param>
public sealed record BrowserNotification(string Title, string Body, string Url, string? Tag = null, int? Badge = null, bool Urgent = false);

/// <summary>
/// Posts an encrypted message to the push service of a subscription (RFC 8030), signed as this instance (VAPID) - the
/// MatMail pattern. The connection goes to a PUBLIC address only: the endpoint is what a browser told us, and without
/// that check a signed-in user could make the instance post into its own network by naming such an address.
/// </summary>
public sealed class WebPushClient(HttpClient http, WebPushKeys keys, IMonitoringWorkspaceStore store, ILogger<WebPushClient> logger)
{
    public const string HttpClientName = "matmon-webpush";

    /// <summary>Who the push services may contact about this instance: the first admin's e-mail, else the project's site.</summary>
    private string Subject =>
        store.ResolveBuiltInRecipients(NotificationReceiverDefaults.AllAdminsReceiverId).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } admin
            ? "mailto:" + admin
            : "https://matmon.eu";

    public async Task<PushOutcome> SendAsync(PushDevice device, BrowserNotification notification, CancellationToken cancellationToken)
    {
        var receiver = WebPushEncryption.FromBase64Url(device.P256dh);
        var auth = WebPushEncryption.FromBase64Url(device.Auth);
        if (receiver is not { Length: 65 } || auth is not { Length: 16 }
            || !Uri.TryCreate(device.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return PushOutcome.Gone; // nothing that could ever work
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            title = Trim(notification.Title, 120),
            body = Trim(notification.Body, 900),
            url = notification.Url,
            tag = notification.Tag,
            badge = notification.Badge
        });
        var body = WebPushEncryption.Encrypt(payload, receiver, auth);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("Authorization", keys.Authorization(device.Endpoint, Subject, DateTimeOffset.UtcNow));
        request.Headers.TryAddWithoutValidation("TTL", "86400");
        request.Headers.TryAddWithoutValidation("Urgency", notification.Urgent ? "high" : "normal");
        if (notification.Tag is { Length: > 0 } tag)
        {
            // A newer message for the same alert replaces one the push service has not delivered yet (32 base64url chars max).
            request.Headers.TryAddWithoutValidation("Topic", WebPushEncryption.ToBase64Url(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tag)))[..32]);
        }

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return PushOutcome.Delivered;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return PushOutcome.Gone;
            }

            logger.LogWarning("The push service {Host} answered {Status}.", endpoint.Host, (int)response.StatusCode);
            return PushOutcome.Failed;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The push service {Host} could not be reached: {Reason}", endpoint.Host, exception.Message);
            return PushOutcome.Failed;
        }
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>Opens the connection only to a public address (see the class remarks).</summary>
    internal static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var candidates = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)).Where(IsPublic).ToArray();
        if (candidates.Length == 0)
        {
            throw new HttpRequestException("The push address does not lead to a public server.");
        }

        SocketException? last = null;
        foreach (var target in candidates)
        {
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                last = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("The push service could not be reached.", last);
    }

    /// <summary>An address of the internet: not this machine, not a private network, not link-local, not multicast.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] >= 224
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);
    }
}

/// <summary>Sends one notification to a set of devices: forgets the ones that are gone, notes when one was reached.</summary>
public sealed class BrowserPushNotifier(WebPushClient client, IMonitoringWorkspaceStore store)
{
    public sealed record Result(int Delivered, int Failed, int Gone);

    public async Task<Result> SendAsync(IReadOnlyList<PushDevice> devices, BrowserNotification notification, CancellationToken cancellationToken)
    {
        int delivered = 0, failed = 0, gone = 0;
        foreach (var device in devices)
        {
            switch (await client.SendAsync(device, notification, cancellationToken))
            {
                case PushOutcome.Delivered:
                    delivered++;
                    store.MarkPushDeviceUsed(device.Id, DateTimeOffset.UtcNow);
                    break;
                case PushOutcome.Gone:
                    gone++;
                    store.RemovePushDevice(device.Id, userId: null);
                    break;
                default:
                    failed++;
                    break;
            }
        }

        return new Result(delivered, failed, gone);
    }
}
