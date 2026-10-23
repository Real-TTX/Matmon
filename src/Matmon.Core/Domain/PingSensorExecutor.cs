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
        Description = "Checks host reachability, round-trip latency, packet loss and jitter via ICMP echo (ping).",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = CountParameter,
                Label = "Packets per check",
                Kind = SensorParameterKind.Integer,
                Description = "Echo requests sent per check, 200 ms apart - more than one measures packet loss and jitter. 1 = just reachability and latency.",
                DefaultValue = DefaultCount.ToString(CultureInfo.InvariantCulture),
                Min = 1,
                Max = MaxCount,
                Step = "1"
            },
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

    public const string CountParameter = "count";

    /// <summary>Packets per check when the sensor says nothing - also what a sensor created before the parameter existed
    /// gets, so every ping measures loss and jitter. Three keep a 30-second ping cheap.</summary>
    public const int DefaultCount = 3;

    public const int MaxCount = 20;

    /// <summary>The gap between two echo requests of one check (they are sent staggered, not one after another's answer,
    /// so a check of a dead host takes one timeout, not one per packet).</summary>
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(200);

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

            var count = MonitoringSettings.TryReadParameterInt(context.Settings, CountParameter, out var configuredCount)
                ? Math.Clamp(configuredCount, 1, MaxCount)
                : DefaultCount;
            var sends = new List<Task<PingReply>>(count);
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    await Task.Delay(Gap, cancellationToken);
                }

                sends.Add(SendOneAsync(resolvedTarget, timeoutMs, buffer, options));
            }

            var replies = await Task.WhenAll(sends);
            watch.Stop();

            var series = PingSeries.From(replies.Select(reply => reply.Status == IPStatus.Success ? (long?)reply.RoundtripTime : null).ToList());
            if (series.Received == 0)
            {
                var status = replies.Select(reply => reply.Status).FirstOrDefault(candidate => candidate != IPStatus.Success);
                return SensorExecutionResult.Critical(watch.Elapsed, count == 1 ? status.ToString() : $"{status} - no reply to {count} of {count}")
                    with
                    {
                        Channels = count == 1 ? [] : [LossChannel(series)]
                    };
            }

            var channels = new List<SensorChannelValue>
            {
                new() { Key = "latency", Label = "Latency", Value = series.Average, Unit = "ms", MeasurementKind = SensorMeasurementKind.Duration, IsDefault = true }
            };
            if (count > 1)
            {
                channels.Add(LossChannel(series));
                if (series.Jitter is not null)
                {
                    channels.Add(new SensorChannelValue { Key = "jitter", Label = "Jitter", Value = series.Jitter, Unit = "ms", MeasurementKind = SensorMeasurementKind.Duration });
                }

                channels.Add(new SensorChannelValue { Key = "latencyMin", Label = "Latency (min)", Value = series.Min, Unit = "ms", MeasurementKind = SensorMeasurementKind.Duration, LogByDefault = false });
                channels.Add(new SensorChannelValue { Key = "latencyMax", Label = "Latency (max)", Value = series.Max, Unit = "ms", MeasurementKind = SensorMeasurementKind.Duration });
            }

            var result = SensorExecutionResult.Healthy(
                watch.Elapsed,
                series.Describe(),
                series.Average,
                "latency",
                channels);

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

    private static async Task<PingReply> SendOneAsync(IPAddress target, int timeoutMs, byte[] buffer, PingOptions options)
    {
        using var ping = new Ping();
        return await ping.SendPingAsync(target, timeoutMs, buffer, options);
    }

    private static SensorChannelValue LossChannel(PingSeries series) => new()
    {
        Key = "packetLoss",
        Label = "Packet loss",
        Value = series.LossPercent,
        Unit = "%",
        MeasurementKind = SensorMeasurementKind.Percent
    };

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

/// <summary>
/// What one ping check measured: the round-trip times of the packets that came back (null = lost), in the order they were
/// sent. Pure, so the arithmetic is tested rather than eyeballed: loss is lost/sent, latency the mean of what came back,
/// jitter the mean absolute difference between CONSECUTIVE answered packets (RFC 3550's idea, without its smoothing - a
/// check is a handful of packets, not a stream).
/// </summary>
public sealed record PingSeries(int Sent, int Received, double LossPercent, double? Average, double? Min, double? Max, double? Jitter)
{
    public static PingSeries From(IReadOnlyList<long?> roundTrips)
    {
        var answered = roundTrips.Where(value => value is not null).Select(value => (double)value!.Value).ToList();
        var sent = roundTrips.Count;
        var loss = sent == 0 ? 0 : Math.Round((sent - answered.Count) * 100.0 / sent, 1);
        if (answered.Count == 0)
        {
            return new PingSeries(sent, 0, loss, null, null, null, null);
        }

        double? jitter = answered.Count < 2
            ? null
            : Math.Round(answered.Zip(answered.Skip(1), (first, second) => Math.Abs(second - first)).Average(), 2);
        return new PingSeries(sent, answered.Count, loss, Math.Round(answered.Average(), 2), answered.Min(), answered.Max(), jitter);
    }

    /// <summary>"latency 12 ms" / "latency 12 ms, jitter 1.5 ms" / "latency 12 ms, 1 of 3 lost, jitter 4 ms".</summary>
    public string Describe()
    {
        var parts = new List<string> { $"latency {Format(Average)} ms" };
        if (Received < Sent)
        {
            parts.Add($"{Sent - Received} of {Sent} lost");
        }

        if (Sent > 1 && Jitter is { } jitter)
        {
            parts.Add($"jitter {Format(jitter)} ms");
        }

        return string.Join(", ", parts);
    }

    private static string Format(double? value) => value is { } number ? number.ToString("0.#", CultureInfo.InvariantCulture) : "-";
}
