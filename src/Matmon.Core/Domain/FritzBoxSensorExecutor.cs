using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Matmon.Core.Domain;

/// <summary>
/// An AVM FRITZ!Box - the router of most small offices and homes in Germany - over its own SOAP interfaces on port 49000.
/// The INTERNET CONNECTION is read without a login, through the UPnP status the box shares with its network (connected,
/// connection uptime, line speed, what goes through it right now, the public address). With a FRITZ!Box user it also reads
/// TR-064: model, FRITZ!OS version and uptime, and on a DSL line the synchronised rates, the signal-to-noise margin and
/// the attenuation - the values that tell a flaky line from a flaky provider.
/// <para>The state is the box's own: not connected = critical (the internet is down), whatever thresholds say.</para>
/// </summary>
public sealed class FritzBoxSensorExecutor : ISensorExecutor
{
    public const int DefaultPort = 49000;
    public const string ConnectedChannel = "connected";
    public const string DownloadChannel = "downloadKbps";
    public const string UploadChannel = "uploadKbps";

    private const string IgdConnection = "urn:schemas-upnp-org:service:WANIPConnection:1";
    private const string IgdCommon = "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1";
    private const string Tr064DeviceInfo = "urn:dslforum-org:service:DeviceInfo:1";
    private const string Tr064Dsl = "urn:dslforum-org:service:WANDSLInterfaceConfig:1";

    private readonly Func<NetworkCredential?, HttpMessageHandler> _handlerFactory;

    public FritzBoxSensorExecutor()
        : this(DefaultHandler)
    {
    }

