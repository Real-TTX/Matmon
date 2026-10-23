using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Matmon.Core.Domain;

/// <summary>
/// A server's hardware through its management controller's Redfish API (DMTF; iDRAC 8/9, iLO 4/5/6, Lenovo XClarity,
/// Supermicro, Fujitsu iRMC, HPE/Dell/Lenovo blades …). Reads the systems (health rollup, power state, model) and every
/// chassis' Thermal and Power resources (temperatures, fans, power supplies, power draw). The STATE is the controller's
/// own verdict - its health rollup knows the vendor's limits for every sensor, which a generic threshold never could -
/// and the readings become channels, so a temperature can still get a threshold of its own.
/// <para>Auth: HTTP Basic first (every controller above takes it); a controller that refuses Basic gets a Redfish
/// session, which is DELETED again after the read - controllers have a handful of session slots, and a monitor that
/// left one behind per poll would lock the admins out.</para>
/// </summary>
public sealed class RedfishSensorExecutor : ISensorExecutor
{
    public const string HealthChannel = "health";
    public const string PowerOnChannel = "powerOn";
    public const string TemperatureMaxChannel = "temperatureMax";
    public const string PowerWattsChannel = "powerWatts";
    public const string FansFailedChannel = "fansFailed";
    public const string PowerSuppliesFailedChannel = "psuFailed";

    private const int MaxChassis = 8;
    private const int MaxMembers = 16;
    private readonly Func<bool, HttpMessageHandler> _handlerFactory;

    public RedfishSensorExecutor()
        : this(DefaultHandler)
    {
    }

