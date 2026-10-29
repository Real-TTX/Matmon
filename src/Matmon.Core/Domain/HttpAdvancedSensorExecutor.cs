using System.Diagnostics;
using System.Globalization;
using System.Text;
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
        Description = "HTTP check that downloads the response body (up to 8 MB) for text match and Regex/JSON/XML channel extraction. For a lightweight up/latency check use HTTP.",
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
                Kind = SensorParameterKind.Text,
                Description = "Treat this HTTP status code as healthy. 2xx accepts any success code.",
                DefaultValue = "200",
                Placeholder = "200 or 2xx"
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
                Key = "unexpectedText",
                Label = "Text that must not appear",
                Kind = SensorParameterKind.Text,
                Description = "Optional text that must NOT be in the response body (an error page, \"maintenance\")."
            },
            new SensorParameterDefinition
            {
                Key = "expectedPattern",
                Label = "Pattern that must match",
                Kind = SensorParameterKind.Text,
                Description = "Optional regular expression that must match the response body (case-insensitive).",
                Placeholder = "Version \\d+\\.\\d+"
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
    private readonly int _maxBodyBytes = DefaultMaxBodyBytes;

    // ONE public constructor: the typed HttpClient registration (AddHttpClient<T>) needs exactly one it can use.
    public HttpAdvancedSensorExecutor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>A smaller cap on the body read (the cloud Executor reads at most 2 MB of a customer's page).</summary>
    public int MaxBodyBytes
    {
        get => _maxBodyBytes;
        init => _maxBodyBytes = Math.Max(1024, value);
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
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var body = await ReadBodyWithLimitAsync(response.Content, _maxBodyBytes, timeoutCts.Token);
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

            var statusCode = (int)response.StatusCode;
            var statusOk = IsExpectedStatus(context.Settings, statusCode, out var expectedLabel);
            // A HEAD answer has no body, so the content checks would fail on every run - they only apply to a body.
            var contentProblem = method == HttpMethod.Head ? null : CheckContent(context.Settings, body);

            var state = statusOk && contentProblem is null ? SensorState.Healthy : SensorState.Critical;
            var message = !statusOk
                ? $"http {statusCode} expected {expectedLabel}"
                : contentProblem ?? $"http {statusCode} ok";

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

    // Cap on the response body buffered for content checks, so a huge/misbehaving endpoint can't OOM.
    public const int DefaultMaxBodyBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Whether the status is the one asked for: an exact code or a class - "2xx" = any success, "3xx" ... A missing
    /// or unreadable value means 200, as it always did.</summary>
    public static bool IsExpectedStatus(MonitoringSettings settings, int statusCode, out string expectedLabel)
    {
        var configured = MonitoringSettings.TryReadParameter(settings, "expectedStatus", out var raw) ? raw.Trim() : string.Empty;
        if (configured.Length == 3 && configured[0] is >= '1' and <= '5'
            && configured.EndsWith("xx", StringComparison.OrdinalIgnoreCase))
        {
            expectedLabel = configured.ToLowerInvariant();
            return statusCode / 100 == configured[0] - '0';
        }

        var expected = int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : 200;
        expectedLabel = expected.ToString(CultureInfo.InvariantCulture);
        return statusCode == expected;
    }

    /// <summary>The content checks on the body: a text that must be there, one that must not, a pattern that must match.
    /// Null when all pass, else what failed (that becomes the message).</summary>
    public static string? CheckContent(MonitoringSettings settings, string body)
    {
        if (MonitoringSettings.TryReadParameter(settings, "expectedText", out var expectedText) &&
            !string.IsNullOrWhiteSpace(expectedText) &&
            !body.Contains(expectedText, StringComparison.OrdinalIgnoreCase))
        {
            return "expected text missing";
        }

        if (MonitoringSettings.TryReadParameter(settings, "unexpectedText", out var unexpectedText) &&
            !string.IsNullOrWhiteSpace(unexpectedText) &&
            body.Contains(unexpectedText, StringComparison.OrdinalIgnoreCase))
        {
            return $"unexpected text found: \"{Shorten(unexpectedText)}\"";
        }

        if (MonitoringSettings.TryReadParameter(settings, "expectedPattern", out var pattern) &&
            !string.IsNullOrWhiteSpace(pattern))
        {
            try
            {
                // The pattern is someone's input and the body a stranger's page: the time limit keeps a catastrophic
                // pattern from holding the executor.
                if (!Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, PatternTimeout))
                {
                    return "pattern did not match";
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return "pattern took too long to match";
            }
            catch (ArgumentException exception)
            {
                return $"invalid pattern: {exception.Message}";
            }
        }

        return null;
    }

    private static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 60 ? trimmed : trimmed[..60] + "...";
    }

    private static async Task<string> ReadBodyWithLimitAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < maxBytes &&
            (read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)) > 0)
        {
            var toCopy = (int)Math.Min(read, maxBytes - buffer.Length);
            buffer.Write(chunk, 0, toCopy);
        }

        return ResolveEncoding(content.Headers.ContentType?.CharSet).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static Encoding ResolveEncoding(string? charSet)
    {
        if (!string.IsNullOrWhiteSpace(charSet))
        {
            try
            {
                return Encoding.GetEncoding(charSet.Trim().Trim('"'));
            }
            catch
            {
                // fall through to UTF-8
            }
        }

        return Encoding.UTF8;
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

        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, PatternTimeout);
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
