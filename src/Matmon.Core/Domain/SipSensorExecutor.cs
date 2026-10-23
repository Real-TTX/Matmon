using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Matmon.Core.Domain;

/// <summary>
/// Is the phone system there? A SIP OPTIONS request - the "ping" of SIP that every PBX, SBC and SIP trunk answers
/// (Asterisk, FreePBX, 3CX, Kamailio, a FRITZ!Box, a provider's registrar) - over UDP or TCP, and how long the answer
/// took. Any final answer means the SIP stack is alive: 2xx is a plain yes, 4xx is a yes that wants a login or
/// refuses the method (normal for a PBX that does not trust strangers), 5xx/6xx is a server that answers but says it
/// cannot serve (a warning). No answer is critical.
/// </summary>
public sealed class SipSensorExecutor : ISensorExecutor
{
    public const int DefaultPort = 5060;
    public const string ResponseTimeChannel = "responseTime";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "sip",
        DisplayName = "SIP (OPTIONS)",
        Description = "Asks a phone system, SBC or SIP trunk with a SIP OPTIONS request (UDP or TCP) whether it is there: response time and status. A login request (401/407) still means it is alive.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "sip.transport",
                Label = "Transport",
                Kind = SensorParameterKind.ValueList,
                Description = "UDP is what most phones and PBXs use; TCP for a server that only listens there.",
                DefaultValue = "udp",
                Options =
                [
                    new SensorParameterOption { Value = "udp", Label = "UDP" },
                    new SensorParameterOption { Value = "tcp", Label = "TCP" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "sip.port",
                Label = "Port",
                Kind = SensorParameterKind.Integer,
                Description = "The SIP port.",
                DefaultValue = "5060",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "sip.user",
                Label = "Extension or user (optional)",
                Group = "Connection",
                Kind = SensorParameterKind.Text,
                Description = "Ask for a particular extension or account (sip:<user>@host) instead of the server itself.",
                Placeholder = "100"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (!context.OpenTcpPorts.Contains(DefaultPort))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["sip.transport"] = "tcp";
        settings.Parameters["sip.port"] = DefaultPort.ToString(CultureInfo.InvariantCulture);
        return ValueTask.FromResult(SensorDiscoveryCheckResult.Available(
            new SensorDiscoverySuggestion(Definition.Key, "SIP", context.Host, settings, "Port 5060 is open - a SIP server (PBX, phone, trunk) listens here.", 78)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        var host = context.Target?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "The SIP server's address is missing (the host's address, or a target).");
        }

        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "sip.port", out var configured) && configured is >= 1 and <= 65535 ? configured : DefaultPort;
        var tcp = MonitoringSettings.TryReadParameter(context.Settings, "sip.transport", out var transport) && transport.Trim().Equals("tcp", StringComparison.OrdinalIgnoreCase);
        MonitoringSettings.TryReadParameter(context.Settings, "sip.user", out var user);
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(5);
        var label = $"{(tcp ? "TCP" : "UDP")} {host}:{port}";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token);
            var address = addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
                ?? throw new SocketException((int)SocketError.HostNotFound);
            watch.Restart();
            var answer = tcp
                ? await AskTcpAsync(address, port, host, user, timeoutCts.Token)
                : await AskUdpAsync(address, port, host, user, timeoutCts.Token);
            watch.Stop();
            return BuildResult(answer, watch.Elapsed, label);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"No SIP answer from {label} within {timeout.TotalSeconds:0} s.");
        }
        catch (SocketException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, exception.SocketErrorCode switch
            {
                SocketError.ConnectionRefused or SocketError.ConnectionReset => $"Nothing listens for SIP on {label}.",
                SocketError.HostNotFound or SocketError.NoData => $"The name '{host}' does not resolve.",
                _ => $"{label} cannot be reached: {exception.Message}"
            });
        }
        catch (FormatException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"{label} answered, but not with SIP: {exception.Message}");
        }
    }

    // ---- the result (pure) -----------------------------------------------------------------------------------------------

    /// <summary>A final SIP answer: the status, its reason phrase and the product the server named (Server / User-Agent).</summary>
    public sealed record SipAnswer(int Status, string Reason, string? Product);

    public static SensorExecutionResult BuildResult(SipAnswer answer, TimeSpan duration, string label)
    {
        var milliseconds = Math.Round(duration.TotalMilliseconds, 1);
        IReadOnlyList<SensorChannelValue> channels =
        [
            new SensorChannelValue { Key = ResponseTimeChannel, Label = "Response time", Value = milliseconds, Unit = "ms", MeasurementKind = SensorMeasurementKind.Duration, IsDefault = true },
            new SensorChannelValue { Key = "statusCode", Label = "SIP status", Value = answer.Status, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false }
        ];
        var product = string.IsNullOrWhiteSpace(answer.Product) ? string.Empty : $" ({answer.Product.Trim()})";
        var verdict = answer.Status switch
        {
            >= 200 and < 300 => "answers",
            401 or 407 => "answers and wants a login",
            >= 400 and < 500 => "answers",
            _ => "answers but cannot serve"
        };
        var message = $"{label} {verdict}: {answer.Status} {answer.Reason}{product}";
        return answer.Status >= 500
            ? SensorExecutionResult.Warning(duration, message, milliseconds, ResponseTimeChannel, channels)
            : SensorExecutionResult.Healthy(duration, message, milliseconds, ResponseTimeChannel, channels);
    }

    // ---- SIP -------------------------------------------------------------------------------------------------------------

    /// <summary>The OPTIONS request (RFC 3261 §11). The branch carries the RFC 3261 magic cookie.</summary>
    public static string BuildOptions(string host, int port, string? user, string transport, EndPoint local, string callId, string tag, string branch)
    {
        var target = port == DefaultPort ? host : $"{host}:{port}";
        if (target.Contains(':') && IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            target = port == DefaultPort ? $"[{host}]" : $"[{host}]:{port}";
        }

        var uri = string.IsNullOrWhiteSpace(user) ? $"sip:{target}" : $"sip:{user.Trim()}@{target}";
        var via = local is IPEndPoint endpoint ? $"{(endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{endpoint.Address}]" : endpoint.Address.ToString())}:{endpoint.Port}" : "0.0.0.0";
        return $"OPTIONS {uri} SIP/2.0\r\n"
            + $"Via: SIP/2.0/{transport} {via};branch={branch};rport\r\n"
            + "Max-Forwards: 70\r\n"
            + $"From: \"Matmon\" <sip:matmon@{via.Split(':')[0].Trim('[', ']')}>;tag={tag}\r\n"
            + $"To: <{uri}>\r\n"
            + $"Call-ID: {callId}\r\n"
            + "CSeq: 1 OPTIONS\r\n"
            + $"Contact: <sip:matmon@{via}>\r\n"
            + "Accept: application/sdp\r\n"
            + "User-Agent: Matmon\r\n"
            + "Content-Length: 0\r\n\r\n";
    }

    /// <summary>Reads a SIP response: status line, Call-ID and the Server / User-Agent header. Throws FormatException
    /// when it is not a SIP response at all.</summary>
    public static (SipAnswer Answer, string? CallId) ParseResponse(string text)
    {
        var lines = text.Split("\r\n");
        var first = lines[0].Split(' ', 3);
        if (first.Length < 2 || !first[0].StartsWith("SIP/2.0", StringComparison.OrdinalIgnoreCase) || !int.TryParse(first[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
        {
            throw new FormatException(lines[0].Length > 60 ? lines[0][..60] + "…" : lines[0]);
        }

        string? Header(params string[] names) => lines.Skip(1).TakeWhile(line => line.Length > 0)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && names.Any(name => parts[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(parts => parts[1].Trim())
            .FirstOrDefault();

        return (new SipAnswer(status, first.Length > 2 ? first[2].Trim() : string.Empty, Header("Server", "User-Agent")), Header("Call-ID", "i"));
    }

    private static async Task<SipAnswer> AskUdpAsync(IPAddress address, int port, string host, string? user, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(address.AddressFamily);
        udp.Connect(address, port);
        var callId = NewToken() + "@matmon";
        var request = Encoding.ASCII.GetBytes(BuildOptions(host, port, user, "UDP", udp.Client.LocalEndPoint!, callId, NewToken(), "z9hG4bK" + NewToken()));
        await udp.SendAsync(request, cancellationToken);
        while (true)
        {
            var datagram = await udp.ReceiveAsync(cancellationToken);
            var (answer, answeredCallId) = ParseResponse(Encoding.UTF8.GetString(datagram.Buffer));
            if (answeredCallId is not null && answeredCallId != callId)
            {
                continue; // a late answer to someone else's request
            }

            if (answer.Status >= 200)
            {
                return answer;
            }
        }
    }

    private static async Task<SipAnswer> AskTcpAsync(IPAddress address, int port, string host, string? user, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient(address.AddressFamily);
        await tcp.ConnectAsync(address, port, cancellationToken);
        await using var stream = tcp.GetStream();
        var callId = NewToken() + "@matmon";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(BuildOptions(host, port, user, "TCP", tcp.Client.LocalEndPoint!, callId, NewToken(), "z9hG4bK" + NewToken())), cancellationToken);
        var buffer = new StringBuilder();
        var chunk = new byte[4096];
        while (true)
        {
            // One message = headers up to the blank line, then Content-Length bytes (an OPTIONS answer may carry SDP).
            var end = buffer.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            while (end < 0)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    throw new SocketException((int)SocketError.ConnectionReset);
                }

                buffer.Append(Encoding.UTF8.GetString(chunk, 0, read));
                if (buffer.Length > 64 * 1024)
                {
                    throw new FormatException("an endless header");
                }

                end = buffer.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }

            var head = buffer.ToString(0, end);
            var (answer, _) = ParseResponse(head);
            var length = head.Split("\r\n").Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2 && (parts[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || parts[0].Trim() == "l"))
                .Select(parts => int.TryParse(parts[1].Trim(), out var value) ? value : 0).FirstOrDefault();
            if (answer.Status >= 200)
            {
                return answer;
            }

            var consumed = Math.Min(buffer.Length, end + 4 + length);
            buffer.Remove(0, consumed);
        }
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}
