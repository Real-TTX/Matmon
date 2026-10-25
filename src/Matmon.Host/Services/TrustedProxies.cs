using System.Net;

namespace Matmon.Host.Services;

/// <summary>
/// Which senders may tell the instance, through <c>X-Forwarded-For</c> / <c>-Proto</c> / <c>-Host</c>, who the client
/// really is. The forwarded headers used to be believed from ANYONE, so a client could name any address it liked:
/// the per-address rate limits (agent enrolment, push) then counted nothing, and the scheme/host the instance builds
/// its own links from could be chosen by the caller. A reverse proxy in front of a self-hosted instance is on the
/// same machine, the LAN or the Docker network, so by default only those (loopback, RFC 1918, link-local, IPv6 ULA)
/// are trusted. <c>Matmon__TrustedProxies</c> replaces that list with its own (addresses or CIDR ranges, separated by
/// commas, semicolons or line breaks), and <c>any</c> trusts every sender again - for a proxy on a public address
/// that cannot be named.
/// </summary>
public static class TrustedProxies
{
    public const string AnyKeyword = "any";

    public static readonly IReadOnlyList<string> PrivateRanges =
    [
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16",
        "::1/128", "fc00::/7", "fe80::/10"
    ];

    /// <summary>The networks to trust, or null for "any sender". Entries that do not parse are returned in
    /// <paramref name="ignored"/> - the caller logs them; they never widen the list.</summary>
    public static IReadOnlyList<System.Net.IPNetwork>? Resolve(string? configured, out IReadOnlyList<string> ignored)
    {
        var tokens = string.IsNullOrWhiteSpace(configured)
            ? PrivateRanges
            : configured.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Any(token => string.Equals(token, AnyKeyword, StringComparison.OrdinalIgnoreCase) || token == "*"))
        {
            ignored = [];
            return null;
        }

        var networks = new List<System.Net.IPNetwork>();
        var bad = new List<string>();
        foreach (var token in tokens)
        {
            if (System.Net.IPNetwork.TryParse(token, out var network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(token, out var address))
            {
                networks.Add(new System.Net.IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                bad.Add(token);
            }
        }

        ignored = bad;
        return networks;
    }
}
