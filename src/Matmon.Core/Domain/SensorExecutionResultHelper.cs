namespace Matmon.Core.Domain;

public static class SensorExecutionResultHelper
{
    private const string VirtualStateChannelKey = "sensorState";

    public static SensorExecutionResult ApplyDefaultChannelSelection(
        MonitoringSettings settings,
        SensorExecutionResult result)
    {
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
