using System.Net;
using System.Net.NetworkInformation;

namespace Matmon.Core.Domain;

/// <summary>
/// "This machine" as a sensor target - the one rule both remote engines (PowerShell/WinRM and SSH) use to
/// decide that a script should simply run locally, which is what an agent monitoring the box it sits on wants:
/// no listener, no credential. No target, localhost / loopback / ".", the machine's own name (bare or as the
/// first label of an FQDN), or one of the machine's own IP addresses - an agent bound to a host that was added by
/// its address measures that address, and it used to go out over WinRM to itself (and fail on a Windows box
/// without PowerShell 7). Anything else - including another host's IP - stays remote.
/// </summary>
public static class LocalTarget
{
    private static readonly TimeSpan AddressCacheLifetime = TimeSpan.FromMinutes(1);
    private static (DateTime Read, HashSet<IPAddress> Addresses)? _ownAddresses;

    public static bool Is(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return true;
        }

        var host = target.Trim();
        if (host is "." or "localhost" or "127.0.0.1" or "::1" or "[::1]")
        {
            return true;
        }

        var machine = Environment.MachineName;
        if (string.Equals(host, machine, StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith(machine + ".", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IsOwnAddress(address);
    }

    /// <summary>Whether the address is a loopback or one of this machine's interface addresses.</summary>
    public static bool IsOwnAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return OwnAddresses().Contains(address);
    }

    private static HashSet<IPAddress> OwnAddresses()
    {
        var cached = _ownAddresses;
        if (cached is { } known && DateTime.UtcNow - known.Read < AddressCacheLifetime)
        {
            return known.Addresses;
        }

        var addresses = new HashSet<IPAddress>();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    var own = unicast.Address;
                    if (own.IsIPv6LinkLocal && own.ScopeId != 0)
                    {
                        // A typed address carries no zone - compare without it.
                        own = new IPAddress(own.GetAddressBytes());
                    }
                    addresses.Add(own);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No interface list on this platform: only names and loopback count as local.
        }

        _ownAddresses = (DateTime.UtcNow, addresses);
        return addresses;
    }
}
