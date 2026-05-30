using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;

namespace Matmon.Core.Domain;

public sealed class TcpPortSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "tcp-port",
        DisplayName = "Port Open Check",
        Description = "Checks whether a TCP port is reachable.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "tcp.port",
                Label = "Port",
                Kind = SensorParameterKind.Integer,
                Description = "TCP port to connect to. Can be omitted when target is host:port or URL.",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "tcp.expectedOpen",
                Label = "Expected open",
                Kind = SensorParameterKind.Boolean,
                Description = "Healthy when the port is open. Set to false for negative checks.",
                DefaultValue = "true"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (context.OpenTcpPorts.Count == 0)
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var suggestions = context.OpenTcpPorts
            .Select(port =>
            {
                var settings = new MonitoringSettings();
                settings.Parameters["tcp.port"] = port.ToString(CultureInfo.InvariantCulture);
                settings.Parameters["tcp.expectedOpen"] = "true";

                return new SensorDiscoverySuggestion(
                    Definition.Key,
                    $"Port {port}",
                    string.Empty,
                    settings,
                    $"TCP port {port} is open.",
                    85);
            })
            .ToArray();

        return ValueTask.FromResult(SensorDiscoveryCheckResult.Available(suggestions));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target is required");
        }

        var (host, parsedPort) = ParseTarget(context.Target);
        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "tcp.port", out var configuredPort)
            ? configuredPort
            : parsedPort;
        if (string.IsNullOrWhiteSpace(host))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target host is required");
        }

        if (port is null or < 1 or > 65535)
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "port is required");
        }

        var expectedOpen = !MonitoringSettings.TryReadParameterBool(context.Settings, "tcp.expectedOpen", out var configuredExpectedOpen) ||
            configuredExpectedOpen;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(3);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port.Value, timeoutCts.Token);
            watch.Stop();

            var channels = BuildChannels(open: true, watch.Elapsed.TotalMilliseconds);
            var result = expectedOpen
                ? SensorExecutionResult.Healthy(watch.Elapsed, $"port {port} open", watch.Elapsed.TotalMilliseconds, "connectMs", channels)
                : SensorExecutionResult.Critical(watch.Elapsed, $"port {port} is open", watch.Elapsed.TotalMilliseconds, "connectMs", channels);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            var channels = BuildChannels(open: false, watch.Elapsed.TotalMilliseconds);
            var result = expectedOpen
                ? SensorExecutionResult.Critical(watch.Elapsed, $"port {port} timed out", watch.Elapsed.TotalMilliseconds, "connectMs", channels)
                : SensorExecutionResult.Healthy(watch.Elapsed, $"port {port} closed", watch.Elapsed.TotalMilliseconds, "connectMs", channels);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (Exception ex)
        {
            watch.Stop();
            var channels = BuildChannels(open: false, watch.Elapsed.TotalMilliseconds);
            var result = expectedOpen
                ? SensorExecutionResult.Critical(watch.Elapsed, $"port {port} closed - {ex.Message}", watch.Elapsed.TotalMilliseconds, "connectMs", channels)
                : SensorExecutionResult.Healthy(watch.Elapsed, $"port {port} closed", watch.Elapsed.TotalMilliseconds, "connectMs", channels);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
    }

    private static IReadOnlyList<SensorChannelValue> BuildChannels(bool open, double connectMs)
    {
        return
        [
            new SensorChannelValue
            {
                Key = "connectMs",
                Label = "Connect",
                Value = Math.Round(connectMs, 2),
                Unit = "ms",
                IsDefault = true
            },
            new SensorChannelValue
            {
                Key = "open",
                Label = "Open",
                Value = open ? 1 : 0,
                State = open ? SensorState.Healthy : SensorState.Critical
            }
        ];
    }

    private static (string Host, int? Port) ParseTarget(string target)
    {
        var trimmed = target.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return (uri.Host, uri.IsDefaultPort ? null : uri.Port);
        }

        var colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex > 0 &&
            colonIndex < trimmed.Length - 1 &&
            trimmed.Count(character => character == ':') == 1 &&
            int.TryParse(trimmed[(colonIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return (trimmed[..colonIndex], port);
        }

        return (trimmed, null);
    }
}
