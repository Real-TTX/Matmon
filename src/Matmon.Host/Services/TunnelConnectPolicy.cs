using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Matmon.Host.Services;

public enum TunnelDecision
{
    /// <summary>Nothing to do: Full Access is off, or there is no cloud link to open it on.</summary>
    Idle,

    /// <summary>Full Access is switched on and linked, but the current plan does not include it.</summary>
    NotIncluded,

    /// <summary>Open the tunnel.</summary>
    Connect
}

/// <summary>
/// When the Full Access client knocks on the cloud's door, and how long it waits after being turned away - the
/// decisions, kept apart from the WebSocket so they can be tested. It used to ask only "is it switched on and
/// linked?", never "does the plan include it?": a switched-on instance on a plan without Full Access was refused
/// (403) every minute for as long as it ran - 3705 attempts in a row on a local instance - and every refusal logged
/// a warning with a stack trace.
/// </summary>
public static class TunnelConnectPolicy
{
    /// <summary>How long to wait after a DEFINITIVE refusal (the cloud answered 401/403 at the handshake).</summary>
    public static readonly TimeSpan RefusedRetry = TimeSpan.FromMinutes(10);

    /// <summary>How often to look again while the plan does not include Full Access. The licence is re-read on
    /// every cloud heartbeat, so an upgrade shows up here within one beat.</summary>
    public static readonly TimeSpan NotIncludedPoll = TimeSpan.FromSeconds(20);

    /// <summary>How often a long wait checks whether anything that could change the answer changed.</summary>
    public static readonly TimeSpan ChangePoll = TimeSpan.FromSeconds(15);

    public static TunnelDecision Decide(bool fullAccessEnabled, bool linkEnabled, bool hasLinkCredentials, bool planIncludesFullAccess)
    {
        if (!fullAccessEnabled || !linkEnabled || !hasLinkCredentials)
        {
            return TunnelDecision.Idle;
        }

        return planIncludesFullAccess ? TunnelDecision.Connect : TunnelDecision.NotIncluded;
    }

    /// <summary>The cloud answered the handshake with "no" (401 unauthorized / 403 not licensed or blocked) - as opposed
    /// to a network error, which is worth retrying soon. Retrying a definitive answer every minute only adds noise.</summary>
    public static bool IsDefinitiveRefusal(Exception exception) =>
        exception is WebSocketException socket
        && (socket.Message.Contains("401", StringComparison.Ordinal) || socket.Message.Contains("403", StringComparison.Ordinal));

    /// <summary>Exponential backoff with jitter for ordinary failures: 5, 10, 20, 40, 60 s (cap), reset once a
    /// connection succeeds. <paramref name="tickMs"/> supplies the jitter so tests stay deterministic.</summary>
    public static TimeSpan Backoff(int consecutiveFailures, long tickMs)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.FromSeconds(5);
        }

        var seconds = Math.Min(60, 5 * Math.Pow(2, Math.Min(consecutiveFailures - 1, 4)));
        var jitter = (tickMs % 1000) / 1000.0; // 0..1 s, no RNG dependency
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromSeconds(jitter);
    }

    /// <summary>Everything that could change the cloud's answer, as one comparable string. The token is hashed, not
    /// kept.</summary>
    public static string Signature(Matmon.Core.Domain.CloudConnectionSettings settings, string? token, bool planIncludesFullAccess)
    {
        var tokenHash = string.IsNullOrEmpty(token) ? "-" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..12];
        return $"{settings.FullAccessEnabled}|{settings.Enabled}|{settings.Url?.Trim().TrimEnd('/')}|{settings.InstanceId?.Trim()}|{tokenHash}|{planIncludesFullAccess}";
    }

    /// <summary>
    /// A long wait that ends EARLY when something that could change the answer changes: a plan upgrade, a new token
    /// after a reconnect, Full Access toggled. Without this a refused instance would sit out the whole wait even
    /// though it can connect now. <paramref name="delay"/> is injectable for tests.
    /// </summary>
    public static async Task WaitForChangeAsync(
        Func<string> readSignature,
        TimeSpan max,
        TimeSpan poll,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= static async (span, token) =>
        {
            try
            {
                await Task.Delay(span, token);
            }
            catch (OperationCanceledException)
            {
                // shutting down - the loop's own check ends it
            }
        };

        var before = readSignature();
        var waited = TimeSpan.Zero;
        while (waited < max && !cancellationToken.IsCancellationRequested)
        {
            var step = poll < max - waited ? poll : max - waited;
            await delay(step, cancellationToken);
            waited += step;
            if (cancellationToken.IsCancellationRequested || !string.Equals(readSignature(), before, StringComparison.Ordinal))
            {
                return;
            }
        }
    }
}
