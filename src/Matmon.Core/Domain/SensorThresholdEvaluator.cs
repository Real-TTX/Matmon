using System.Globalization;

namespace Matmon.Core.Domain;

public static class SensorThresholdEvaluator
{
    public static SensorExecutionResult ApplyChannelThresholds(
        MonitoringSettings settings,
        SensorExecutionResult result)
    {
        if (result.Channels.Count == 0)
        {
            return result;
        }

        var adjustedChannels = new List<SensorChannelValue>(result.Channels.Count);
        var highestChannelState = SensorState.Unknown;

        foreach (var channel in result.Channels)
        {
            var adjustedChannel = channel;
            var channelState = channel.State;
            var channelMessage = channel.Message;
            var normalizedChannelState = channelState ?? SensorState.Unknown;

            if (channel.Value.HasValue)
            {
                if (MonitoringSettings.TryReadChannelThreshold(settings, channel.Key, "critical", out var criticalRule) &&
                    MonitoringSettings.IsThresholdBreached(criticalRule, channel.Value.Value))
                {
                    channelState = MaxState(normalizedChannelState, SensorState.Critical);
                    channelMessage = AppendMessage(channelMessage, BuildThresholdMessage("critical", criticalRule));
                }
                else if (MonitoringSettings.TryReadChannelThreshold(settings, channel.Key, "warning", out var warningRule) &&
                    MonitoringSettings.IsThresholdBreached(warningRule, channel.Value.Value))
                {
                    channelState = MaxState(normalizedChannelState, SensorState.Warning);
                    channelMessage = AppendMessage(channelMessage, BuildThresholdMessage("warning", warningRule));
                }
            }

            adjustedChannel = adjustedChannel with
            {
                State = channelState,
                Message = channelMessage
            };

            adjustedChannels.Add(adjustedChannel);
            highestChannelState = MaxState(highestChannelState, channelState ?? SensorState.Unknown);
        }

        var effectiveState = result.State;
        if (result.State is not SensorState.Disabled and not SensorState.Paused)
        {
            if (highestChannelState == SensorState.Critical)
            {
                effectiveState = SensorState.Critical;
            }
            else if (highestChannelState == SensorState.Warning &&
                result.State is SensorState.Healthy or SensorState.Unknown)
            {
                effectiveState = SensorState.Warning;
            }
        }

        return new SensorExecutionResult(effectiveState, result.Duration, result.Value, result.Message)
        {
            DefaultChannelKey = result.DefaultChannelKey,
            Channels = adjustedChannels
        };
    }

    private static SensorState MaxState(SensorState left, SensorState right)
    {
        if (left == right)
        {
            return left;
        }

        if (left == SensorState.Critical || right == SensorState.Critical)
        {
            return SensorState.Critical;
        }

        if (left == SensorState.Warning || right == SensorState.Warning)
        {
            return SensorState.Warning;
        }

        if (left == SensorState.Healthy || right == SensorState.Healthy)
        {
            return SensorState.Healthy;
        }

        return left == SensorState.Unknown ? right : left;
    }

    private static string BuildThresholdMessage(string severity, ThresholdRule rule)
    {
        var severityLabel = string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase)
            ? "error"
            : severity;
        return $"{severityLabel} {MonitoringSettings.FormatThresholdRule(rule)}";
    }

    private static string AppendMessage(string? existing, string addition)
    {
        if (string.IsNullOrWhiteSpace(addition))
        {
            return existing ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(existing))
        {
            return addition;
        }

        if (existing.Contains(addition, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        return $"{existing}; {addition}";
    }

    private static string FormatValue(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
