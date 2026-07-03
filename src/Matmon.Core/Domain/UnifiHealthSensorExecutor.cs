using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Matmon.Core.Domain;

/// <summary>
/// Reports UniFi health via the API-key (X-API-KEY) interface, in two modes:
/// <list type="bullet">
/// <item><b>Cloud</b> — the UniFi Site Manager API at <c>https://api.ui.com</c>,
/// listing the consoles/hosts on the account and how many are online.</item>
/// <item><b>Local</b> — a controller's Network Integration API at
/// <c>https://&lt;host&gt;/proxy/network/integration/v1</c>, listing the adopted
/// devices of a site and how many are online.</item>
/// </list>
/// Note: UniFi's API does not expose raw disk SMART attributes, so this is a
/// device/host availability sensor, not a SMART sensor.
/// </summary>
public sealed class UnifiHealthSensorExecutor : ISensorExecutor
{
    private const string CloudBaseUrl = "https://api.ui.com";

    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "unifi-health",
        DisplayName = "UniFi Health",
        Description = "UniFi console/device availability via the API key — cloud (api.ui.com) or a local controller's integration API.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "unifi.mode",
                Label = "Mode",
                Kind = SensorParameterKind.ValueList,
                Description = "Cloud uses api.ui.com; Local talks to a controller on your network.",
                DefaultValue = "cloud",
                Options =
                [
                    new SensorParameterOption { Value = "cloud", Label = "Cloud (api.ui.com)" },
                    new SensorParameterOption { Value = "local", Label = "Local controller" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "unifi.apiKey",
                Label = "API key",
                Kind = SensorParameterKind.Secret,
                Description = "Cloud: Site Manager API key (required). Local: a Network app API key (or use username + password below).",
                CredentialKind = MonitoringCredentialKind.Unifi
            },
            new SensorParameterDefinition
            {
                Key = "unifi.username",
                Label = "Username (local login)",
                Kind = SensorParameterKind.Text,
                Description = "Local only: controller admin username, if not using a local API key.",
                CredentialKind = MonitoringCredentialKind.Unifi
            },
            new SensorParameterDefinition
            {
                Key = "unifi.password",
                Label = "Password (local login)",
                Kind = SensorParameterKind.Secret,
                Description = "Local only: controller admin password (used with the username above). 2FA must be off.",
                CredentialKind = MonitoringCredentialKind.Unifi
            },
            new SensorParameterDefinition
            {
                Key = "unifi.baseUrl",
                Label = "Base URL",
                Group = "Connection",
                Kind = SensorParameterKind.Text,
                Description = "Cloud: leave empty for https://api.ui.com. Local: your controller, e.g. https://192.168.1.1 (or use the host target).",
                Placeholder = "https://192.168.1.1"
            },
            new SensorParameterDefinition
            {
                Key = "unifi.site",
                Label = "Site (local)",
                Group = "Connection",
                Kind = SensorParameterKind.Text,
                Description = "Local only: the site id/name to read devices from. Empty = first site.",
                Placeholder = "default"
            },
            new SensorParameterDefinition
            {
                Key = "unifi.verifySsl",
                Label = "Verify SSL (local)",
                Group = "Connection",
                Kind = SensorParameterKind.Boolean,
                Description = "Validate the controller certificate (usually off for a local self-signed UCG).",
                DefaultValue = "false"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var mode = ResolveMode(context.Settings);
        var verifySsl = MonitoringSettings.TryReadParameterBool(context.Settings, "unifi.verifySsl", out var configuredVerify) && configuredVerify;
        MonitoringSettings.TryReadParameter(context.Settings, "unifi.apiKey", out var apiKey);
        MonitoringSettings.TryReadParameter(context.Settings, "unifi.username", out var username);
        MonitoringSettings.TryReadParameter(context.Settings, "unifi.password", out var password);
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(15);

        var watch = Stopwatch.StartNew();
        try
        {
            if (mode == "cloud")
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return SensorExecutionResult.Critical(TimeSpan.Zero, "Cloud mode requires the Site Manager API key");
                }

                using var cloudClient = CreateApiKeyClient(apiKey, verifySsl, timeout);
                return await ExecuteCloudAsync(cloudClient, context, watch, cancellationToken);
            }

            // Local mode: prefer a local API key (integration API), else username + password (controller login).
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                using var localClient = CreateApiKeyClient(apiKey, verifySsl, timeout);
                return await ExecuteLocalAsync(localClient, context, watch, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                return await ExecuteLocalLoginAsync(context, username.Trim(), password, verifySsl, watch, cancellationToken);
            }

            return SensorExecutionResult.Critical(TimeSpan.Zero, "Local mode needs an API key, or a username and password");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "request timed out");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static HttpClient CreateApiKeyClient(string apiKey, bool verifySsl, TimeSpan timeout)
    {
        var client = CreateHttpClient(verifySsl, timeout);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-API-KEY", apiKey.Trim());
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async ValueTask<SensorExecutionResult> ExecuteCloudAsync(
        HttpClient client,
        SensorExecutionContext context,
        Stopwatch watch,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveBaseUri(context, CloudBaseUrl) ?? new Uri(CloudBaseUrl);
        var hosts = await ReadArrayAsync(client, new Uri(baseUri, "v1/hosts"), "UniFi cloud", cancellationToken);
        var (total, online) = CountOnline(hosts);
        watch.Stop();

        return BuildResult(context.Settings, watch, "host", "console", total, online, "UniFi cloud reachable");
    }

    private static async ValueTask<SensorExecutionResult> ExecuteLocalAsync(
        HttpClient client,
        SensorExecutionContext context,
        Stopwatch watch,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveBaseUri(context, null);
        if (baseUri is null)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "local mode needs a controller base URL or host target");
        }

