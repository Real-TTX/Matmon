
namespace Matmon.Core.Domain;

public static class SensorThresholdEvaluator
{
    // describeBreaches: the sensor's own message only echoes its default channel (the script, SQL and generic SNMP
    // sensors - "Cpu Load 11"), so it gains the channel that crossed its limit. A hand-written executor explains its
    // state in words already ("UPS on battery - 87 % ...") and keeps its message as it is.
    public static SensorExecutionResult ApplyChannelThresholds(
        MonitoringSettings settings,
        SensorExecutionResult result,
        bool describeBreaches = false)
    {
        if (result.Channels.Count == 0)
        {
            return result;
        }

        var adjustedChannels = new List<SensorChannelValue>(result.Channels.Count);
        var highestChannelState = SensorState.Unknown;
        var breaches = new List<(SensorState State, string Text)>();

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
                    breaches.Add((SensorState.Critical, BuildBreachText(channel, BuildThresholdMessage("critical", criticalRule))));
                }
                else if (MonitoringSettings.TryReadChannelThreshold(settings, channel.Key, "warning", out var warningRule) &&
                    MonitoringSettings.IsThresholdBreached(warningRule, channel.Value.Value))
                {
                    channelState = MaxState(normalizedChannelState, SensorState.Warning);
                    channelMessage = AppendMessage(channelMessage, BuildThresholdMessage("warning", warningRule));
                    breaches.Add((SensorState.Warning, BuildBreachText(channel, BuildThresholdMessage("warning", warningRule))));
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

        // The sensor's message says WHICH channel crossed its limit when a threshold - not the executor - made it worse.
        // It used to keep only the executor's own text, so a Windows Health warning for pending updates read
        // "Warning (Cpu Load 11)", in the list and in the notification. A breach no worse than what the executor already
        // reported is left out: the executor has explained that state in its own words ("UPS on battery - ...").
        // Worst first, at most three; AppendMessage dedupes, so applying the thresholds twice changes nothing.
        var message = result.Message;
        var reported = result.State is SensorState.Critical or SensorState.Warning ? result.State : SensorState.Healthy;
        foreach (var breach in breaches
            .Where(_ => describeBreaches)
            .Where(item => reported == SensorState.Healthy || (item.State == SensorState.Critical && reported == SensorState.Warning))
            .OrderByDescending(item => item.State == SensorState.Critical)
            .Take(3))
        {
            message = AppendMessage(message, breach.Text);
        }

        return new SensorExecutionResult(effectiveState, result.Duration, result.Value, message)
        {
            DefaultChannelKey = result.DefaultChannelKey,
            Channels = adjustedChannels
        };
    }

    private static string BuildBreachText(SensorChannelValue channel, string thresholdMessage)
    {
        var name = string.IsNullOrWhiteSpace(channel.Label) ? channel.Key : channel.Label;
        var value = channel.Value!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var unit = string.IsNullOrWhiteSpace(channel.Unit) ? string.Empty : channel.Unit == "%" ? "%" : " " + channel.Unit;
        return $"{name} {value}{unit} ({thresholdMessage})";
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
}
