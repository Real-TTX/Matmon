using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Matmon.Core.Domain;

/// <summary>
/// A UPS read through a Network UPS Tools server (upsd, TCP 3493). That covers the most common small-office setup: a UPS
/// on the USB port of a NAS (Synology, QNAP, TrueNAS), a Proxmox node, a firewall (pfSense, OPNsense) or Home Assistant,
/// which then serves it on the network. Reading needs no login - upsd decides by the client's address (on a Synology:
/// "Permitted DiskStation devices" in the UPS settings must list the probe). The values become the same channels as the
/// SNMP UPS sensor (<see cref="UpsChannels"/>).
/// </summary>
public sealed class NutUpsSensorExecutor : ISensorExecutor
{
    public const int DefaultPort = 3493;

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "nut-ups",
        DisplayName = "UPS (NUT)",
        Description = "Reads a UPS through a Network UPS Tools server (port 3493) - a UPS on the USB port of a NAS, a Proxmox node, a firewall or Home Assistant. On battery, low battery, charge, runtime, load.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "nut.ups",
                Label = "UPS name",
                Kind = SensorParameterKind.Text,
                Description = "The UPS's name on the NUT server (Synology: ups). Empty = the first UPS the server lists.",
                Placeholder = "ups"
            },
            new SensorParameterDefinition
            {
                Key = "nut.port",
                Label = "Port",
                Kind = SensorParameterKind.Integer,
                Description = "The NUT server's TCP port",
                DefaultValue = "3493",
                Min = 1,
                Max = 65535,
                Step = "1"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (!context.OpenTcpPorts.Contains(DefaultPort))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["nut.port"] = DefaultPort.ToString();
        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "UPS",
                    context.Host,
                    settings,
                    "Port 3493 is open - a Network UPS Tools server shares a UPS here.",
                    80)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "The NUT server's address is missing (the host's address, or a target).");
        }

        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "nut.port", out var configuredPort) && configuredPort is >= 1 and <= 65535
            ? configuredPort
            : DefaultPort;
        MonitoringSettings.TryReadParameter(context.Settings, "nut.ups", out var upsName);
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(10);

        var watch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await using var client = await NutClient.ConnectAsync(context.Target.Trim(), port, timeoutCts.Token);
            var name = upsName?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                var listed = await client.ListUpsAsync(timeoutCts.Token);
                if (listed.Count == 0)
                {
                    return SensorExecutionResult.Critical(watch.Elapsed, "The NUT server shares no UPS.");
                }

                name = listed[0];
            }

            var variables = await client.ListVariablesAsync(name, timeoutCts.Token);
            var reading = UpsChannels.FromNut(variables);
            var channels = UpsChannels.ToChannels(reading);
            if (channels.Count == 0)
            {
                return SensorExecutionResult.Unknown($"The UPS '{name}' reported no values.");
            }

            var defaultChannel = channels.Any(channel => channel.Key == UpsChannels.BatteryCharge) ? UpsChannels.BatteryCharge : channels[0].Key;
            channels = channels.Select(channel => channel with { IsDefault = channel.Key == defaultChannel }).ToList();
            var message = UpsChannels.Describe(reading) + (string.IsNullOrWhiteSpace(reading.Model) ? string.Empty : $" ({reading.Model})");
            var result = SensorExecutionResult.Healthy(
                watch.Elapsed,
                message,
                channels.First(channel => channel.IsDefault).Value,
                defaultChannel,
                channels);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (NutProtocolException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The NUT server at {context.Target}:{port} did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (SocketException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, exception.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => $"Nothing listens on {context.Target}:{port} - is the NUT server switched on and shared on the network?",
                SocketError.HostNotFound or SocketError.NoData => $"The name '{context.Target}' does not resolve.",
                _ => $"The NUT server at {context.Target}:{port} cannot be reached: {exception.Message}"
            });
        }
        catch (IOException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The connection to the NUT server broke off: {exception.Message}");
        }
    }
}

/// <summary>An ERR answer of a NUT server, said in words (the server only sends a code).</summary>
public sealed class NutProtocolException(string message) : Exception(message);

