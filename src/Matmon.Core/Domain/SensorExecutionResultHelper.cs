namespace Matmon.Core.Domain;

public static class SensorExecutionResultHelper
{
    private const string VirtualStateChannelKey = "sensorState";

    /// <summary>A channel that only carries state from one run to the next (the push sensor's last push, Mail Health's send
    /// time): virtual, and not the sensorState one. A number nobody should read - the pages leave it out.</summary>
    public static bool IsCarriedState(SensorChannelValue channel) =>
        channel.IsVirtual && !string.Equals(channel.Key, VirtualStateChannelKey, StringComparison.OrdinalIgnoreCase);

    public static SensorExecutionResult ApplyDefaultChannelSelection(
        MonitoringSettings settings,
        SensorExecutionResult result)
    {
        result = DropNonFiniteReadings(result);
        result = EnsureVirtualStateChannel(result);

        if (!string.IsNullOrWhiteSpace(settings.DefaultChannelKey))
        {
            var selectedChannel = result.Channels.FirstOrDefault(channel =>
                string.Equals(channel.Key, settings.DefaultChannelKey, StringComparison.OrdinalIgnoreCase));

            if (selectedChannel is null || !selectedChannel.Value.HasValue)
            {
                return result;
            }

            return result with
            {
                DefaultChannelKey = selectedChannel.Key,
                Value = selectedChannel.Value,
                Channels = result.Channels
                    .Select(channel => channel with
                    {
                        IsDefault = string.Equals(channel.Key, selectedChannel.Key, StringComparison.OrdinalIgnoreCase)
                    })
                    .ToArray()
            };
        }

        if (string.IsNullOrWhiteSpace(result.DefaultChannelKey) &&
            !result.Channels.Any(channel => channel.IsDefault))
        {
            var selectedChannel = result.Channels.FirstOrDefault(channel =>
                    !channel.IsVirtual && channel.Value.HasValue)
                ?? result.Channels.FirstOrDefault(channel => channel.Value.HasValue);

            if (selectedChannel is not null && selectedChannel.Value.HasValue)
            {
                return result with
                {
                    DefaultChannelKey = selectedChannel.Key,
                    Value = selectedChannel.Value,
                    Channels = result.Channels
                        .Select(channel => channel with
                        {
                            IsDefault = string.Equals(channel.Key, selectedChannel.Key, StringComparison.OrdinalIgnoreCase)
                        })
                        .ToArray()
                };
            }
        }

        return result;
    }

    // NaN / Infinity is no reading (a script printing "NaN" parses as one, a division by zero yields one), and JSON has
    // no way to write it: on a probe ONE such value made every post of the offline buffer throw, so no observation of
    // any sensor reached the primary until it aged out, and the dashboard's JSON failed the same way.
    private static SensorExecutionResult DropNonFiniteReadings(SensorExecutionResult result)
    {
        var valueBad = result.Value is { } value && !double.IsFinite(value);
        var channelBad = result.Channels.Any(channel => channel.Value is { } reading && !double.IsFinite(reading));
        if (!valueBad && !channelBad)
        {
            return result;
        }

        return result with
        {
            Value = valueBad ? null : result.Value,
            Channels = channelBad
                ? result.Channels
                    .Select(channel => channel.Value is { } reading && !double.IsFinite(reading) ? channel with { Value = null } : channel)
                    .ToArray()
                : result.Channels
        };
    }

    private static SensorExecutionResult EnsureVirtualStateChannel(SensorExecutionResult result)
    {
        if (result.State is SensorState.Disabled or SensorState.Paused)
        {
            return result;
        }

        if (result.Channels.Any(channel =>
                string.Equals(channel.Key, VirtualStateChannelKey, StringComparison.OrdinalIgnoreCase)))
        {
            return result;
        }

        var isHealthy = result.State == SensorState.Healthy;

        var virtualChannel = new SensorChannelValue
        {
            Key = VirtualStateChannelKey,
            Label = "Sensor State",
            Value = isHealthy ? 1d : 0d,
            MeasurementKind = SensorMeasurementKind.Boolean,
            State = result.State,
            Message = isHealthy ? "ok" : result.Message,
            IsVirtual = true
        };

        return result with
        {
            Channels = [virtualChannel, ..result.Channels]
        };
    }
}