    /// <summary>For tests: the handler every request goes through (the flag is "verify the certificate").</summary>
    public RedfishSensorExecutor(Func<bool, HttpMessageHandler> handlerFactory) => _handlerFactory = handlerFactory;

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "redfish",
        DisplayName = "Server hardware (Redfish)",
        Description = "A server's management controller (iDRAC, iLO, XClarity, Supermicro, iRMC …) over Redfish: the controller's health verdict, power state, temperatures, fans, power supplies and power draw.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "generic.username",
                Label = "Username",
                Kind = SensorParameterKind.Text,
                Description = "A user of the management controller - read-only is enough (iDRAC: Read Only, iLO: Login privilege).",
                Required = true,
                Placeholder = "monitor",
                CredentialKind = MonitoringCredentialKind.Generic
            },
            new SensorParameterDefinition
            {
                Key = "generic.password",
                Label = "Password",
                Kind = SensorParameterKind.Secret,
                Description = "The controller user's password.",
                Required = true,
                CredentialKind = MonitoringCredentialKind.Generic
            },
            new SensorParameterDefinition
            {
                Key = "redfish.port",
                Label = "Port",
                Group = "Connection",
                Kind = SensorParameterKind.Integer,
                Description = "The controller's HTTPS port. A target with its own scheme (https://bmc:8443) wins.",
                DefaultValue = "443",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "redfish.verifySsl",
                Label = "Verify SSL",
                Group = "Connection",
                Kind = SensorParameterKind.Boolean,
                Description = "Validate the controller's certificate (most ship a self-signed one).",
                DefaultValue = "false"
            },
            new SensorParameterDefinition
            {
                Key = "redfish.system",
                Label = "System",
                Group = "Connection",
                Kind = SensorParameterKind.Text,
                Description = "Only on a controller that manages several systems (a blade chassis): the system's Id or name. Empty = all of them.",
                Placeholder = "System.Embedded.1"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ResolveBaseUri(context);
        if (baseUri is null)
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "The management controller's address is missing (the host's address, or a target).");
        }

        MonitoringSettings.TryReadParameter(context.Settings, "generic.username", out var username);
        MonitoringSettings.TryReadParameter(context.Settings, "generic.password", out var password);
        MonitoringSettings.TryReadParameter(context.Settings, "redfish.system", out var systemFilter);
        var verifySsl = MonitoringSettings.TryReadParameterBool(context.Settings, "redfish.verifySsl", out var verify) && verify;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(20);

        var watch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var client = new HttpClient(_handlerFactory(verifySsl), disposeHandler: true) { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("OData-Version", "4.0");
        var session = new RedfishSession(client, username?.Trim() ?? string.Empty, password ?? string.Empty);
        try
        {
            var reading = await ReadAsync(session, systemFilter?.Trim(), timeoutCts.Token);
            watch.Stop();
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, BuildResult(reading, watch.Elapsed));
        }
        catch (RedfishException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The management controller at {baseUri.Host} did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The management controller at {baseUri.Host} cannot be reached: {exception.Message}");
        }
        catch (JsonException)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"{baseUri.Host} does not answer like a Redfish service (no JSON).");
        }
        finally
        {
            await session.LogoutAsync();
        }
    }

    // ---- reading ---------------------------------------------------------------------------------------------------------

    /// <summary>What one read of a controller found.</summary>
    public sealed record RedfishReading(
        IReadOnlyList<RedfishSystem> Systems,
        IReadOnlyList<RedfishComponent> Temperatures,
        IReadOnlyList<RedfishComponent> Fans,
        IReadOnlyList<RedfishComponent> PowerSupplies,
        double? PowerWatts,
        IReadOnlyList<string> ChassisHealth);

    public sealed record RedfishSystem(string Name, string? Model, string? Health, bool? PoweredOn);

    /// <summary>A temperature, a fan or a power supply: its name, reading (with unit) and the controller's health for it.</summary>
    public sealed record RedfishComponent(string Name, double? Reading, string? Unit, string? Health, bool Present);

    private static async Task<RedfishReading> ReadAsync(RedfishSession session, string? systemFilter, CancellationToken cancellationToken)
    {
        var root = await session.GetAsync("/redfish/v1/", cancellationToken)
            ?? throw new RedfishException("No Redfish service at /redfish/v1/ - is this a management controller (iDRAC, iLO, XClarity …), and is Redfish switched on?");

        var systems = new List<RedfishSystem>();
        foreach (var path in await MembersAsync(session, Link(root, "Systems"), cancellationToken))
        {
            if (await session.GetAsync(path, cancellationToken) is not { } system || !MatchesFilter(system, path, systemFilter))
            {
                continue;
            }

            var status = Object(system, "Status");
            systems.Add(new RedfishSystem(
                Text(system, "Name") ?? Text(system, "Id") ?? path,
                JoinModel(Text(system, "Manufacturer"), Text(system, "Model")),
                Text(status, "HealthRollup") ?? Text(status, "Health"),
                Text(system, "PowerState") is { } power ? power.Equals("On", StringComparison.OrdinalIgnoreCase) || power.Equals("PoweringOff", StringComparison.OrdinalIgnoreCase) : null));
        }

        if (systems.Count == 0 && !string.IsNullOrEmpty(systemFilter))
        {
            throw new RedfishException($"The controller manages no system called '{systemFilter}'.");
        }

        var temperatures = new List<RedfishComponent>();
        var fans = new List<RedfishComponent>();
        var supplies = new List<RedfishComponent>();
        var chassisHealth = new List<string>();
        double? watts = null;
        foreach (var path in (await MembersAsync(session, Link(root, "Chassis"), cancellationToken)).Take(MaxChassis))
        {
            if (await session.GetAsync(path, cancellationToken) is not { } chassis)
            {
                continue;
            }

            if (Text(Object(chassis, "Status"), "Health") is { } health)
            {
                chassisHealth.Add(health);
            }

            if (Link(chassis, "Thermal") is { } thermalPath && await session.GetAsync(thermalPath, cancellationToken) is { } thermal)
            {
                temperatures.AddRange(Components(thermal, "Temperatures", "ReadingCelsius", _ => "°C"));
                fans.AddRange(Components(thermal, "Fans", "Reading", entry => Text(entry, "ReadingUnits") is { } unit && unit.Equals("Percent", StringComparison.OrdinalIgnoreCase) ? "%" : "rpm"));
            }
            else if (Link(chassis, "ThermalSubsystem") is { } subsystemPath && await session.GetAsync(subsystemPath, cancellationToken) is { } subsystem)
            {
                // The newer model (Redfish 2020.4+; some controllers dropped Thermal): readings in ThermalMetrics, one
                // resource per fan.
                if (Link(subsystem, "ThermalMetrics") is { } metricsPath && await session.GetAsync(metricsPath, cancellationToken) is { } metrics)
                {
                    temperatures.AddRange(Array(metrics, "TemperatureReadingsCelsius").Select(entry => new RedfishComponent(
                        Text(entry, "DeviceName") ?? LastSegment(Text(entry, "DataSourceUri")) ?? "Temperature", Number(entry, "Reading"), "°C", null, true)));
                }

                foreach (var fanPath in (await MembersAsync(session, Link(subsystem, "Fans"), cancellationToken)).Take(MaxMembers))
                {
                    if (await session.GetAsync(fanPath, cancellationToken) is { } fan)
                    {
                        var speed = Object(fan, "SpeedPercent");
                        fans.Add(Component(fan, Number(speed, "Reading") ?? Number(speed, "SpeedRPM"), Number(speed, "Reading") is not null ? "%" : "rpm"));
                    }
                }
            }

            if (Link(chassis, "Power") is { } powerPath && await session.GetAsync(powerPath, cancellationToken) is { } power)
            {
                supplies.AddRange(Components(power, "PowerSupplies", "LastPowerOutputWatts", _ => "W"));
                if (Array(power, "PowerControl").Select(control => Number(control, "PowerConsumedWatts")).FirstOrDefault(value => value is not null) is { } consumed)
                {
                    watts = (watts ?? 0) + consumed;
                }
            }
            else if (Link(chassis, "PowerSubsystem") is { } powerSubsystemPath && await session.GetAsync(powerSubsystemPath, cancellationToken) is { } powerSubsystem)
            {
                foreach (var supplyPath in (await MembersAsync(session, Link(powerSubsystem, "PowerSupplies"), cancellationToken)).Take(MaxMembers))
                {
                    if (await session.GetAsync(supplyPath, cancellationToken) is { } supply)
                    {
                        supplies.Add(Component(supply, null, "W"));
                    }
                }

                if (Link(chassis, "EnvironmentMetrics") is { } environmentPath && await session.GetAsync(environmentPath, cancellationToken) is { } environment
                    && Number(Object(environment, "PowerWatts"), "Reading") is { } reading)
                {
                    watts = (watts ?? 0) + reading;
                }
            }
        }

        return new RedfishReading(systems, temperatures, fans, supplies, watts, chassisHealth);
    }

    private static async Task<IReadOnlyList<string>> MembersAsync(RedfishSession session, string? collectionPath, CancellationToken cancellationToken)
    {
        if (collectionPath is null || await session.GetAsync(collectionPath, cancellationToken) is not { } collection)
        {
            return [];
        }

        return Array(collection, "Members").Select(member => Text(member, "@odata.id")).OfType<string>().ToList();
    }

    private static IEnumerable<RedfishComponent> Components(JsonElement resource, string arrayName, string readingProperty, Func<JsonElement, string> unit) =>
        Array(resource, arrayName).Select(entry => Component(entry, Number(entry, readingProperty), unit(entry), arrayName)).ToList();

    private static RedfishComponent Component(JsonElement entry, double? reading, string unit, string fallbackName = "Component")
    {
        var status = Object(entry, "Status");
        var state = Text(status, "State");
        var present = state is null || !(state.Equals("Absent", StringComparison.OrdinalIgnoreCase) || state.Equals("Disabled", StringComparison.OrdinalIgnoreCase) || state.Equals("UnavailableOffline", StringComparison.OrdinalIgnoreCase));
        var name = Text(entry, "Name") ?? Text(entry, "FanName") ?? Text(entry, "MemberId") ?? Text(entry, "Id") ?? fallbackName;
        return new RedfishComponent(name.Trim(), reading, unit, present ? Text(status, "Health") : null, present);
    }

    private static string? LastSegment(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.TrimEnd('/').Split('/')[^1];

    private static bool MatchesFilter(JsonElement system, string path, string? filter) =>
        string.IsNullOrEmpty(filter)
        || string.Equals(Text(system, "Id"), filter, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Text(system, "Name"), filter, StringComparison.OrdinalIgnoreCase)
        || path.TrimEnd('/').EndsWith("/" + filter, StringComparison.OrdinalIgnoreCase);

    // ---- the result ------------------------------------------------------------------------------------------------------

    /// <summary>Health as a number: OK 0, Warning 1, Critical 2 (Redfish's three values); anything else is unknown.</summary>
    public static int? HealthLevel(string? health) => health?.Trim().ToLowerInvariant() switch
    {
        "ok" => 0,
        "warning" => 1,
        "critical" => 2,
        _ => null
    };

    /// <summary>The channels, the state and the message of one reading - pure, so it is tested without a controller.</summary>
    public static SensorExecutionResult BuildResult(RedfishReading reading, TimeSpan duration)
    {
        var channels = new List<SensorChannelValue>();
        var levels = reading.Systems.Select(system => HealthLevel(system.Health))
            .Concat(reading.ChassisHealth.Select(HealthLevel))
            .Concat(reading.Temperatures.Concat(reading.Fans).Concat(reading.PowerSupplies).Select(component => HealthLevel(component.Health)))
            .OfType<int>()
            .ToList();
        var worst = levels.Count == 0 ? (int?)null : levels.Max();
        if (worst is { } level)
        {
            channels.Add(new SensorChannelValue
            {
                Key = HealthChannel, Label = "Health (0 OK, 1 warning, 2 critical)", Value = level,
                MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false
            });
        }

        var power = reading.Systems.Select(system => system.PoweredOn).OfType<bool>().ToList();
        if (power.Count > 0)
        {
            channels.Add(new SensorChannelValue
            {
                Key = PowerOnChannel, Label = "Powered on", Value = power.All(on => on) ? 1 : 0,
                MeasurementKind = SensorMeasurementKind.Boolean, LogByDefault = false
            });
        }

        var temperatures = reading.Temperatures.Where(component => component.Present && component.Reading is not null).ToList();
        if (temperatures.Count > 0)
        {
            channels.Add(new SensorChannelValue
            {
                Key = TemperatureMaxChannel, Label = "Highest temperature", Value = temperatures.Max(component => component.Reading),
                Unit = "°C", MeasurementKind = SensorMeasurementKind.Temperature
            });
        }

        if (reading.PowerWatts is { } watts)
        {
            channels.Add(new SensorChannelValue { Key = PowerWattsChannel, Label = "Power draw", Value = Math.Round(watts, 1), Unit = "W" });
        }

        var fans = reading.Fans.Where(component => component.Present).ToList();
        if (fans.Count > 0)
        {
            channels.Add(new SensorChannelValue
            {
                Key = FansFailedChannel, Label = "Fans failed", Value = fans.Count(component => HealthLevel(component.Health) > 0),
                MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false
            });
        }

        var supplies = reading.PowerSupplies.Where(component => component.Present).ToList();
        if (supplies.Count > 0)
        {
            channels.Add(new SensorChannelValue
            {
                Key = PowerSuppliesFailedChannel, Label = "Power supplies failed", Value = supplies.Count(component => HealthLevel(component.Health) > 0),
                MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false
            });
        }

        var used = new HashSet<string>(channels.Select(channel => channel.Key), StringComparer.Ordinal);
        foreach (var component in temperatures)
        {
            channels.Add(ComponentChannel("temp.", component, SensorMeasurementKind.Temperature, used));
        }

        foreach (var component in fans.Where(component => component.Reading is not null))
        {
            channels.Add(ComponentChannel("fan.", component, component.Unit == "%" ? SensorMeasurementKind.Percent : SensorMeasurementKind.Unknown, used));
        }

        foreach (var component in supplies.Where(component => component.Reading is not null))
        {
            channels.Add(ComponentChannel("psu.", component, SensorMeasurementKind.Unknown, used));
        }

        if (channels.Count == 0)
        {
            return SensorExecutionResult.Unknown("The controller answered but reported no system, temperature, fan or power supply.");
        }

        var defaultKey = channels.Any(channel => channel.Key == TemperatureMaxChannel) ? TemperatureMaxChannel
            : channels.Any(channel => channel.Key == PowerWattsChannel) ? PowerWattsChannel
            : channels[0].Key;
        channels = channels.Select(channel => channel with { IsDefault = channel.Key == defaultKey }).ToList();

        var message = Describe(reading, worst, temperatures, fans, supplies);
        var value = channels.First(channel => channel.IsDefault).Value;
        return worst switch
        {
            2 => SensorExecutionResult.Critical(duration, message, value, defaultKey, channels),
            1 => SensorExecutionResult.Warning(duration, message, value, defaultKey, channels),
            _ => SensorExecutionResult.Healthy(duration, message, value, defaultKey, channels)
        };
    }

    private static SensorChannelValue ComponentChannel(string prefix, RedfishComponent component, SensorMeasurementKind kind, HashSet<string> used)
    {
        var key = prefix + Slug(component.Name);
        for (var index = 2; !used.Add(key); index++)
        {
            key = prefix + Slug(component.Name) + "-" + index.ToString(CultureInfo.InvariantCulture);
        }

        return new SensorChannelValue
        {
            Key = key,
            Label = component.Name,
            Value = component.Reading,
            Unit = component.Unit,
            MeasurementKind = kind,
            State = HealthLevel(component.Health) switch { 2 => SensorState.Critical, 1 => SensorState.Warning, _ => null },
            Message = HealthLevel(component.Health) > 0 ? $"{component.Name}: {component.Health}" : null
        };
    }

    private static string Describe(RedfishReading reading, int? worst, List<RedfishComponent> temperatures, List<RedfishComponent> fans, List<RedfishComponent> supplies)
    {
        var builder = new StringBuilder();
        var model = reading.Systems.Select(system => system.Model).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
            ?? reading.Systems.Select(system => system.Name).FirstOrDefault();
        builder.Append(string.IsNullOrWhiteSpace(model) ? "Server" : model);
        builder.Append(": ").Append(worst switch { 2 => "critical", 1 => "warning", 0 => "OK", _ => "health not reported" });
        if (reading.Systems.Any(system => system.PoweredOn == false))
        {
            builder.Append(", powered OFF");
        }

        var problems = reading.Temperatures.Concat(reading.Fans).Concat(reading.PowerSupplies)
            .Where(component => HealthLevel(component.Health) > 0)
            .Select(component => $"{component.Name} {component.Health!.ToLowerInvariant()}")
            .Take(4)
            .ToList();
        if (problems.Count > 0)
        {
            builder.Append(" - ").Append(string.Join(", ", problems));
        }
        else if (worst > 0)
        {
            builder.Append(" - see the controller's log for the component");
        }

        var parts = new List<string>();
        if (temperatures.Count > 0)
        {
            parts.Add($"{temperatures.Max(component => component.Reading)!.Value.ToString("0.#", CultureInfo.InvariantCulture)} °C max");
        }

        if (fans.Count > 0)
        {
            parts.Add($"{fans.Count} fan{(fans.Count == 1 ? string.Empty : "s")}");
        }

        if (supplies.Count > 0)
        {
            parts.Add($"{supplies.Count} power suppl{(supplies.Count == 1 ? "y" : "ies")}");
        }

        if (reading.PowerWatts is { } watts)
        {
            parts.Add($"{watts.ToString("0", CultureInfo.InvariantCulture)} W");
        }

        if (parts.Count > 0)
        {
            builder.Append(" (").Append(string.Join(", ", parts)).Append(')');
        }

        return builder.ToString();
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    internal static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        }

        var slug = string.Join('-', builder.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "item" : slug.Length > 48 ? slug[..48].TrimEnd('-') : slug;
    }

    private static string? JoinModel(string? manufacturer, string? model) =>
        string.IsNullOrWhiteSpace(model) ? manufacturer?.Trim()
        : string.IsNullOrWhiteSpace(manufacturer) || model.Contains(manufacturer.Trim(), StringComparison.OrdinalIgnoreCase) ? model.Trim()
        : $"{manufacturer.Trim()} {model.Trim()}";

    private static Uri? ResolveBaseUri(SensorExecutionContext context)
    {
        var target = context.Target?.Trim();
        if (string.IsNullOrEmpty(target))
        {
            return null;
        }

        if (!target.Contains("://", StringComparison.Ordinal))
        {
            var port = MonitoringSettings.TryReadParameterInt(context.Settings, "redfish.port", out var configured) && configured is >= 1 and <= 65535 ? configured : 443;
            target = port == 443 ? $"https://{target}" : $"https://{target}:{port}";
        }

        return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? new Uri(uri.GetLeftPart(UriPartial.Authority) + "/")
            : null;
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

    private static string? Link(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var link) ? Text(link, "@odata.id") : null;

    private static JsonElement Object(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object).ToList()
            : [];

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static double? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private sealed class RedfishException(string message) : Exception(message);

    /// <summary>The requests of one read: Basic auth, a session when the controller refuses Basic, and the session's logout.</summary>
    private sealed class RedfishSession(HttpClient client, string username, string password)
    {
        private string? _token;
        private string? _sessionPath;
        private bool _triedSession;

        public async Task<JsonElement?> GetAsync(string path, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(path, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && _token is null && !_triedSession && username.Length > 0)
            {
                await LoginAsync(cancellationToken);
                using var retried = await SendAsync(path, cancellationToken);
                return await ReadAsync(retried, path, cancellationToken);
            }

            return await ReadAsync(response, path, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path.TrimStart('/'));
            Authorize(request);
            return await client.SendAsync(request, cancellationToken);
        }

        private void Authorize(HttpRequestMessage request)
        {
            if (_token is not null)
            {
                request.Headers.TryAddWithoutValidation("X-Auth-Token", _token);
            }
            else if (username.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
            }
        }

        private async Task<JsonElement?> ReadAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new RedfishException(response.StatusCode == HttpStatusCode.Unauthorized
                    ? username.Length == 0 ? "The controller wants a user name and password." : "The controller refused the user name or password."
                    : $"The controller user may not read {path} - give it at least read-only rights.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RedfishException($"The controller answered {(int)response.StatusCode} for {path}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }

        private async Task LoginAsync(CancellationToken cancellationToken)
        {
            _triedSession = true;
            using var request = new HttpRequestMessage(HttpMethod.Post, "redfish/v1/SessionService/Sessions")
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["UserName"] = username, ["Password"] = password }), Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode && response.Headers.TryGetValues("X-Auth-Token", out var tokens))
            {
                _token = tokens.FirstOrDefault();
                _sessionPath = response.Headers.Location?.IsAbsoluteUri == true ? response.Headers.Location.AbsolutePath : response.Headers.Location?.OriginalString;
            }
        }

        /// <summary>Gives the session slot back - best effort, with its own short deadline (the read's may have run out).</summary>
        public async Task LogoutAsync()
        {
            if (_token is null || string.IsNullOrEmpty(_sessionPath))
            {
                return;
            }

            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Delete, _sessionPath.TrimStart('/'));
                request.Headers.TryAddWithoutValidation("X-Auth-Token", _token);
                using var _ = await client.SendAsync(request, deadline.Token);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                // the controller expires it by itself
            }
        }
    }
}
