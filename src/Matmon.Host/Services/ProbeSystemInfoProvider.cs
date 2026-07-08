using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Matmon.Host.Services;

/// <summary>System details a probe reports about itself in the heartbeat "full sync".</summary>
public sealed record ProbeSystemInfo(string OperatingSystem, string Host, IReadOnlyList<string> Networks);

/// <summary>
/// Collects the local operating system, host name and reachable IPv4 subnets (as CIDR) so a secondary
/// probe can report them to the primary - surfaced as probe details and as discovery scan suggestions.
/// </summary>
public static class ProbeSystemInfoProvider
{
    public static ProbeSystemInfo Collect()
    {
        var networks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue; // IPv4 subnets only
                    }

                    var ip = unicast.Address;
                    if (IPAddress.IsLoopback(ip))
                    {
                        continue;
                    }

                    var bytes = ip.GetAddressBytes();
                    if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
                    {
                        continue; // APIPA / link-local
                    }

                    var prefix = unicast.PrefixLength is > 0 and <= 32 ? unicast.PrefixLength : 24;
                    if (ToCidr(ip, prefix) is { } cidr)
                    {
                        networks.Add(cidr);
                    }
                }
            }
        }
        catch
        {
            // Network enumeration is best-effort; never let it break the heartbeat.
        }

        return new ProbeSystemInfo(
            RuntimeInformation.OSDescription,
            Environment.MachineName,
            networks.ToArray());
    }

    private static string? ToCidr(IPAddress ip, int prefix)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return null;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsLeft = prefix - (i * 8);
            byte mask = bitsLeft >= 8 ? (byte)0xFF : bitsLeft <= 0 ? (byte)0x00 : (byte)(0xFF << (8 - bitsLeft));
            bytes[i] &= mask;
        }

        return $"{new IPAddress(bytes)}/{prefix}";
    }
}
