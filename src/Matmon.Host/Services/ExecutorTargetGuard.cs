using System.Globalization;
using System.Net;

namespace Matmon.Host.Services;

/// <summary>
/// The stateless Executor runs what a cloud CUSTOMER typed: a host to ping, a port to open, a certificate to read, a name
/// to resolve. Without a check, naming an internal address would let anyone with a cloud account scan the network the
/// cloud runs in (its database, the cloud itself, a metadata endpoint). So before ANY sensor runs, the host it will talk
/// to is resolved and every address must be public (<see cref="PublicNetwork.IsPublic"/>) - else the run is refused.
/// For DNS that is the DNS server when one is named; without one the name is resolved here and an answer that points
/// into a private network is refused too (it would tell the customer the cloud's internal addresses).
/// The HTTP sensors keep their connect-time check as well: it is made on the address actually connected to, so it also
/// covers redirects and a name that resolves differently a moment later (this check alone would not).
/// </summary>
public static class ExecutorTargetGuard
{
    public delegate Task<IPAddress[]> Resolver(string host, CancellationToken cancellationToken);

    public const string Refusal = "cloud sensors only check what is reachable from the internet";

    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    public static Task<IPAddress[]> SystemResolver(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <summary>Null when the sensor may run; otherwise why not (becomes the Critical message).</summary>
    public static async Task<string?> CheckAsync(
        string sensorTypeKey,
        string? target,
        IReadOnlyDictionary<string, string> parameters,
        Resolver resolver,
        CancellationToken cancellationToken)
    {
        string? host;
        if (string.Equals(sensorTypeKey, "dns", StringComparison.OrdinalIgnoreCase))
        {
            var server = Read(parameters, "dns.server");
            host = server is not null
                ? HostOf(server)
                : HostOf(Read(parameters, "dns.recordName") ?? target ?? string.Empty);
        }
        else
        {
            host = HostOf(target ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return $"No target host - {Refusal}.";
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ResolveTimeout);
                addresses = await resolver(host, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return $"{host} could not be resolved (timeout).";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return $"{host} could not be resolved: {exception.Message}";
            }
        }

        if (addresses.Length == 0)
        {
            return $"{host} could not be resolved.";
        }

        // EVERY address must be public: a name with one public and one private address could be connected to the private one.
        return addresses.All(PublicNetwork.IsPublic)
            ? null
            : $"{host} does not lead to a public address - {Refusal}.";
    }

    /// <summary>The host of a target: the host of a URL, else "host", "host:port", "[v6]:port" or a bare IPv6 address.</summary>
    public static string? HostOf(string target)
    {
        var text = target.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host.Trim('[', ']')
                : null;
        }

        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            return close > 1 ? text[1..close] : null;
        }

        if (IPAddress.TryParse(text, out _))
        {
            return text; // also a bare IPv6 address, whose colons are not a port
        }

        // "host/path" without a scheme: the host is what comes before the path
        var slash = text.IndexOf('/');
        if (slash >= 0)
        {
            text = text[..slash];
        }

        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon
            && int.TryParse(text[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            text = text[..colon];
        }

        return text.Length == 0 ? null : text.TrimEnd('.');
    }

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
    {
        foreach (var (name, value) in parameters)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        return null;
    }
}
