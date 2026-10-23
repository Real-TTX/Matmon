using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Matmon.Core.Domain;

/// <summary>
/// An OPNsense firewall through its REST API (an API key + secret, sent as HTTP Basic): every gateway's state, latency
/// and packet loss as dpinger measures them (the WAN line seen from the firewall itself), pending firmware updates from
/// the last update check, memory and root disk. The field names follow OPNsense's own sources
/// (Routes/GatewayController, scripts/routes/gateway_status.php, Core/FirmwareController, Diagnostics/SystemController).
/// <para>A gateway that is down (or forced down) is critical; one with latency or packet loss over its own dpinger
/// limits is a warning. Only the gateway list is required - the API user may lack the rights for the rest, and then
/// those channels are simply missing.</para>
/// </summary>
public sealed class OpnsenseSensorExecutor : ISensorExecutor
{
    public const string GatewaysDownChannel = "gatewaysDown";
    public const string UpdatesChannel = "updatesAvailable";

    private readonly Func<bool, HttpMessageHandler> _handlerFactory;

    public OpnsenseSensorExecutor()
        : this(DefaultHandler)
    {
    }

    /// <summary>For tests: the handler every request goes through (the flag is "verify the certificate").</summary>
    public OpnsenseSensorExecutor(Func<bool, HttpMessageHandler> handlerFactory) => _handlerFactory = handlerFactory;

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "opnsense",
        DisplayName = "OPNsense",
        Description = "An OPNsense firewall via its API: gateway state, latency and packet loss (the WAN line as the firewall sees it), pending firmware updates, memory and disk.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "generic.username",
                Label = "API key",
                Kind = SensorParameterKind.Text,
                Description = "System → Access → Users → (a user) → API keys. The user needs the privileges 'Status: Gateways' (and, for the rest, 'System: Firmware' and 'Diagnostics: System Activity').",
                Required = true,
                CredentialKind = MonitoringCredentialKind.Generic
            },
            new SensorParameterDefinition
            {
                Key = "generic.password",
                Label = "API secret",
                Kind = SensorParameterKind.Secret,
                Description = "The secret downloaded together with the key.",
                Required = true,
                CredentialKind = MonitoringCredentialKind.Generic
            },
            new SensorParameterDefinition
            {
                Key = "opnsense.port",
                Label = "Port",
                Group = "Connection",
                Kind = SensorParameterKind.Integer,
                Description = "The web interface's HTTPS port. A target with its own scheme (https://fw:8443) wins.",
                DefaultValue = "443",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "opnsense.verifySsl",
                Label = "Verify SSL",
                Group = "Connection",
                Kind = SensorParameterKind.Boolean,
                Description = "Validate the firewall's certificate (it ships a self-signed one).",
                DefaultValue = "false"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        var target = context.Target?.Trim();
        if (string.IsNullOrEmpty(target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "The firewall's address is missing (the host's address, or a target).");
        }

        MonitoringSettings.TryReadParameter(context.Settings, "generic.username", out var key);
        MonitoringSettings.TryReadParameter(context.Settings, "generic.password", out var secret);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "An API key and its secret are needed (System → Access → Users → API keys).");
        }

        if (!target.Contains("://", StringComparison.Ordinal))
        {
            var port = MonitoringSettings.TryReadParameterInt(context.Settings, "opnsense.port", out var configured) && configured is >= 1 and <= 65535 ? configured : 443;
            target = port == 443 ? $"https://{target}" : $"https://{target}:{port}";
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, $"'{context.Target}' is not an address.");
        }

        var verifySsl = MonitoringSettings.TryReadParameterBool(context.Settings, "opnsense.verifySsl", out var verify) && verify;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(15);
        var watch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var client = new HttpClient(_handlerFactory(verifySsl), disposeHandler: true)
        {
            BaseAddress = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/api/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(key.Trim() + ":" + secret.Trim())));
        try
        {
            var gateways = await GetAsync(client, "routes/gateway/status", required: true, timeoutCts.Token);
            var firmware = await GetAsync(client, "core/firmware/status", required: false, timeoutCts.Token);
            var resources = await GetAsync(client, "diagnostics/system/systemResources", required: false, timeoutCts.Token);
            var disk = await GetAsync(client, "diagnostics/system/systemDisk", required: false, timeoutCts.Token);
            watch.Stop();
            var reading = Parse(gateways, firmware, resources, disk);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, BuildResult(reading, uri.Host, watch.Elapsed));
        }
        catch (OpnsenseException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The firewall at {uri.Host} did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The firewall at {uri.Host} cannot be reached: {exception.Message}");
        }
        catch (JsonException)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"{uri.Host} does not answer like an OPNsense API.");
        }
    }

    // ---- parsing + result (pure) -----------------------------------------------------------------------------------------

    /// <summary>One gateway as dpinger sees it. Delay/loss are null where OPNsense reports "~" (monitoring off).</summary>
    public sealed record OpnsenseGateway(string Name, string Status, string? StatusText, double? DelayMs, double? LossPercent);

    public sealed record OpnsenseReading(
        IReadOnlyList<OpnsenseGateway> Gateways,
        string? FirmwareStatus,
        string? FirmwareMessage,
        int? Updates,
        string? Version,
        double? MemoryUsedPercent,
        double? DiskUsedPercent);

    public static OpnsenseReading Parse(JsonElement gateways, JsonElement firmware, JsonElement resources, JsonElement disk)
    {
        var list = new List<OpnsenseGateway>();
        if (gateways.ValueKind == JsonValueKind.Object && gateways.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
            {
                list.Add(new OpnsenseGateway(
                    Text(item, "name") ?? "gateway",
                    (Text(item, "status") ?? "none").ToLowerInvariant(),
                    Text(item, "status_translated"),
                    LeadingNumber(Text(item, "delay")),
                    LeadingNumber(Text(item, "loss"))));
            }
        }

        string? firmwareStatus = null;
        string? firmwareMessage = null;
        int? updates = null;
        string? version = null;
        if (firmware.ValueKind == JsonValueKind.Object)
        {
            firmwareStatus = Text(firmware, "status");
            firmwareMessage = Text(firmware, "status_msg");
            version = firmware.TryGetProperty("product", out var product) ? Text(product, "product_version") : null;
            version ??= Text(firmware, "product_version");
            updates = firmwareStatus switch
            {
                "update" => Count(firmware, "all_packages") ?? 1,
                "upgrade" => Count(firmware, "all_sets") ?? 1,
                "none" => 0,
                _ => null
            };
        }

        double? memory = null;
        if (resources.ValueKind == JsonValueKind.Object && resources.TryGetProperty("memory", out var mem)
            && Number(mem, "total") is { } total && total > 0 && Number(mem, "used") is { } used)
        {
            memory = Math.Round(used / total * 100, 1);
        }

        double? root = null;
        if (disk.ValueKind == JsonValueKind.Object && disk.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
        {
            var rootDevice = devices.EnumerateArray().FirstOrDefault(device => Text(device, "mountpoint") == "/");
            if (rootDevice.ValueKind == JsonValueKind.Object)
            {
                root = Number(rootDevice, "used_pct") ?? LeadingNumber(Text(rootDevice, "used_pct"));
            }
        }

        return new OpnsenseReading(list, firmwareStatus, firmwareMessage, updates, version, memory, root);
    }

    public static SensorExecutionResult BuildResult(OpnsenseReading reading, string host, TimeSpan duration)
    {
        if (reading.Gateways.Count == 0)
        {
            return SensorExecutionResult.Unknown($"{host} reports no gateway - is gateway monitoring set up (System → Gateways)?");
        }

        static bool IsDown(OpnsenseGateway gateway) => gateway.Status is "down" or "force_down";
        static bool IsDegraded(OpnsenseGateway gateway) => gateway.Status is "delay" or "loss" or "delay+loss";

        var channels = new List<SensorChannelValue>
        {
            new() { Key = GatewaysDownChannel, Label = "Gateways down", Value = reading.Gateways.Count(IsDown), MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false }
        };
        var used = new HashSet<string>(StringComparer.Ordinal) { GatewaysDownChannel };
        string? firstDelay = null;
        foreach (var gateway in reading.Gateways)
        {
            var key = "gw." + RedfishSensorExecutor.Slug(gateway.Name);
            if (!used.Add(key))
            {
                continue;
            }

            channels.Add(new SensorChannelValue
            {
                Key = key + ".up", Label = $"{gateway.Name} up", Value = IsDown(gateway) ? 0 : 1, MeasurementKind = SensorMeasurementKind.Boolean, LogByDefault = false,
                State = IsDown(gateway) ? SensorState.Critical : IsDegraded(gateway) ? SensorState.Warning : null
            });
            if (gateway.DelayMs is { } delay)
            {
                channels.Add(new SensorChannelValue { Key = key + ".delay", Label = $"{gateway.Name} latency", Value = delay, Unit = "ms", MeasurementKind = SensorMeasurementKind.Duration });
                firstDelay ??= key + ".delay";
            }

            if (gateway.LossPercent is { } loss)
            {
                channels.Add(new SensorChannelValue { Key = key + ".loss", Label = $"{gateway.Name} packet loss", Value = loss, Unit = "%", MeasurementKind = SensorMeasurementKind.Percent });
            }
        }

        if (reading.Updates is { } updates)
        {
            channels.Add(new SensorChannelValue { Key = UpdatesChannel, Label = "Firmware updates", Value = updates, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false });
        }

        if (reading.MemoryUsedPercent is { } memory)
        {
            channels.Add(new SensorChannelValue { Key = "memoryUsedPercent", Label = "Memory used", Value = memory, Unit = "%", MeasurementKind = SensorMeasurementKind.Percent });
        }

        if (reading.DiskUsedPercent is { } disk)
        {
            channels.Add(new SensorChannelValue { Key = "diskUsedPercent", Label = "Root disk used", Value = disk, Unit = "%", MeasurementKind = SensorMeasurementKind.Percent });
        }

        var defaultKey = firstDelay ?? GatewaysDownChannel;
        channels = channels.Select(channel => channel with { IsDefault = channel.Key == defaultKey }).ToList();
        var value = channels.First(channel => channel.IsDefault).Value;

        var builder = new StringBuilder("OPNsense");
        if (!string.IsNullOrWhiteSpace(reading.Version))
        {
            builder.Append(' ').Append(reading.Version.Trim());
        }

        builder.Append(": ").Append(string.Join(", ", reading.Gateways.Select(gateway =>
            $"{gateway.Name} {(gateway.StatusText ?? gateway.Status).ToLowerInvariant()}" + (gateway.DelayMs is { } delay ? $" {delay.ToString("0.#", CultureInfo.InvariantCulture)} ms" : string.Empty)
            + (gateway.LossPercent is > 0 ? $" {gateway.LossPercent.Value.ToString("0.#", CultureInfo.InvariantCulture)} % loss" : string.Empty))));
        if (reading.Updates is > 0)
        {
            builder.Append($"; {reading.Updates} firmware update{(reading.Updates == 1 ? string.Empty : "s")} pending");
        }
        else if (reading.FirmwareStatus == "error" && !string.IsNullOrWhiteSpace(reading.FirmwareMessage))
        {
            builder.Append("; update check: ").Append(reading.FirmwareMessage.Trim());
        }

        var message = builder.ToString();
        if (reading.Gateways.Any(IsDown))
        {
            return SensorExecutionResult.Critical(duration, message, value, defaultKey, channels);
        }

        return reading.Gateways.Any(IsDegraded)
            ? SensorExecutionResult.Warning(duration, message, value, defaultKey, channels)
            : SensorExecutionResult.Healthy(duration, message, value, defaultKey, channels);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private static async Task<JsonElement> GetAsync(HttpClient client, string path, bool required, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new OpnsenseException("The firewall refused the API key and secret.");
        }

        if (!response.IsSuccessStatusCode)
        {
            if (!required)
            {
                return default;
            }

            throw new OpnsenseException(response.StatusCode == HttpStatusCode.Forbidden
                ? "The API user may not read the gateways - give it the privilege 'Status: Gateways'."
                : $"The firewall answered {(int)response.StatusCode} for /api/{path}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private static HttpMessageHandler DefaultHandler(bool verifySsl)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        if (!verifySsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    /// <summary>"12.3 ms" / "0.0 %" (dpinger's formats) → the number; "~" (not monitored) → null.</summary>
    private static double? LeadingNumber(string? text) =>
        text is not null && Regex.Match(text, @"^\s*(-?\d+(\.\d+)?)") is { Success: true } match
            ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;

    private static int? Count(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Count(),
            JsonValueKind.Array => value.GetArrayLength(),
            _ => null
        } : null;

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>A number, or a string holding one (OPNsense passes sysctl values through as strings).</summary>
    private static double? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetDouble(out var number) => number,
                JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            }
            : null;

    private sealed class OpnsenseException(string message) : Exception(message);
}
