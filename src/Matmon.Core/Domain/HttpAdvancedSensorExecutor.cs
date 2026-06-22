using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Matmon.Core.Domain;

public sealed class HttpAdvancedSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "http-advanced",
        DisplayName = "HTTP Advanced",
        Description = "HTTP check with body validation and Regex, JSON or XML channel extraction.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "method",
                Label = "Method",
                Kind = SensorParameterKind.ValueList,
                Description = "HTTP method to use",
                DefaultValue = "GET",
                Options =
                [
                    new SensorParameterOption { Value = "GET", Label = "GET" },
                    new SensorParameterOption { Value = "HEAD", Label = "HEAD" },
                    new SensorParameterOption { Value = "POST", Label = "POST" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "expectedStatus",
                Label = "Expected status",
                Kind = SensorParameterKind.Integer,
                Description = "Treat this HTTP status code as healthy",
                DefaultValue = "200",
                Min = 100,
                Max = 599,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "expectedText",
                Label = "Expected text",
                Kind = SensorParameterKind.Text,
                Description = "Optional text that must be present in the response body."
            },
            new SensorParameterDefinition
            {
                Key = "extractMode",
                Label = "Extract mode",
                Kind = SensorParameterKind.ValueList,
                Description = "How numeric channels are extracted from the response body",
                DefaultValue = "none",
                Options =
                [
                    new SensorParameterOption { Value = "none", Label = "None" },
                    new SensorParameterOption { Value = "regex", Label = "Regex groups" },
                    new SensorParameterOption { Value = "json", Label = "JSON paths" },
                    new SensorParameterOption { Value = "xml", Label = "XML paths" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "regexPattern",
                Label = "Regex pattern",
                Kind = SensorParameterKind.Multiline,
                Description = "Named numeric groups become channels.",
                Placeholder = "(?<value>\\d+(?:\\.\\d+)?)",
                VisibleWhenParameterKey = "extractMode",
                VisibleWhenValues = ["regex"]
            },
            new SensorParameterDefinition
            {
                Key = "jsonPaths",
                Label = "JSON paths",
                Kind = SensorParameterKind.Multiline,
                Description = "One path per line. Use path|Label. Arrays return their length.",
                Placeholder = "data.count|Count",
                VisibleWhenParameterKey = "extractMode",
                VisibleWhenValues = ["json"]
            },
            new SensorParameterDefinition
            {
                Key = "xmlPaths",
                Label = "XML paths",
                Kind = SensorParameterKind.Multiline,
                Description = "One element path per line. Use path|Label.",
                Placeholder = "status/value|Value",
                VisibleWhenParameterKey = "extractMode",
                VisibleWhenValues = ["xml"]
            }
        ]
    };

    private readonly HttpClient _httpClient;

    public HttpAdvancedSensorExecutor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var suggestions = new List<SensorDiscoverySuggestion>();
        foreach (var port in context.OpenTcpPorts)
        {
            if (port is 80 or 443 or 5000 or 5001 or 8006 or 8080 or 8443)
            {
                var useSsl = port is 443 or 5001 or 8006 or 8443;
                var target = $"{(useSsl ? "https" : "http")}://{context.Host}{(port is 80 or 443 ? string.Empty : $":{port}")}";
                var settings = new MonitoringSettings();
                settings.Parameters["method"] = "GET";
                settings.Parameters["expectedStatus"] = "200";
                settings.Parameters["extractMode"] = "none";
                suggestions.Add(new SensorDiscoverySuggestion(
                    Definition.Key,
                    port is 80 or 443 ? "HTTP Advanced" : $"HTTP Advanced {port}",
                    target,
                    settings,
                    $"Web port {port} is open.",
                    76));
            }
        }

        return ValueTask.FromResult(
            suggestions.Count == 0
                ? SensorDiscoveryCheckResult.NotAvailable
                : SensorDiscoveryCheckResult.Available([.. suggestions]));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(context.Target, UriKind.Absolute, out var targetUri))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target must be an absolute URL");
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(10);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();

        try
        {
            var method = MonitoringSettings.TryReadParameter(context.Settings, "method", out var configuredMethod) &&
                !string.IsNullOrWhiteSpace(configuredMethod)
                ? new HttpMethod(configuredMethod.Trim().ToUpperInvariant())
                : HttpMethod.Get;

            using var request = new HttpRequestMessage(method, targetUri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            watch.Stop();

            var channels = new List<SensorChannelValue>
            {
                new()
                {
                    Key = "latency",
                    Label = "Latency",
                    Value = watch.Elapsed.TotalMilliseconds,
                    Unit = "ms"
                },
                new()
                {
                    Key = "statusCode",
                    Label = "Status code",
                    Value = (int)response.StatusCode
                },
                new()
                {
                    Key = "contentLength",
                    Label = "Content length",
                    Value = body.Length,
                    Unit = "chars"
                }
            };

            channels.AddRange(ExtractChannels(context.Settings, body));
            var defaultChannelKey = ResolveDefaultChannelKey(context.Settings, channels);
            channels = MarkDefault(channels, defaultChannelKey).ToList();

            var expectedStatus = MonitoringSettings.TryReadParameterInt(context.Settings, "expectedStatus", out var configuredExpectedStatus)
                ? configuredExpectedStatus
                : 200;
            var expectedTextOk = !MonitoringSettings.TryReadParameter(context.Settings, "expectedText", out var expectedText) ||
                string.IsNullOrWhiteSpace(expectedText) ||
                body.Contains(expectedText, StringComparison.OrdinalIgnoreCase);
            var statusOk = (int)response.StatusCode == expectedStatus;

            var state = statusOk && expectedTextOk ? SensorState.Healthy : SensorState.Critical;
            var message = statusOk
                ? expectedTextOk ? $"http {(int)response.StatusCode} ok" : "expected text missing"
                : $"http {(int)response.StatusCode} expected {expectedStatus}";

            var defaultValue = channels.FirstOrDefault(channel => channel.IsDefault)?.Value;
            var result = new SensorExecutionResult(state, watch.Elapsed, defaultValue, message)
            {
                DefaultChannelKey = defaultChannelKey,
                Channels = channels
            };

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "http timeout");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static IReadOnlyList<SensorChannelValue> ExtractChannels(MonitoringSettings settings, string body)
    {
        var mode = MonitoringSettings.TryReadParameter(settings, "extractMode", out var configuredMode)
            ? configuredMode.Trim().ToLowerInvariant()
            : "none";

        return mode switch
        {
            "regex" => ExtractRegex(settings, body),
            "json" => ExtractJson(settings, body),
            "xml" => ExtractXml(settings, body),
            _ => []
        };
    }

    private static IReadOnlyList<SensorChannelValue> ExtractRegex(MonitoringSettings settings, string body)
    {
        if (!MonitoringSettings.TryReadParameter(settings, "regexPattern", out var pattern) ||
            string.IsNullOrWhiteSpace(pattern))
        {
            return [];
        }

        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        var match = regex.Match(body);
        if (!match.Success)
        {
            return [];
        }

        return regex.GetGroupNames()
            .Where(name => !int.TryParse(name, out _))
            .Select(name => (Name: name, Value: match.Groups[name].Value))
            .Where(pair => double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(pair => new SensorChannelValue
            {
                Key = NormalizeKey(pair.Name),
                Label = pair.Name,
                Value = double.Parse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture)
            })
            .ToArray();
    }

    private static IReadOnlyList<SensorChannelValue> ExtractJson(MonitoringSettings settings, string body)
    {
        if (!MonitoringSettings.TryReadParameter(settings, "jsonPaths", out var paths) ||
            string.IsNullOrWhiteSpace(paths))
        {
            return [];
        }

        using var document = JsonDocument.Parse(body);
        return ParsePathLines(paths)
            .Select(path => BuildJsonChannel(document.RootElement, path.Path, path.Label))
            .Where(channel => channel is not null)
            .Cast<SensorChannelValue>()
            .ToArray();
    }

    private static IReadOnlyList<SensorChannelValue> ExtractXml(MonitoringSettings settings, string body)
    {
        if (!MonitoringSettings.TryReadParameter(settings, "xmlPaths", out var paths) ||
            string.IsNullOrWhiteSpace(paths))
        {
            return [];
        }

        var document = XDocument.Parse(body);
        return ParsePathLines(paths)
            .Select(path => BuildXmlChannel(document, path.Path, path.Label))
            .Where(channel => channel is not null)
            .Cast<SensorChannelValue>()
            .ToArray();
    }

    private static SensorChannelValue? BuildJsonChannel(JsonElement root, string path, string label)
    {
        if (!TryResolveJsonPath(root, path, out var element))
        {
            return null;
        }

        double value;
        if (element.ValueKind == JsonValueKind.Array)
        {
            value = element.GetArrayLength();
        }
        else if (element.ValueKind == JsonValueKind.Number)
        {
            value = element.GetDouble();
        }
        else if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean() ? 1 : 0;
        }
        else if (!double.TryParse(element.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return null;
        }

        return new SensorChannelValue
        {
            Key = NormalizeKey(label),
            Label = label,
            Value = value
        };
    }

    private static SensorChannelValue? BuildXmlChannel(XDocument document, string path, string label)
    {
        var current = document.Root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            current = current?.Elements().FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, segment, StringComparison.OrdinalIgnoreCase));
        }

        if (current is null ||
            !double.TryParse(current.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return new SensorChannelValue
        {
            Key = NormalizeKey(label),
            Label = label,
            Value = value
        };
    }

    private static bool TryResolveJsonPath(JsonElement root, string path, out JsonElement element)
    {
        element = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(segment, out var child))
            {
                element = child;
                continue;
            }

            return false;
        }

        return true;
    }

    private static IReadOnlyList<(string Path, string Label)> ParsePathLines(string raw)
    {
        return raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var separator = line.IndexOf('|');
                if (separator < 0)
                {
                    return (line.Trim(), line.Trim());
                }

                return (line[..separator].Trim(), line[(separator + 1)..].Trim());
            })
            .Where(path => !string.IsNullOrWhiteSpace(path.Item1))
            .ToArray();
    }

    private static string ResolveDefaultChannelKey(MonitoringSettings settings, IReadOnlyList<SensorChannelValue> channels)
    {
        if (MonitoringSettings.TryReadParameter(settings, "defaultChannelKey", out var configuredDefaultChannelKey) &&
            !string.IsNullOrWhiteSpace(configuredDefaultChannelKey) &&
            channels.Any(channel => string.Equals(channel.Key, configuredDefaultChannelKey.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return configuredDefaultChannelKey.Trim();
        }

        return channels.FirstOrDefault(channel => channel.Key != "statusCode" && channel.Key != "contentLength")?.Key
            ?? channels.FirstOrDefault()?.Key
            ?? "latency";
    }

    private static IReadOnlyList<SensorChannelValue> MarkDefault(IReadOnlyList<SensorChannelValue> channels, string key)
    {
        return channels.Select(channel => channel with
        {
            IsDefault = string.Equals(channel.Key, key, StringComparison.OrdinalIgnoreCase)
        }).ToArray();
    }

    private static string NormalizeKey(string key)
    {
        var normalized = new string(key.Trim().Select(character =>
            char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_').ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "value" : normalized;
    }
}