    /// <summary>For tests: the handler every request goes through (the credential is the FRITZ!Box login, if any).</summary>
    public FritzBoxSensorExecutor(Func<NetworkCredential?, HttpMessageHandler> handlerFactory) => _handlerFactory = handlerFactory;

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "fritzbox",
        DisplayName = "FRITZ!Box",
        Description = "An AVM FRITZ!Box: internet connected, connection uptime, line speed and current throughput (no login needed); with a FRITZ!Box user also the model, FRITZ!OS version and, on DSL, sync rates, signal-to-noise margin and attenuation.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "generic.username",
                Label = "FRITZ!Box user (optional)",
                Kind = SensorParameterKind.Text,
                Description = "Only for the model, FRITZ!OS version and DSL line values. A user with the right 'FRITZ!Box settings' (System → FRITZ!Box Users). The connection itself is read without one.",
                Placeholder = "monitor",
                CredentialKind = MonitoringCredentialKind.Generic
            },
            new SensorParameterDefinition
            {
                Key = "generic.password",
                Label = "Password (optional)",
                Kind = SensorParameterKind.Secret,
                Description = "That user's password.",
                CredentialKind = MonitoringCredentialKind.Generic
            },
            new SensorParameterDefinition
            {
                Key = "fritz.port",
                Label = "Port",
                Group = "Connection",
                Kind = SensorParameterKind.Integer,
                Description = "The box's TR-064 / UPnP port.",
                DefaultValue = "49000",
                Min = 1,
                Max = 65535,
                Step = "1"
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
        settings.Parameters["fritz.port"] = DefaultPort.ToString(CultureInfo.InvariantCulture);
        return ValueTask.FromResult(SensorDiscoveryCheckResult.Available(
            new SensorDiscoverySuggestion(
                Definition.Key,
                "Internet (FRITZ!Box)",
                context.Host,
                settings,
                "Port 49000 is open - a FRITZ!Box (TR-064) answers here. The internet connection is read without a login.",
                85)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        var target = context.Target?.Trim();
        if (string.IsNullOrEmpty(target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "The FRITZ!Box's address is missing (the host's address - usually fritz.box or 192.168.178.1).");
        }

        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "fritz.port", out var configured) && configured is >= 1 and <= 65535 ? configured : DefaultPort;
        if (!Uri.TryCreate(target.Contains("://", StringComparison.Ordinal) ? target : $"http://{target}:{port}", UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, $"'{target}' is not an address.");
        }

        MonitoringSettings.TryReadParameter(context.Settings, "generic.username", out var username);
        MonitoringSettings.TryReadParameter(context.Settings, "generic.password", out var password);
        // A box set to "password only" takes TR-064 under the old fixed user name.
        var credential = string.IsNullOrEmpty(password) ? null : new NetworkCredential(string.IsNullOrWhiteSpace(username) ? "dslf-config" : username.Trim(), password);
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(10);
        var watch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var client = new HttpClient(_handlerFactory(credential), disposeHandler: true)
        {
            BaseAddress = new Uri(parsed.GetLeftPart(UriPartial.Authority) + "/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        try
        {
            var status = await CallAsync(client, "igdupnp/control/WANIPConn1", IgdConnection, "GetStatusInfo", timeoutCts.Token);
            if (status is null)
            {
                return SensorExecutionResult.Critical(watch.Elapsed,
                    "The FRITZ!Box does not share its connection status. Switch on Home Network → Network → Network Settings → 'Transmit status information over UPnP'.");
            }

            var address = await CallAsync(client, "igdupnp/control/WANIPConn1", IgdConnection, "GetExternalIPAddress", timeoutCts.Token);
            var link = await CallAsync(client, "igdupnp/control/WANCommonIFC1", IgdCommon, "GetCommonLinkProperties", timeoutCts.Token);
            var traffic = await CallAsync(client, "igdupnp/control/WANCommonIFC1", IgdCommon, "GetAddonInfos", timeoutCts.Token);

            IReadOnlyDictionary<string, string>? device = null;
            IReadOnlyDictionary<string, string>? dsl = null;
            string? loginProblem = null;
            if (credential is not null)
            {
                try
                {
                    device = await CallAsync(client, "upnp/control/deviceinfo", Tr064DeviceInfo, "GetInfo", timeoutCts.Token, authenticated: true);
                    dsl = await CallAsync(client, "upnp/control/wandslifconfig1", Tr064Dsl, "GetInfo", timeoutCts.Token, authenticated: true);
                }
                catch (FritzLoginException exception)
                {
                    loginProblem = exception.Message;
                }
            }

            watch.Stop();
            var result = BuildResult(new FritzReading(status, address, link, traffic, device, dsl, loginProblem), watch.Elapsed);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (FritzLoginException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, char.ToUpperInvariant(exception.Message[0]) + exception.Message[1..] + " - enter a FRITZ!Box user.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The FRITZ!Box at {parsed.Host} did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                ? $"Nothing answers on {parsed.Host}:{parsed.Port} - is this a FRITZ!Box, and is 'Allow access for applications' (TR-064) on under Home Network → Network → Network Settings?"
                : $"The FRITZ!Box at {parsed.Host} cannot be reached: {exception.Message}");
        }
        catch (XmlException)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"{parsed.Host}:{parsed.Port} does not answer like a FRITZ!Box.");
        }
    }

    // ---- the result (pure) -----------------------------------------------------------------------------------------------

    /// <summary>The answers of one read; each is the action's output arguments by name (null = the action is not there).</summary>
    public sealed record FritzReading(
        IReadOnlyDictionary<string, string> Status,
        IReadOnlyDictionary<string, string>? Address,
        IReadOnlyDictionary<string, string>? Link,
        IReadOnlyDictionary<string, string>? Traffic,
        IReadOnlyDictionary<string, string>? Device,
        IReadOnlyDictionary<string, string>? Dsl,
        string? LoginProblem);

    public static SensorExecutionResult BuildResult(FritzReading reading, TimeSpan duration)
    {
        var connectionStatus = Get(reading.Status, "NewConnectionStatus") ?? "Unknown";
        var connected = connectionStatus.Equals("Connected", StringComparison.OrdinalIgnoreCase);
        var channels = new List<SensorChannelValue>
        {
            new() { Key = ConnectedChannel, Label = "Internet connected", Value = connected ? 1 : 0, MeasurementKind = SensorMeasurementKind.Boolean, LogByDefault = false }
        };

        if (connected && Number(reading.Status, "NewUptime") is { } uptime)
        {
            channels.Add(new SensorChannelValue { Key = "connectionUptimeHours", Label = "Connection uptime", Value = Math.Round(uptime / 3600, 1), Unit = "h", LogByDefault = false });
        }

        var downMax = Number(reading.Link, "NewLayer1DownstreamMaxBitRate") is { } downBits && downBits > 0 ? downBits / 1000 : (double?)null;
        var upMax = Number(reading.Link, "NewLayer1UpstreamMaxBitRate") is { } upBits && upBits > 0 ? upBits / 1000 : (double?)null;
        var download = Number(reading.Traffic, "NewByteReceiveRate") is { } received ? received * 8 / 1000 : (double?)null;
        var upload = Number(reading.Traffic, "NewByteSendRate") is { } sent ? sent * 8 / 1000 : (double?)null;
        AddRate(channels, DownloadChannel, "Download", download);
        AddRate(channels, UploadChannel, "Upload", upload);
        if (download is not null && downMax is not null)
        {
            channels.Add(Percent("downloadUtilization", "Download utilisation", download.Value / downMax.Value * 100));
        }

        if (upload is not null && upMax is not null)
        {
            channels.Add(Percent("uploadUtilization", "Upload utilisation", upload.Value / upMax.Value * 100));
        }

        AddRate(channels, "downstreamMaxKbps", "Line speed down", downMax, log: false);
        AddRate(channels, "upstreamMaxKbps", "Line speed up", upMax, log: false);

        if (Number(reading.Device, "NewUpTime") is { } boxUptime)
        {
            channels.Add(new SensorChannelValue { Key = "uptimeHours", Label = "FRITZ!Box uptime", Value = Math.Round(boxUptime / 3600, 1), Unit = "h", LogByDefault = false });
        }

        var dslUp = reading.Dsl is not null && (Get(reading.Dsl, "NewStatus") ?? string.Empty).Equals("Up", StringComparison.OrdinalIgnoreCase);
        if (dslUp)
        {
            AddRate(channels, "dslDownstreamSyncKbps", "DSL sync down", Number(reading.Dsl, "NewDownstreamCurrRate"), log: false);
            AddRate(channels, "dslUpstreamSyncKbps", "DSL sync up", Number(reading.Dsl, "NewUpstreamCurrRate"), log: false);
            AddDecibel(channels, "dslDownstreamSnrDb", "DSL noise margin down", Number(reading.Dsl, "NewDownstreamNoiseMargin"));
            AddDecibel(channels, "dslUpstreamSnrDb", "DSL noise margin up", Number(reading.Dsl, "NewUpstreamNoiseMargin"));
            AddDecibel(channels, "dslDownstreamAttenuationDb", "DSL attenuation down", Number(reading.Dsl, "NewDownstreamAttenuation"));
            AddDecibel(channels, "dslUpstreamAttenuationDb", "DSL attenuation up", Number(reading.Dsl, "NewUpstreamAttenuation"));
        }

        var defaultKey = channels.Any(channel => channel.Key == DownloadChannel) ? DownloadChannel : ConnectedChannel;
        channels = channels.Select(channel => channel with { IsDefault = channel.Key == defaultKey }).ToList();
        var value = channels.First(channel => channel.IsDefault).Value;

        var parts = new List<string>();
        var model = Get(reading.Device, "NewModelName");
        var firmware = Get(reading.Device, "NewSoftwareVersion");
        var name = string.IsNullOrWhiteSpace(model) ? "FRITZ!Box" : model.Trim();
        if (connected)
        {
            var access = Get(reading.Link, "NewWANAccessType") switch
            {
                null or "" => null,
                var type when type.Contains("Fiber", StringComparison.OrdinalIgnoreCase) => "fibre",
                var type when type.Equals("Ethernet", StringComparison.OrdinalIgnoreCase) => "cable / Ethernet",
                var type => type
            };
            parts.Add("internet connected" + (access is null ? string.Empty : $" ({access})"));
            if (downMax is not null && upMax is not null)
            {
                parts.Add($"{Mbit(downMax.Value)} / {Mbit(upMax.Value)} Mbit/s");
            }

            if (Get(reading.Address, "NewExternalIPAddress") is { Length: > 0 } ip && ip != "0.0.0.0")
            {
                parts.Add("public address " + ip);
            }
        }
        else
        {
            var error = Get(reading.Status, "NewLastConnectionError");
            parts.Add($"internet NOT connected ({connectionStatus}{(string.IsNullOrWhiteSpace(error) || error == "ERROR_NONE" ? string.Empty : ", " + error)})");
        }

        if (!string.IsNullOrWhiteSpace(firmware))
        {
            parts.Add("FRITZ!OS " + firmware.Trim());
        }

        if (reading.LoginProblem is not null)
        {
            parts.Add(reading.LoginProblem);
        }

        var message = $"{name}: {string.Join(", ", parts)}";
        return connected
            ? SensorExecutionResult.Healthy(duration, message, value, defaultKey, channels)
            : SensorExecutionResult.Critical(duration, message, value, defaultKey, channels);
    }

    private static void AddRate(List<SensorChannelValue> channels, string key, string label, double? kbps, bool log = true)
    {
        if (kbps is { } value)
        {
            channels.Add(new SensorChannelValue { Key = key, Label = label, Value = Math.Round(value, 1), Unit = "kbit/s", LogByDefault = log });
        }
    }

    /// <summary>TR-064 reports noise margin and attenuation in tenths of a decibel.</summary>
    private static void AddDecibel(List<SensorChannelValue> channels, string key, string label, double? tenths)
    {
        if (tenths is { } value)
        {
            channels.Add(new SensorChannelValue { Key = key, Label = label, Value = Math.Round(value / 10, 1), Unit = "dB" });
        }
    }

    private static SensorChannelValue Percent(string key, string label, double value) =>
        new() { Key = key, Label = label, Value = Math.Round(Math.Clamp(value, 0, 100), 1), Unit = "%", MeasurementKind = SensorMeasurementKind.Percent };

    private static string Mbit(double kbps) => (kbps / 1000).ToString(kbps >= 10_000 ? "0" : "0.#", CultureInfo.InvariantCulture);

    private static string? Get(IReadOnlyDictionary<string, string>? values, string name) =>
        values is not null && values.TryGetValue(name, out var value) ? value : null;

    private static double? Number(IReadOnlyDictionary<string, string>? values, string name) =>
        Get(values, name) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    // ---- SOAP ------------------------------------------------------------------------------------------------------------

    /// <summary>The SOAP request body of an action without arguments - public for the tests.</summary>
    public static string Envelope(string service, string action) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
        + $"<s:Body><u:{action} xmlns:u=\"{service}\"/></s:Body></s:Envelope>";

    /// <summary>The output arguments of a SOAP answer, by name.</summary>
    public static IReadOnlyDictionary<string, string> ParseResponse(string xml)
    {
        var document = XDocument.Parse(xml);
        var body = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "Body");
        var response = body?.Elements().FirstOrDefault();
        return response?.Elements().ToDictionary(element => element.Name.LocalName, element => element.Value.Trim(), StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static async Task<IReadOnlyDictionary<string, string>?> CallAsync(HttpClient client, string path, string service, string action, CancellationToken cancellationToken, bool authenticated = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(Envelope(service, action), Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service}#{action}\"");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new FritzLoginException(authenticated
                ? "the FRITZ!Box refused the user name or password (line values not read)"
                : "the FRITZ!Box wants a login for its status");
        }

        // A box without that service (no DSL on a cable or fibre box, the UPnP status switched off) answers 500 or 404.
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return ParseResponse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static HttpMessageHandler DefaultHandler(NetworkCredential? credential) => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = credential,
        PreAuthenticate = false,
        // The box serves TR-064 on https (49443) with a self-signed certificate; plain http on 49000 needs nothing.
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    };

    private sealed class FritzLoginException(string message) : Exception(message);
}
