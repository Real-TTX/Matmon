using System.Diagnostics;

namespace Matmon.Core.Domain;

public sealed class HttpSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "http",
        DisplayName = "HTTP",
        Description = "HTTP status and response time",
        ChannelMode = SensorChannelMode.Fixed,
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
            }
        ]
    };

    private readonly HttpClient _httpClient;

    public HttpSensorExecutor(HttpClient httpClient)
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
            if (TryResolveWebScheme(port, out var useSsl))
            {
                suggestions.Add(BuildHttpSuggestion(context.Host, port, useSsl));
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

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(5);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var watch = Stopwatch.StartNew();
        try
        {
            var method = ResolveHttpMethod(context.Settings);
            using var request = new HttpRequestMessage(method, targetUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);

            watch.Stop();

            var latencyChannel = new SensorChannelValue
            {
                Key = "latency",
                Label = "Latency",
                Value = watch.Elapsed.TotalMilliseconds,
                Unit = "ms",
                IsDefault = true
            };

            var statusCodeChannel = new SensorChannelValue
            {
                Key = "statusCode",
                Label = "Status code",
                Value = (int)response.StatusCode
            };

            if (MonitoringSettings.TryReadParameterInt(context.Settings, "expectedStatus", out var expectedStatus) &&
                (int)response.StatusCode != expectedStatus)
            {
                var result = SensorExecutionResult.Critical(
                    watch.Elapsed,
                    $"http {(int)response.StatusCode} {response.ReasonPhrase}",
                    watch.Elapsed.TotalMilliseconds,
                    latencyChannel.Key,
                    [latencyChannel, statusCodeChannel]);
                return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
            }

            if (!response.IsSuccessStatusCode)
            {
                var result = SensorExecutionResult.Critical(
                    watch.Elapsed,
                    $"http {(int)response.StatusCode} {response.ReasonPhrase}",
                    watch.Elapsed.TotalMilliseconds,
                    latencyChannel.Key,
                    [latencyChannel, statusCodeChannel]);
                return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
            }

            var healthyResult = SensorExecutionResult.Healthy(
                watch.Elapsed,
                $"http {(int)response.StatusCode} ok",
                watch.Elapsed.TotalMilliseconds,
                latencyChannel.Key,
                [latencyChannel, statusCodeChannel]);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, healthyResult);
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

    private static HttpMethod ResolveHttpMethod(MonitoringSettings settings)
    {
        if (MonitoringSettings.TryReadParameter(settings, "method", out var method))
        {
            return method.Trim().ToUpperInvariant() switch
            {
                "HEAD" => HttpMethod.Head,
                "POST" => HttpMethod.Post,
                _ => HttpMethod.Get
            };
        }

        return HttpMethod.Get;
    }

    private static SensorDiscoverySuggestion BuildHttpSuggestion(string host, int port, bool useSsl)
    {
        var settings = new MonitoringSettings();
        settings.Parameters["method"] = "HEAD";
        settings.Parameters["expectedStatus"] = "200";

        var scheme = useSsl ? "https" : "http";
        var isDefaultPort = (!useSsl && port == 80) || (useSsl && port == 443);
        var target = isDefaultPort
            ? $"{scheme}://{host}/"
            : $"{scheme}://{host}:{port}/";
        var name = isDefaultPort
            ? (useSsl ? "HTTPS" : "HTTP")
            : $"{(useSsl ? "HTTPS" : "HTTP")} {port}";
        return new SensorDiscoverySuggestion(
            Definition.Key,
            name,
            target,
            settings,
            $"Web port {port} is open.",
            82);
    }

    private static bool TryResolveWebScheme(int port, out bool useSsl)
    {
        useSsl = port is 443 or 5001 or 8006 or 8443;
        return port is 80 or 443 or 5000 or 5001 or 8006 or 8080 or 8099 or 8443;
    }
}