/// <summary>
/// The read-only part of the NUT network protocol (docs: networkupstools.org, "Network protocol information"): plain
/// text lines over TCP, <c>LIST UPS</c> and <c>LIST VAR &lt;ups&gt;</c>, answered with <c>BEGIN LIST ...</c>, one
/// line per item and <c>END LIST ...</c> - or a single <c>ERR &lt;code&gt;</c>.
/// </summary>
public sealed class NutClient : IAsyncDisposable
{
    private const int MaxLines = 2000;

    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private NutClient(TcpClient tcp)
    {
        _tcp = tcp;
        var stream = tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
    }

    public static async Task<NutClient> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken);
            return new NutClient(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> ListUpsAsync(CancellationToken cancellationToken)
    {
        var lines = await ListAsync("LIST UPS", "UPS", null, cancellationToken);
        return lines
            .Select(line => SplitFields(line))
            .Where(fields => fields.Count >= 2 && fields[0] == "UPS")
            .Select(fields => fields[1])
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<string, string>> ListVariablesAsync(string ups, CancellationToken cancellationToken)
    {
        var lines = await ListAsync($"LIST VAR {ups}", $"VAR {ups}", ups, cancellationToken);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fields in lines.Select(line => SplitFields(line)))
        {
            // VAR <ups> <name> "<value>"
            if (fields.Count >= 4 && fields[0] == "VAR")
            {
                variables[fields[2]] = fields[3];
            }
        }

        return variables;
    }

    private async Task<List<string>> ListAsync(string command, string listName, string? ups, CancellationToken cancellationToken)
    {
        await _writer.WriteLineAsync(command.AsMemory(), cancellationToken);
        var first = await ReadLineAsync(cancellationToken);
        ThrowIfError(first, ups);

        var begin = $"BEGIN LIST {listName}";
        if (!string.Equals(first, begin, StringComparison.Ordinal))
        {
            throw new NutProtocolException($"The server on this port does not speak the NUT protocol (it answered '{Trim(first)}').");
        }

        var end = $"END LIST {listName}";
        var lines = new List<string>();
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (string.Equals(line, end, StringComparison.Ordinal))
            {
                return lines;
            }

            ThrowIfError(line, ups);
            if (lines.Count >= MaxLines)
            {
                throw new NutProtocolException("The NUT server sent an endless list.");
            }

            lines.Add(line);
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await _reader.ReadLineAsync(cancellationToken);
        return line ?? throw new IOException("the server closed the connection");
    }

    private static void ThrowIfError(string line, string? ups)
    {
        if (!line.StartsWith("ERR ", StringComparison.Ordinal))
        {
            return;
        }

        var code = line[4..].Trim().Split(' ')[0];
        throw new NutProtocolException(code switch
        {
            "UNKNOWN-UPS" => $"The NUT server has no UPS called '{ups}'. Leave the UPS name empty to use the first one it shares.",
            "ACCESS-DENIED" => "The NUT server refuses this address. Allow the probe's address on the server (Synology: UPS > Permitted DiskStation devices; upsd.conf: LISTEN / ACL).",
            "DATA-STALE" => "The NUT server has no fresh data from the UPS - check the UPS's USB or serial cable and the driver.",
            "DRIVER-NOT-CONNECTED" => "The NUT server's UPS driver is not running - the UPS is not connected to the server.",
            "FEATURE-NOT-CONFIGURED" or "FEATURE-NOT-SUPPORTED" => "The NUT server does not offer this (it may be too old).",
            _ => $"The NUT server answered {code}."
        });
    }

    /// <summary>Splits a protocol line into its fields; a quoted field may contain spaces and <c>\"</c> / <c>\\</c>.</summary>
    public static List<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasField = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (inQuotes)
            {
                if (character == '\\' && index + 1 < line.Length)
                {
                    current.Append(line[++index]);
                }
                else if (character == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character == '"')
            {
                inQuotes = true;
                hasField = true;
            }
            else if (character == ' ')
            {
                if (hasField)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                    hasField = false;
                }
            }
            else
            {
                current.Append(character);
                hasField = true;
            }
        }

        if (hasField)
        {
            fields.Add(current.ToString());
        }

        return fields;
    }

    private static string Trim(string text) => text.Length <= 60 ? text : text[..60] + "…";

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Polite, but nothing waits for the answer.
            await _writer.WriteLineAsync("LOGOUT");
        }
        catch
        {
            // the connection may already be gone
        }

        _writer.Dispose();
        _reader.Dispose();
        _tcp.Dispose();
    }
}
