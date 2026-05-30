using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public static class SensorHistoryAnalytics
{
    public static double? GetDefaultValue(SensorObservation? observation, string? defaultChannelKeyOverride = null)
    {
        if (observation is null)
        {
            return null;
        }

        var defaultChannel = GetDefaultChannel(observation, defaultChannelKeyOverride);
        if (defaultChannel?.Value is double defaultValue)
        {
            return defaultValue;
        }

        return observation.Value;
    }

    public static SensorChannelValue? GetDefaultChannel(SensorObservation? observation, string? defaultChannelKeyOverride = null)
    {
        if (observation is null || observation.Channels.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(defaultChannelKeyOverride))
        {
            var byOverrideKey = observation.Channels.FirstOrDefault(channel =>
                string.Equals(channel.Key, defaultChannelKeyOverride, StringComparison.OrdinalIgnoreCase));

            if (byOverrideKey is not null)
            {
                return byOverrideKey;
            }
        }

        if (!string.IsNullOrWhiteSpace(observation.DefaultChannelKey))
        {
            var byConfiguredKey = observation.Channels.FirstOrDefault(channel =>
                string.Equals(channel.Key, observation.DefaultChannelKey, StringComparison.OrdinalIgnoreCase));

            if (byConfiguredKey is not null)
            {
                return byConfiguredKey;
            }
        }

        var markedDefault = observation.Channels.FirstOrDefault(channel => channel.IsDefault);
        if (markedDefault is not null)
        {
            return markedDefault;
        }

        return observation.Channels[0];
    }

    public static SensorWindowStatistics BuildWindowStatistics(
        IReadOnlyList<SensorObservation> observations,
        string key,
        string label,
        TimeSpan window,
        DateTimeOffset now,
        string lineColor,
        string? defaultChannelKeyOverride = null,
        int maxGraphPoints = 240)
    {
        var fromUtc = now - window;
        var windowObservations = observations
            .Where(observation => observation.TimestampUtc >= fromUtc && observation.TimestampUtc <= now)
            .ToArray();

        var allPoints = windowObservations
            .Select(observation => BuildGraphPoint(observation, defaultChannelKeyOverride))
            .Where(point => point is not null)
            .Select(point => point!)
            .ToArray();
        var points = DownsampleGraphPoints(allPoints, maxGraphPoints);

        var values = windowObservations
            .Select(observation => GetDefaultValue(observation, defaultChannelKeyOverride))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        var latestObservation = windowObservations.LastOrDefault();
        var latestValue = GetDefaultValue(latestObservation, defaultChannelKeyOverride);
        var latestState = latestObservation?.State ?? SensorState.Unknown;
        var (linePath, areaPath) = BuildPaths(points, fromUtc, now);

        return new SensorWindowStatistics(
            key,
            label,
            window,
            fromUtc,
            now,
            windowObservations.Length,
            values.Length == 0 ? null : values.Average(),
            values.Length == 0 ? null : values.Min(),
            values.Length == 0 ? null : values.Max(),
            latestValue,
            MonitoringStatePresentation.Key(latestState),
            MonitoringStatePresentation.Label(latestState),
            MonitoringStatePresentation.Color(latestState),
            linePath,
            areaPath,
            points,
            lineColor);
    }

    public static TelemetrySamplePoint? BuildGraphPoint(SensorObservation observation, string? defaultChannelKeyOverride = null)
    {
        if (observation.State == SensorState.Critical)
        {
            return new TelemetrySamplePoint(observation.TimestampUtc, 0d, observation.State);
        }

        var value = GetDefaultValue(observation, defaultChannelKeyOverride);
        return value.HasValue
            ? new TelemetrySamplePoint(observation.TimestampUtc, value.Value, observation.State)
            : null;
    }

    private static (string LinePath, string AreaPath) BuildPaths(
        IReadOnlyList<TelemetrySamplePoint> points,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        if (points.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        const double width = 100d;
        const double height = 40d;
        const double padding = 3d;

        var scaleValues = points
            .Where(point => point.State != SensorState.Critical)
            .Select(point => point.Value)
            .ToArray();
        if (scaleValues.Length == 0)
        {
            scaleValues = points.Select(point => point.Value).ToArray();
        }

        var min = scaleValues.Min();
        var max = scaleValues.Max();
        var range = max - min;
        if (Math.Abs(range) < 0.000001d)
        {
            range = 1d;
        }

        var spanSeconds = Math.Max((toUtc - fromUtc).TotalSeconds, 1d);
        var coords = points
            .Select(point =>
            {
                var x = ((point.TimestampUtc - fromUtc).TotalSeconds / spanSeconds) * width;
                var normalized = point.State == SensorState.Critical
                    ? 1d
                    : (point.Value - min) / range;
                normalized = Math.Clamp(normalized, 0d, 1d);
                var y = point.State == SensorState.Critical
                    ? padding
                    : height - padding - normalized * (height - padding * 2d);
                return (X: x, Y: y);
            })
            .ToArray();

        if (coords.Length == 1)
        {
            coords[0] = (width / 2d, coords[0].Y);
        }

        var line = string.Join(" ", coords.Select((point, index) => $"{(index == 0 ? "M" : "L")} {point.X:0.##} {point.Y:0.##}"));
        var areaSegments = new List<string>
        {
            $"M 0 {height:0.##}",
            $"L {coords[0].X:0.##} {coords[0].Y:0.##}"
        };
        areaSegments.AddRange(coords.Skip(1).Select(point => $"L {point.X:0.##} {point.Y:0.##}"));
        areaSegments.Add($"L {width:0.##} {height:0.##}");
        areaSegments.Add("Z");
        var area = string.Join(" ", areaSegments);

        return (line, area);
    }

    private static IReadOnlyList<TelemetrySamplePoint> DownsampleGraphPoints(
        IReadOnlyList<TelemetrySamplePoint> points,
        int maxGraphPoints)
    {
        if (maxGraphPoints <= 0 || points.Count <= maxGraphPoints)
        {
            return points;
        }

        var result = new List<TelemetrySamplePoint>(maxGraphPoints);
        var bucketSize = points.Count / (double)maxGraphPoints;

        for (var bucketIndex = 0; bucketIndex < maxGraphPoints; bucketIndex++)
        {
            var start = (int)Math.Floor(bucketIndex * bucketSize);
            var end = (int)Math.Floor((bucketIndex + 1) * bucketSize);
            if (bucketIndex == maxGraphPoints - 1)
            {
                end = points.Count;
            }

            end = Math.Clamp(end, start + 1, points.Count);
            var selected = points[start];
            for (var index = start + 1; index < end; index++)
            {
                var candidate = points[index];
                if (GetGraphPriority(candidate) > GetGraphPriority(selected) ||
                    (GetGraphPriority(candidate) == GetGraphPriority(selected) && candidate.Value > selected.Value))
                {
                    selected = candidate;
                }
            }

            result.Add(selected);
        }

        return result;
    }

    private static int GetGraphPriority(TelemetrySamplePoint point)
    {
        return point.State switch
        {
            SensorState.Critical => 3,
            SensorState.Warning => 2,
            SensorState.Paused => 1,
            SensorState.Disabled => 1,
            _ => 0
        };
    }
}

public sealed record SensorWindowStatistics(
    string Key,
    string Label,
    TimeSpan Window,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int SampleCount,
    double? Average,
    double? Minimum,
    double? Maximum,
    double? LatestValue,
    string StateKey,
    string StateLabel,
    string StateColor,
    string LinePath,
    string AreaPath,
    IReadOnlyList<TelemetrySamplePoint> Points,
    string LineColor);
