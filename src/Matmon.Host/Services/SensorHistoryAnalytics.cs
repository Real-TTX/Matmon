using System.Globalization;
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

        var markedDefault = observation.Channels.FirstOrDefault(channel => channel.IsDefault && !channel.IsVirtual)
            ?? observation.Channels.FirstOrDefault(channel => channel.IsDefault);
        if (markedDefault is not null)
        {
            return markedDefault;
        }

        return observation.Channels.FirstOrDefault(channel => !channel.IsVirtual && channel.Value.HasValue)
            ?? observation.Channels.FirstOrDefault(channel => channel.Value.HasValue)
            ?? observation.Channels[0];
    }

    public static SensorWindowStatistics BuildWindowStatistics(
        IReadOnlyList<SensorObservation> observations,
        string key,
        string label,
        TimeSpan window,
        DateTimeOffset now,
        string lineColor,
        string? defaultChannelKeyOverride = null,
        SensorUnitScale? scale = null,
        int maxGraphPoints = 240,
        double? axisMin = null,
        double? axisMax = null)
    {
        var fromUtc = now - window;
        var windowObservations = observations
            .Where(observation => observation.TimestampUtc >= fromUtc && observation.TimestampUtc <= now)
            .ToArray();

        var allPoints = windowObservations
            .Select(observation => BuildGraphPoint(observation, defaultChannelKeyOverride, scale))
            .Where(point => point is not null)
            .Select(point => point!)
            .ToArray();
        var points = DownsampleGraphPoints(allPoints, maxGraphPoints);

        var values = windowObservations
            .Select(observation => ApplyScale(GetDefaultValue(observation, defaultChannelKeyOverride), scale))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        var latestObservation = windowObservations.LastOrDefault();
        var latestValue = ApplyScale(GetDefaultValue(latestObservation, defaultChannelKeyOverride), scale);
        var latestState = latestObservation?.State ?? SensorState.Unknown;
        var (linePath, areaPath, graphMin, graphMax) = BuildPaths(points, fromUtc, now, axisMin, axisMax);

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
            lineColor,
            graphMin,
            graphMax);
    }

    public static TelemetrySamplePoint? BuildGraphPoint(
        SensorObservation observation,
        string? defaultChannelKeyOverride = null,
        SensorUnitScale? scale = null)
    {
        if (observation.State == SensorState.Critical)
        {
            return new TelemetrySamplePoint(observation.TimestampUtc, 0d, observation.State);
        }

        var value = ApplyScale(GetDefaultValue(observation, defaultChannelKeyOverride), scale);
        return value.HasValue
            ? new TelemetrySamplePoint(observation.TimestampUtc, value.Value, observation.State)
            : null;
    }

    private static double? ApplyScale(double? value, SensorUnitScale? scale)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return scale is { } actualScale
            ? actualScale.Convert(value.Value)
            : value;
    }

    private static (string LinePath, string AreaPath, double Min, double Max) BuildPaths(
        IReadOnlyList<TelemetrySamplePoint> points,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        double? axisMin = null,
        double? axisMax = null)
    {
        if (points.Count == 0)
        {
            return (string.Empty, string.Empty, axisMin ?? 0d, axisMax ?? 1d);
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

        // Fixed axis bounds (when configured on the sensor) override the data-derived
        // min/max so graphs stay comparable across time; otherwise auto-scale.
        var min = axisMin ?? scaleValues.Min();
        var max = axisMax ?? scaleValues.Max();
        if (max < min)
        {
            (min, max) = (max, min);
        }

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

        // SVG paths must use '.' as the decimal separator regardless of server culture - formatting with the
        // current (e.g. German) culture emits "55,05", which makes the path unparseable and the graph vanish.
        static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        var line = string.Join(" ", coords.Select((point, index) => $"{(index == 0 ? "M" : "L")} {F(point.X)} {F(point.Y)}"));
        var areaSegments = new List<string>
        {
            $"M 0 {F(height)}",
            $"L {F(coords[0].X)} {F(coords[0].Y)}"
        };
        areaSegments.AddRange(coords.Skip(1).Select(point => $"L {F(point.X)} {F(point.Y)}"));
        areaSegments.Add($"L {F(width)} {F(height)}");
        areaSegments.Add("Z");
        var area = string.Join(" ", areaSegments);

        return (line, area, min, max);
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
    string LineColor,
    double GraphMin,
    double GraphMax);
