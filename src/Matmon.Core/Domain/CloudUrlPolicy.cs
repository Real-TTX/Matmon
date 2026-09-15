using System.Net;
using System.Net.Sockets;

namespace Matmon.Core.Domain;

/// <summary>
/// Guards against sending cloud credentials (the provision password, or a claim round-trip) over plain HTTP to a
/// PUBLIC host, where they would travel unencrypted. HTTPS is always fine; plain HTTP is allowed only to a host
/// that is clearly on a trusted local network - loopback, a private (RFC1918 / link-local / ULA) IP, a dotless
/// hostname (e.g. a Docker service alias like <c>matmon-cloud</c>) or a <c>.local</c> name. A public http URL is
/// refused so a typo like <c>http://cloud.example.com</c> can't leak the password.
/// </summary>
public static class CloudUrlPolicy
{
    public static bool IsPlainHttpAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true; // encrypted - always fine
        }
        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            return false; // only http/https are cloud URLs
        }

        if (uri.IsLoopback)
        {
            return true; // localhost / 127.0.0.1 / ::1
        }

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }
        if (!host.Contains('.') && !host.Contains(':'))
        {
            return true; // dotless hostname => a LAN/Docker service alias, not a public FQDN
        }
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true; // mDNS / .local names are local
        }
        return IsPrivateOrLocalIp(host);
    }

    private static bool IsPrivateOrLocalIp(string host)
    {
        // Uri.Host wraps an IPv6 literal in [...]; strip the brackets before parsing.
        var candidate = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (!IPAddress.TryParse(candidate, out var ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                10 => true,                                   // 10.0.0.0/8
                127 => true,                                  // 127.0.0.0/8
                169 when b[1] == 254 => true,                 // 169.254.0.0/16 link-local
                172 when b[1] is >= 16 and <= 31 => true,     // 172.16.0.0/12
                192 when b[1] == 168 => true,                 // 192.168.0.0/16
                _ => false
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal)
            {
                return true; // fe80::/10
            }
            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // fc00::/7 unique-local
        }

        return false;
    }
}
