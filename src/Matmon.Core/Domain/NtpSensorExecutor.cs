using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Matmon.Core.Domain;

public sealed class NtpSensorExecutor : ISensorExecutor
{
    private static readonly DateTimeOffset NtpEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "ntp",
        DisplayName = "NTP",
        Description = "Checks NTP reachability, round-trip delay and clock offset.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "ntp.port",
                Label = "Port",
                Kind = SensorParameterKind.Integer,
                Description = "NTP UDP port",
                DefaultValue = "123",
                Min = 1,
                Max = 65535,
                Step = "1"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!context.OpenTcpPorts.Contains(123))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["ntp.port"] = "123";
        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "NTP",
                    context.Host,
                    settings,
                    "NTP-like port 123 is open.",
                    65)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target is required");
        }

        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "ntp.port", out var configuredPort)
            ? configuredPort
            : 123;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(3);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var watch = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(context.Target.Trim(), timeoutCts.Token);
            var address = addresses.FirstOrDefault(candidate =>
                candidate.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
            if (address is null)
            {
                watch.Stop();
                return SensorExecutionResult.Critical(watch.Elapsed, "target could not be resolved");
            }

            using var udp = new UdpClient(address.AddressFamily);
            udp.Connect(new IPEndPoint(address, Math.Clamp(port, 1, 65535)));

            var request = new byte[48];
            request[0] = 0x1B;
            var t1 = DateTimeOffset.UtcNow;
            await udp.SendAsync(request, timeoutCts.Token);
            var response = await udp.ReceiveAsync(timeoutCts.Token);
            var t4 = DateTimeOffset.UtcNow;
            watch.Stop();

            if (response.Buffer.Length < 48)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "invalid NTP response");
            }

            var t2 = ReadTimestamp(response.Buffer, 32);
            var t3 = ReadTimestamp(response.Buffer, 40);
            var delayMs = ((t4 - t1) - (t3 - t2)).TotalMilliseconds;
            var offsetMs = (((t2 - t1) + (t3 - t4)).TotalMilliseconds) / 2d;
            var absoluteOffsetMs = Math.Abs(offsetMs);

            var channels = new[]
            {
                new SensorChannelValue
                {
                    Key = "absoluteOffsetMs",
                    Label = "Offset abs",
                    Value = absoluteOffsetMs,
                    Unit = "ms",
                    IsDefault = true
                },
                new SensorChannelValue
                {
                    Key = "offsetMs",
                    Label = "Offset",
                    Value = offsetMs,
                    Unit = "ms"
                },
                new SensorChannelValue
                {
                    Key = "delayMs",
                    Label = "Delay",
                    Value = Math.Max(delayMs, 0),
                    Unit = "ms"
                },
                new SensorChannelValue
                {
                    Key = "reachable",
                    Label = "Reachable",
                    Value = 1
                }
            };

            var result = SensorExecutionResult.Healthy(
                watch.Elapsed,
                $"offset {offsetMs.ToString("0.#", CultureInfo.InvariantCulture)} ms",
                absoluteOffsetMs,
                "absoluteOffsetMs",
                channels);

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "ntp timeout");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static DateTimeOffset ReadTimestamp(byte[] buffer, int offset)
    {
        var seconds = ReadUInt32(buffer, offset);
        var fraction = ReadUInt32(buffer, offset + 4);
        var milliseconds = (fraction * 1000d) / 0x100000000L;
        return NtpEpoch.AddSeconds(seconds).AddMilliseconds(milliseconds);
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
    {
        return ((uint)buffer[offset] << 24) |
            ((uint)buffer[offset + 1] << 16) |
            ((uint)buffer[offset + 2] << 8) |
            buffer[offset + 3];
    }
}
