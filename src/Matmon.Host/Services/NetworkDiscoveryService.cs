using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class NetworkDiscoveryService
{
    private const int MaxHardHostLimit = 1024;
    private readonly IReadOnlyList<ISensorExecutor> _sensorExecutors;

    public NetworkDiscoveryService(IEnumerable<ISensorExecutor> sensorExecutors)
    {
        _sensorExecutors = sensorExecutors
            .GroupBy(executor => executor.SensorTypeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public async Task<IReadOnlyList<NetworkDiscoveryResult>> DiscoverAsync(
        NetworkDiscoveryRequest request,
        Func<NetworkDiscoveryResult, CancellationToken, ValueTask>? onResultDiscovered = null,
        CancellationToken cancellationToken = default)
    {
        var options = request.Options.Normalized();
        var addresses = NetworkTargetParser.Parse(request.Network, options.MaxHosts).ToArray();
        if (addresses.Length == 0)
        {
            return [];
        }

        var results = new List<NetworkDiscoveryResult>();
        using var gate = new SemaphoreSlim(Math.Clamp(options.Parallelism, 1, 128));
        var tasks = addresses.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await ProbeAddressAsync(address, options, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        var pendingTasks = tasks.ToList();
        while (pendingTasks.Count > 0)
        {
            var task = await Task.WhenAny(pendingTasks);
            pendingTasks.Remove(task);

            var result = await task;
            if (result.IsDiscovered)
            {
                results.Add(result);
                if (onResultDiscovered is not null)
                {
                    await onResultDiscovered(result, cancellationToken);
                }
            }
        }

        return results
            .OrderBy(result => NetworkTargetParser.ToSortableAddress(result.Address))
            .ToArray();
    }

    private async Task<NetworkDiscoveryResult> ProbeAddressAsync(
        string address,
        NetworkDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(options.TimeoutMs);
        var pingMs = options.UsePing
            ? await TryPingAsync(address, timeout, cancellationToken)
            : null;
        var openPorts = options.UseTcpPorts
            ? await FindOpenTcpPortsAsync(address, options.TcpPorts, timeout, cancellationToken)
            : [];
        var (snmpResponded, snmpSummary) = options.UseSnmp
            ? await TryProbeSnmpAsync(address, options, timeout, cancellationToken)
            : (false, null);
        var hostName = options.UseReverseDns
            ? await TryReverseDnsAsync(address, TimeSpan.FromMilliseconds(Math.Min(options.TimeoutMs, 900)), cancellationToken)
            : null;
        var suggestedSensors = await DiscoverSensorSuggestionsAsync(
            new SensorDiscoveryContext(
                address,
                pingMs is not null,
                pingMs,
                openPorts,
                snmpResponded,
                snmpSummary,
                options.SnmpCommunity,
                options.SnmpVersion,
                options.SnmpPort,
                timeout),
            cancellationToken);

        var messages = new List<string>();
        if (pingMs is double latency)
        {
            messages.Add($"ping {latency:0.#} ms");
        }

        if (openPorts.Count > 0)
        {
            messages.Add($"ports {string.Join(", ", openPorts)}");
        }

        if (snmpResponded)
        {
            messages.Add(string.IsNullOrWhiteSpace(snmpSummary) ? "snmp" : $"snmp {snmpSummary}");
        }

        if (suggestedSensors.Count > 0)
        {
            messages.Add($"{suggestedSensors.Count} sensor suggestion{(suggestedSensors.Count == 1 ? string.Empty : "s")}");
        }

        return new NetworkDiscoveryResult(
            address,
            hostName,
            pingMs is not null,
            pingMs,
            openPorts,
            snmpResponded,
            snmpSummary,
            messages.Count == 0 ? "not reachable" : string.Join(" | ", messages))
        {
            SuggestedSensors = suggestedSensors
        };
    }

    private async Task<IReadOnlyList<SensorDiscoverySuggestion>> DiscoverSensorSuggestionsAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken)
    {
        var suggestions = new List<SensorDiscoverySuggestion>();

        foreach (var executor in _sensorExecutors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await executor.DiscoverAsync(context, cancellationToken);
                if (!result.IsAvailable)
                {
                    continue;
                }

                suggestions.AddRange(result.Suggestions.Where(suggestion =>
                    string.Equals(suggestion.SensorTypeKey, executor.SensorTypeKey, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                // Discovery checks should never make the whole host scan fail.
            }
        }

        return suggestions
            .GroupBy(BuildSuggestionKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(suggestion => suggestion.Confidence).First())
            .OrderByDescending(suggestion => suggestion.Confidence)
            .ThenBy(suggestion => suggestion.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<double?> TryPingAsync(
        string address,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue))
                .WaitAsync(timeout + TimeSpan.FromMilliseconds(100), cancellationToken);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<int>> FindOpenTcpPortsAsync(
        string address,
        IReadOnlyList<int> ports,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var openPorts = new List<int>();
        foreach (var port in ports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsTcpPortOpenAsync(address, port, timeout, cancellationToken))
            {
                openPorts.Add(port);
            }
        }

        return openPorts;
    }

    private static async Task<bool> IsTcpPortOpenAsync(
        string address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, timeoutCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(bool Responded, string? Summary)> TryProbeSnmpAsync(
        string address,
        NetworkDiscoveryOptions options,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = new MonitoringSettings
            {
                Timeout = timeout
            };
            settings.Parameters["snmp.community"] = options.SnmpCommunity;
            settings.Parameters["snmp.version"] = options.SnmpVersion;
            settings.Parameters["snmp.port"] = options.SnmpPort.ToString(CultureInfo.InvariantCulture);

            var items = await SnmpSensorExecutor.DiscoverAsync(
                address,
                settings,
                "1.3.6.1.2.1.1",
                timeout,
                cancellationToken);

            var summary = items.FirstOrDefault(item => item.Oid.EndsWith(".1.0", StringComparison.Ordinal))?.Value
                ?? items.FirstOrDefault()?.Value;
            return (items.Count > 0, Truncate(summary, 80));
        }
        catch
        {
            return (false, null);
        }
    }

    private static async Task<string?> TryReverseDnsAsync(
        string address,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address, cancellationToken).WaitAsync(timeout, cancellationToken);
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch
        {
            return null;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string BuildSuggestionKey(SensorDiscoverySuggestion suggestion)
    {
        var settings = suggestion.Settings ?? new MonitoringSettings();
        var parameterSignature = string.Join(
            ";",
            settings.Parameters
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));

        return string.Join(
            "|",
            suggestion.SensorTypeKey,
            suggestion.Name,
            suggestion.Target,
            settings.DefaultChannelKey ?? string.Empty,
            parameterSignature);
    }
}

public sealed record NetworkDiscoveryRequest(
    Guid JobId,
    string Network,
    NetworkDiscoveryOptions Options);

public sealed record NetworkDiscoveryOptions(
    bool UsePing,
    bool UseTcpPorts,
    IReadOnlyList<int> TcpPorts,
    bool UseSnmp,
    string SnmpCommunity,
    string SnmpVersion,
    int SnmpPort,
    bool UseReverseDns,
    int TimeoutMs,
    int MaxHosts,
    int Parallelism)
{
    public NetworkDiscoveryOptions Normalized()
    {
        var ports = TcpPorts
            .Where(port => port is >= 1 and <= 65535)
            .Distinct()
            .Take(32)
            .ToArray();

        return this with
        {
            UsePing = UsePing || (!UseTcpPorts && !UseSnmp),
            TcpPorts = ports,
            SnmpCommunity = string.IsNullOrWhiteSpace(SnmpCommunity) ? "public" : SnmpCommunity.Trim(),
            SnmpVersion = NormalizeSnmpVersion(SnmpVersion),
            SnmpPort = SnmpPort is >= 1 and <= 65535 ? SnmpPort : 161,
            TimeoutMs = Math.Clamp(TimeoutMs, 150, 10_000),
            MaxHosts = Math.Clamp(MaxHosts, 1, 1024),
            Parallelism = Math.Clamp(Parallelism, 1, 128)
        };
    }

    private static string NormalizeSnmpVersion(string? version)
    {
        return version?.Trim().ToLowerInvariant() switch
        {
            "v1" => "v1",
            _ => "v2c"
        };
    }
}

public sealed record NetworkDiscoveryResult(
    string Address,
    string? HostName,
    bool PingAlive,
    double? PingMs,
    IReadOnlyList<int> OpenPorts,
    bool SnmpResponded,
    string? SnmpSummary,
    string Message)
{
    public IReadOnlyList<SensorDiscoverySuggestion> SuggestedSensors { get; init; } = [];

    public bool IsDiscovered => PingAlive || OpenPorts.Count > 0 || SnmpResponded || SuggestedSensors.Count > 0;
}

internal static class NetworkTargetParser
{
    public static IReadOnlyList<string> Parse(string rawTargets, int maxHosts)
    {
        if (string.IsNullOrWhiteSpace(rawTargets))
        {
            return [];
        }

        var limit = Math.Clamp(maxHosts, 1, MaxHardHostLimit);
        var addresses = new List<string>(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = rawTargets
            .Split([',', ';', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            foreach (var address in ParseToken(token, limit - addresses.Count))
            {
                if (seen.Add(address))
                {
                    addresses.Add(address);
                }

                if (addresses.Count >= limit)
                {
                    return addresses;
                }
            }
        }

        return addresses;
    }

    public static long ToSortableAddress(string address)
    {
        return IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork
            ? ToUInt32(parsed)
            : long.MaxValue;
    }

    private static IEnumerable<string> ParseToken(string token, int remaining)
    {
        if (remaining <= 0)
        {
            yield break;
        }

        if (token.Contains('/', StringComparison.Ordinal))
        {
            foreach (var address in ParseCidr(token, remaining))
            {
                yield return address;
            }

            yield break;
        }

        if (token.Contains('-', StringComparison.Ordinal))
        {
            var emittedRangeAddress = false;
            foreach (var address in ParseRange(token, remaining))
            {
                emittedRangeAddress = true;
                yield return address;
            }

            if (emittedRangeAddress)
            {
                yield break;
            }
        }

        if (IPAddress.TryParse(token, out var ipAddress) && ipAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            yield return ipAddress.ToString();
            yield break;
        }

        if (IsHostNameToken(token))
        {
            yield return token.Trim();
        }
    }

    private static bool IsHostNameToken(string token)
    {
        var normalized = token.Trim().Trim('.');
        if (normalized.Length is 0 or > 253)
        {
            return false;
        }

        if (normalized.Contains('/', StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '-' or '.' or '_');
    }

    private static IEnumerable<string> ParseCidr(string token, int remaining)
    {
        var parts = token.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var network) ||
            network.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix) ||
            prefix is < 0 or > 32)
        {
            yield break;
        }

        var networkValue = ToUInt32(network);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var start = networkValue & mask;
        var count = prefix == 32 ? 1UL : 1UL << (32 - prefix);
        var first = prefix <= 30 ? start + 1UL : start;
        var last = prefix <= 30 ? start + count - 2UL : start + count - 1UL;

        for (var value = first; value <= last && remaining-- > 0; value++)
        {
            yield return FromUInt32((uint)value);
        }
    }

    private static IEnumerable<string> ParseRange(string token, int remaining)
    {
        var parts = token.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var startIp) ||
            startIp.AddressFamily != AddressFamily.InterNetwork)
        {
            yield break;
        }

        var start = ToUInt32(startIp);
        uint end;
        if (IPAddress.TryParse(parts[1], out var fullEnd) && fullEnd.AddressFamily == AddressFamily.InterNetwork)
        {
            end = ToUInt32(fullEnd);
        }
        else if (byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastOctet))
        {
            end = (start & 0xFFFFFF00u) | lastOctet;
        }
        else
        {
            yield break;
        }

        if (end < start)
        {
            yield break;
        }

        for (var value = start; value <= end && remaining-- > 0; value++)
        {
            yield return FromUInt32(value);
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }

    private static string FromUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes).ToString();
    }

    private const int MaxHardHostLimit = 1024;
}
