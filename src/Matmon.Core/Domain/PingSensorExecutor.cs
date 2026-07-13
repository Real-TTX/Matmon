using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Matmon.Core.Domain;

public sealed class PingSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "ping",
        DisplayName = "Ping",
        Description = "Checks host reachability and round-trip latency via ICMP echo (ping).",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "payloadSize",
                Label = "Payload size",
                Kind = SensorParameterKind.Integer,
                Description = "ICMP payload bytes",
                DefaultValue = "32",
                Min = 1,
                Max = 65507,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "dontFragment",
                Label = "Don't fragment",
                Kind = SensorParameterKind.Boolean,
                Description = "Set the ICMP DF flag",
                DefaultValue = "false"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!context.PingAlive)
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var reason = context.PingMs is double latency
            ? $"Ping answered in {latency.ToString("0.#", CultureInfo.InvariantCulture)} ms."
            : "Ping answered.";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Ping",
                    string.Empty,
                    new MonitoringSettings(),
                    reason,
                    95)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target is required");
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(2);
        var timeoutMs = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
        var payloadSize = MonitoringSettings.TryReadParameterInt(context.Settings, "payloadSize", out var configuredPayloadSize)
            ? configuredPayloadSize
            : 32;
        var dontFragment = MonitoringSettings.TryReadParameterBool(context.Settings, "dontFragment", out var configuredDontFragment) &&
            configuredDontFragment;
        var buffer = new byte[Math.Clamp(payloadSize, 1, 64 * 1024)];
        var options = new PingOptions
        {
            DontFragment = dontFragment
        };
        var watch = Stopwatch.StartNew();

        try
        {
            var (resolvedTarget, resolutionMessage) = await ResolveTargetAsync(context.Target, timeout, cancellationToken);
            if (resolvedTarget is null)
            {
                watch.Stop();
                return SensorExecutionResult.Critical(watch.Elapsed, resolutionMessage ?? "target could not be resolved");
            }

            using var ping = new Ping();
            var reply = await ping.SendPingAsync(resolvedTarget, timeoutMs, buffer, options);
            watch.Stop();

            if (reply.Status != IPStatus.Success)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, reply.Status.ToString());
            }

            var latencyChannel = new SensorChannelValue
            {
                Key = "latency",
                Label = "Latency",
                Value = reply.RoundtripTime,
                Unit = "ms",
                IsDefault = true
            };

            var result = SensorExecutionResult.Healthy(
                watch.Elapsed,
                $"latency {reply.RoundtripTime} ms",
                reply.RoundtripTime,
                latencyChannel.Key,
                [latencyChannel]);

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (PingException ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static async Task<(IPAddress? Address, string? ErrorMessage)> ResolveTargetAsync(
        string target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target, out var address))
        {
            return (address, null);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target).WaitAsync(timeout, cancellationToken);
            var resolvedAddress = addresses.FirstOrDefault(candidate =>
                candidate.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);

            if (resolvedAddress is null)
            {
                return (null, $"target '{target}' could not be resolved");
            }

            return (resolvedAddress, null);
        }
        catch (TimeoutException)
        {
            return (null, $"target resolution timed out for '{target}'");
        }
        catch (SocketException ex)
        {
            return (null, ex.Message);
        }
    }
}
