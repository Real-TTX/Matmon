using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Matmon.Core.Domain;

public sealed class DnsSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "dns",
        DisplayName = "DNS",
        Description = "Resolve DNS records and verify response time, record count and optional expected address.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "dns.recordName",
                Label = "Record name",
                Kind = SensorParameterKind.Text,
                Description = "DNS name to resolve. Empty uses the sensor target.",
                Placeholder = "example.local"
            },
            new SensorParameterDefinition
            {
                Key = "dns.recordType",
                Label = "Record type",
                Kind = SensorParameterKind.ValueList,
                Description = "DNS record type",
                DefaultValue = "A",
                Options =
                [
                    new SensorParameterOption { Value = "A", Label = "A" },
                    new SensorParameterOption { Value = "AAAA", Label = "AAAA" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "dns.server",
                Label = "DNS server",
                Kind = SensorParameterKind.Text,
                Description = "Optional DNS server. Empty uses the resolver inside the probe container.",
                Placeholder = "10.10.0.1"
            },
            new SensorParameterDefinition
            {
                Key = "dns.port",
                Label = "DNS port",
                Kind = SensorParameterKind.Integer,
                Description = "DNS UDP port",
                DefaultValue = "53",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "dns.expectedAddress",
                Label = "Expected address",
                Kind = SensorParameterKind.Text,
                Description = "Optional IP address that must be returned.",
                Placeholder = "10.10.0.10"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!context.OpenTcpPorts.Contains(53))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["dns.server"] = context.Host;
        settings.Parameters["dns.recordType"] = "A";
        settings.Parameters["dns.port"] = "53";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "DNS Resolver",
                    context.Host,
                    settings,
                    "DNS TCP port 53 is open. UDP DNS should be tested.",
                    72)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var recordName = MonitoringSettings.TryReadParameter(context.Settings, "dns.recordName", out var configuredRecordName) &&
            !string.IsNullOrWhiteSpace(configuredRecordName)
            ? configuredRecordName.Trim()
            : context.Target.Trim();
        if (string.IsNullOrWhiteSpace(recordName))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "record name or target is required");
        }

        var recordType = MonitoringSettings.TryReadParameter(context.Settings, "dns.recordType", out var configuredRecordType)
            ? configuredRecordType.Trim().ToUpperInvariant()
            : "A";
        if (recordType is not ("A" or "AAAA"))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "only A and AAAA records are supported");
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(3);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var watch = Stopwatch.StartNew();
        try
        {
            var records = await ResolveRecordsAsync(recordName, recordType, context.Settings, timeoutCts.Token);
            watch.Stop();

            var expectedMatched = 1d;
            var expectedMessage = string.Empty;
            if (MonitoringSettings.TryReadParameter(context.Settings, "dns.expectedAddress", out var expectedAddress) &&
                !string.IsNullOrWhiteSpace(expectedAddress))
            {
                expectedMatched = records.Any(record => string.Equals(record, expectedAddress.Trim(), StringComparison.OrdinalIgnoreCase))
                    ? 1d
                    : 0d;
                expectedMessage = expectedMatched > 0 ? " expected address matched" : " expected address missing";
            }

            var channels = new[]
            {
                new SensorChannelValue
                {
                    Key = "resolveMs",
                    Label = "Resolve",
                    Value = watch.Elapsed.TotalMilliseconds,
                    Unit = "ms",
                    IsDefault = true
                },
                new SensorChannelValue
                {
                    Key = "recordCount",
                    Label = "Records",
                    Value = records.Count
                },
                new SensorChannelValue
                {
                    Key = "expectedMatched",
                    Label = "Expected matched",
                    Value = expectedMatched
                }
            };

            var result = records.Count == 0 || expectedMatched < 0.5
                ? SensorExecutionResult.Critical(
                    watch.Elapsed,
                    records.Count == 0 ? $"no {recordType} records for {recordName}" : $"{recordName}{expectedMessage}",
                    watch.Elapsed.TotalMilliseconds,
                    "resolveMs",
                    channels)
                : SensorExecutionResult.Healthy(
                    watch.Elapsed,
                    $"{recordName} {recordType} {records.Count} record(s){expectedMessage}",
                    watch.Elapsed.TotalMilliseconds,
                    "resolveMs",
                    channels);

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "dns timeout");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordsAsync(
        string recordName,
        string recordType,
        MonitoringSettings settings,
        CancellationToken cancellationToken)
    {
        if (MonitoringSettings.TryReadParameter(settings, "dns.server", out var dnsServer) &&
            !string.IsNullOrWhiteSpace(dnsServer))
        {
            var port = MonitoringSettings.TryReadParameterInt(settings, "dns.port", out var configuredPort)
                ? configuredPort
                : 53;
            return await QueryServerAsync(recordName, recordType, dnsServer.Trim(), port, cancellationToken);
        }

        var addresses = await Dns.GetHostAddressesAsync(recordName, cancellationToken);
        var family = recordType == "AAAA" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        return addresses
            .Where(address => address.AddressFamily == family)
            .Select(address => address.ToString())
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> QueryServerAsync(
        string recordName,
        string recordType,
        string server,
        int port,
        CancellationToken cancellationToken)
    {
        var serverAddresses = await Dns.GetHostAddressesAsync(server, cancellationToken);
        var serverAddress = serverAddresses.FirstOrDefault(address =>
            address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
        if (serverAddress is null)
        {
            throw new InvalidOperationException($"dns server '{server}' could not be resolved");
        }

        using var udp = new UdpClient(serverAddress.AddressFamily);
        udp.Connect(new IPEndPoint(serverAddress, Math.Clamp(port, 1, 65535)));
        var queryId = RandomNumberGenerator.GetInt32(1, ushort.MaxValue);
        var request = BuildDnsQuery(recordName, recordType, (ushort)queryId);
        await udp.SendAsync(request, cancellationToken);
        var response = await udp.ReceiveAsync(cancellationToken);
        return ParseDnsResponse(response.Buffer, (ushort)queryId, recordType);
    }

    private static byte[] BuildDnsQuery(string recordName, string recordType, ushort queryId)
    {
        using var stream = new MemoryStream();
        WriteUInt16(stream, queryId);
        WriteUInt16(stream, 0x0100);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);

        foreach (var label in recordName.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new InvalidOperationException("invalid DNS label");
            }

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        WriteUInt16(stream, recordType == "AAAA" ? (ushort)28 : (ushort)1);
        WriteUInt16(stream, 1);
        return stream.ToArray();
    }

    private static IReadOnlyList<string> ParseDnsResponse(byte[] response, ushort queryId, string recordType)
    {
        if (response.Length < 12 || ReadUInt16(response, 0) != queryId)
        {
            throw new InvalidOperationException("invalid DNS response");
        }

        var answerCount = ReadUInt16(response, 6);
        var offset = 12;
        SkipName(response, ref offset);
        offset += 4;

        var requestedType = recordType == "AAAA" ? 28 : 1;
        var records = new List<string>();
        for (var i = 0; i < answerCount && offset + 12 <= response.Length; i++)
        {
            SkipName(response, ref offset);
            var type = ReadUInt16(response, offset);
            offset += 2;
            var cls = ReadUInt16(response, offset);
            offset += 2;
            offset += 4;
            var rdLength = ReadUInt16(response, offset);
            offset += 2;

            if (offset + rdLength > response.Length)
            {
                break;
            }

            if (cls == 1 && type == requestedType && rdLength is 4 or 16)
            {
                records.Add(new IPAddress(response.AsSpan(offset, rdLength)).ToString());
            }

            offset += rdLength;
        }

        return records;
    }

    private static void SkipName(byte[] buffer, ref int offset)
    {
        while (offset < buffer.Length)
        {
            var length = buffer[offset++];
            if (length == 0)
            {
                return;
            }

            if ((length & 0xC0) == 0xC0)
            {
                offset++;
                return;
            }

            offset += length;
        }
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
    {
        return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }
}