        var integrationRoot = new Uri(baseUri, "proxy/network/integration/v1/");
        var sites = await ReadArrayAsync(client, new Uri(integrationRoot, "sites"), "UniFi controller", cancellationToken);
        var siteId = ResolveSiteId(sites, context.Settings);
        if (string.IsNullOrWhiteSpace(siteId))
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "no UniFi site found for this API key");
        }

        var devices = await ReadArrayAsync(client, new Uri(integrationRoot, $"sites/{Uri.EscapeDataString(siteId)}/devices"), "UniFi controller", cancellationToken);
        var (total, online) = CountOnline(devices);
        watch.Stop();

        return BuildResult(context.Settings, watch, "device", "device", total, online, "UniFi controller reachable");
    }

    private static async ValueTask<SensorExecutionResult> ExecuteLocalLoginAsync(
        SensorExecutionContext context,
        string username,
        string password,
        bool verifySsl,
        Stopwatch watch,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveBaseUri(context, null);
        if (baseUri is null)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "local mode needs a controller base URL or host target");
        }

        using var client = CreateHttpClient(verifySsl, context.Settings.Timeout ?? TimeSpan.FromSeconds(15));
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        // UniFi OS login — the auth cookie is stored by the handler's cookie container
        // and replayed on the device request; the CSRF token is echoed back as a header.
        var loginPayload = JsonSerializer.Serialize(new { username, password });
        using var loginContent = new StringContent(loginPayload, System.Text.Encoding.UTF8, "application/json");
        using var loginResponse = await client.PostAsync(new Uri(baseUri, "api/auth/login"), loginContent, cancellationToken);
        if (!loginResponse.IsSuccessStatusCode)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, $"UniFi login failed ({(int)loginResponse.StatusCode}). Check the username/password and that 2FA is disabled for this account.");
        }

        var csrf = loginResponse.Headers.TryGetValues("x-csrf-token", out var csrfValues) ? csrfValues.FirstOrDefault() : null;
        var site = ResolveSiteName(context.Settings);

        using var deviceRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"proxy/network/api/s/{Uri.EscapeDataString(site)}/stat/device"));
        if (!string.IsNullOrWhiteSpace(csrf))
        {
            deviceRequest.Headers.TryAddWithoutValidation("X-CSRF-Token", csrf);
        }

        using var deviceResponse = await client.SendAsync(deviceRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await deviceResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!deviceResponse.IsSuccessStatusCode)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, $"UniFi device query failed ({(int)deviceResponse.StatusCode}) for site '{site}'");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var devices = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data
            : root;
        var (total, online) = CountOnline(devices);
        watch.Stop();

        return BuildResult(context.Settings, watch, "device", "device", total, online, "UniFi controller reachable");
    }

    private static string ResolveSiteName(MonitoringSettings settings)
    {
        return MonitoringSettings.TryReadParameter(settings, "unifi.site", out var site) && !string.IsNullOrWhiteSpace(site)
            ? site.Trim()
            : "default";
    }

    private static SensorExecutionResult BuildResult(
        MonitoringSettings settings,
        Stopwatch watch,
        string keyPrefix,
        string noun,
        int total,
        int online,
        string reachableMessage)
    {
        var offline = Math.Max(0, total - online);
        var ratio = total == 0 ? 100d : Math.Round((double)online / total * 100d, 1);

        var channels = new List<SensorChannelValue>
        {
            new() { Key = $"{keyPrefix}sOnline", Label = "Online", Value = online, MeasurementKind = SensorMeasurementKind.Count, IsDefault = true },
            new() { Key = $"{keyPrefix}sTotal", Label = "Total", Value = total, MeasurementKind = SensorMeasurementKind.Count },
            new() { Key = $"{keyPrefix}sOffline", Label = "Offline", Value = offline, MeasurementKind = SensorMeasurementKind.Count },
            new() { Key = "onlineRatio", Label = "Online ratio", Value = ratio, Unit = "%", MeasurementKind = SensorMeasurementKind.Percent }
        };

        var state = total == 0
            ? SensorState.Warning
            : offline > 0
                ? SensorState.Warning
                : SensorState.Healthy;

        var message = total == 0
            ? $"{reachableMessage}, but no {noun}s reported"
            : offline > 0
                ? $"{online}/{total} {noun}s online ({offline} offline)"
                : $"{reachableMessage} — {online} {noun}s online";

        var result = state switch
        {
            SensorState.Warning => SensorExecutionResult.Warning(watch.Elapsed, message, online, $"{keyPrefix}sOnline", channels),
            _ => SensorExecutionResult.Healthy(watch.Elapsed, message, online, $"{keyPrefix}sOnline", channels)
        };

        return SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);
    }

    private static async Task<JsonElement> ReadArrayAsync(HttpClient client, Uri uri, string service, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException($"{service} rejected the API key ({(int)response.StatusCode}). Check the key and that it has read access.");
            }

            var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
            throw new InvalidOperationException($"{service} request to '{uri}' failed with {(int)response.StatusCode}: {detail}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.Clone();
        }

        // Most UniFi APIs wrap the payload in { "data": [...] }.
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            return data.Clone();
        }

        // Single object → treat as one item.
        return root.Clone();
    }

    private static (int Total, int Online) CountOnline(JsonElement element)
    {
        var items = element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().ToArray()
            : element.ValueKind == JsonValueKind.Object ? [element] : Array.Empty<JsonElement>();

        var total = 0;
        var online = 0;
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            total++;
            if (IsOnline(item))
            {
                online++;
            }
        }

        return (total, online);
    }

    private static bool IsOnline(JsonElement item)
    {
        // Tolerant across UniFi cloud (hosts) and local (devices) shapes.
        foreach (var key in new[] { "online", "isOnline", "connected", "adopted" })
        {
            if (item.TryGetProperty(key, out var boolProp))
            {
                if (boolProp.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (boolProp.ValueKind == JsonValueKind.False)
                {
                    return false;
                }
            }
        }

        foreach (var key in new[] { "status", "state", "connectionState" })
        {
            if (item.TryGetProperty(key, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String)
                {
                    var value = prop.GetString() ?? string.Empty;
                    if (value.Contains("online", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("connected", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("active", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (value.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("disconnected", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                else if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var numeric))
                {
                    // UniFi devices use state 1 = connected.
                    return numeric == 1;
                }
            }
        }

        // No recognizable signal — assume reachable items are up rather than flag a false outage.
        return true;
    }

    private static string ResolveMode(MonitoringSettings settings)
    {
        if (MonitoringSettings.TryReadParameter(settings, "unifi.mode", out var mode) && !string.IsNullOrWhiteSpace(mode))
        {
            return mode.Trim().ToLowerInvariant() == "local" ? "local" : "cloud";
        }

        return "cloud";
    }

    private static Uri? ResolveBaseUri(SensorExecutionContext context, string? fallback)
    {
        var configured = MonitoringSettings.TryReadParameter(context.Settings, "unifi.baseUrl", out var baseUrl) && !string.IsNullOrWhiteSpace(baseUrl)
            ? baseUrl.Trim()
            : !string.IsNullOrWhiteSpace(context.Target)
                ? context.Target.Trim()
                : fallback;

        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        if (!configured.Contains("://", StringComparison.Ordinal))
        {
            configured = $"https://{configured}";
        }

        var normalized = configured.EndsWith('/') ? configured : configured + "/";
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string ResolveSiteId(JsonElement sites, MonitoringSettings settings)
    {
        var requested = MonitoringSettings.TryReadParameter(settings, "unifi.site", out var site) ? site.Trim() : string.Empty;

        if (sites.ValueKind != JsonValueKind.Array)
        {
            return requested;
        }

        string? firstId = null;
        foreach (var entry in sites.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = entry.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
            var name = entry.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() : null;

            firstId ??= id;

            if (!string.IsNullOrWhiteSpace(requested) &&
                (string.Equals(id, requested, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, requested, StringComparison.OrdinalIgnoreCase)))
            {
                return id ?? requested;
            }
        }

        return !string.IsNullOrWhiteSpace(requested) ? requested : firstId ?? string.Empty;
    }

    private static HttpClient CreateHttpClient(bool verifySsl, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();
        if (!verifySsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            // A finite timeout so a hung controller can't pin a polling worker indefinitely; on expiry
            // HttpClient throws a canceled exception with an internal token, caught as "request timed out".
            Timeout = timeout
        };
    }
}
