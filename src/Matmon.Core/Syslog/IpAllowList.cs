using System.Net;
using System.Net.Sockets;

namespace Matmon.Core.Syslog;

/// <summary>
/// Who may send syslog: a list of addresses and CIDR ranges. An EMPTY list allows everyone - syslog has no login to
/// fall back on, so this is the only gate, and an admin who has not set one gets the permissive default knowingly
/// (the settings page says so).
/// </summary>
public sealed class IpAllowList
{
    private readonly List<(byte[] Network, int Bits)> _ranges = [];

    private IpAllowList()
    {
    }

    /// <summary>True when nothing is configured - everything is allowed.</summary>
    public bool IsEmpty => _ranges.Count == 0;

    public int RangeCount => _ranges.Count;

    /// <summary>Reads "10.0.0.0/8, 192.168.1.20 fd00::/8" (commas, semicolons, spaces and new lines all separate).
    /// Entries that are not an address or range are returned in <paramref name="invalid"/> rather than silently
    /// dropped - a typo must not turn into "allow everybody".</summary>
    public static IpAllowList Parse(string? text, out IReadOnlyList<string> invalid)
    {
        var list = new IpAllowList();
        var bad = new List<string>();
        invalid = bad;

        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }

        foreach (var token in text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseRange(token, out var network, out var bits))
            {
                list._ranges.Add((network, bits));
            }
            else
            {
                bad.Add(token);
            }
        }

        return list;
    }

    public bool Allows(IPAddress address)
    {
        if (IsEmpty)
        {
            return true;
        }

        // A dual-mode socket reports an IPv4 sender as ::ffff:a.b.c.d - test it as the IPv4 address it is.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        foreach (var (network, bits) in _ranges)
        {
            if (network.Length == bytes.Length && Matches(bytes, network, bits))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseRange(string token, out byte[] network, out int bits)
    {
        network = [];
        bits = 0;

        var slash = token.IndexOf('/');
        var addressText = slash < 0 ? token : token[..slash];
        if (!IPAddress.TryParse(addressText, out var address) ||
            (address.AddressFamily is not AddressFamily.InterNetwork and not AddressFamily.InterNetworkV6))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        var max = bytes.Length * 8;
        if (slash < 0)
        {
            bits = max;
        }
        else if (!int.TryParse(token[(slash + 1)..], out bits) || bits < 0 || bits > max)
        {
            return false;
        }

        network = bytes;
        return true;
    }

    private static bool Matches(byte[] address, byte[] network, int bits)
    {
        var whole = bits / 8;
        for (var index = 0; index < whole; index++)
        {
            if (address[index] != network[index])
            {
                return false;
            }
        }

        var remainder = bits % 8;
        if (remainder == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainder));
        return (address[whole] & mask) == (network[whole] & mask);
    }
}
