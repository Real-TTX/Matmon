using System.Globalization;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class MapDisplayProvider
{
    private const double SparklineWidth = 100;
    private const double SparklineHeight = 40;

    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public MapDisplayProvider(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    public MapDisplayViewModel Build(MonitoringMap map)
    {
        var elements = _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
        var latest = _workspaceStore.GetLatestSensorObservations();

        MapDisplayTileViewModel[] BuildTiles(IEnumerable<MonitoringMapTile> source) => source
            .OrderBy(tile => tile.Y)
            .ThenBy(tile => tile.X)
            .Select(tile =>
            {
                if (!string.IsNullOrWhiteSpace(tile.TargetTag))
                {
                    return BuildTagAggregateTile(tile, latest);
                }

                elements.TryGetValue(tile.ElementId ?? Guid.Empty, out var element);
                return ResolveTileState(tile, element, latest);
            })
            .ToArray();

        var slides = map.EffectiveSlides()
            .Select(slide => new MapDisplaySlideViewModel(slide.Id, slide.Name, BuildTiles(slide.Tiles)))
            .ToArray();

        var firstTiles = slides.Length > 0 ? slides[0].Tiles : Array.Empty<MapDisplayTileViewModel>();
        return new MapDisplayViewModel(map, firstTiles, slides);
    }

    /// <summary>Resolve a single tile's live display view-model on demand - used by the map designer to show a
    /// tile's real value / state / graph the moment its target is picked, without needing a whole saved map.
    /// Mirrors the per-tile branch of <see cref="Build"/>.</summary>
    public MapDisplayTileViewModel ResolveTilePreview(MonitoringMapTile tile)
    {
        var latest = _workspaceStore.GetLatestSensorObservations();

        if (!string.IsNullOrWhiteSpace(tile.TargetTag))
        {
            return BuildTagAggregateTile(tile, latest);
        }

        var elements = _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
        elements.TryGetValue(tile.ElementId ?? Guid.Empty, out var element);
        return ResolveTileState(tile, element, latest);
    }

    private MapDisplayTileViewModel ResolveTileState(
        MonitoringMapTile tile,
        MonitoringElement? element,
        IReadOnlyDictionary<Guid, SensorObservation> latest)
    {
        if (tile.Kind == MonitoringMapTileKind.Text)
        {
            return CreateTile(tile, element, "ok", "Info", tile.Text ?? string.Empty, string.Empty, "Text", "list");
        }

        if (element is null)
        {
            return CreateTile(tile, element, "unknown", "No target", "No element assigned", string.Empty, KindLabel(tile.Kind), "warning");
        }

        if (element is SensorElement sensor)
        {
            return BuildSensorTile(tile, sensor, latest);
        }

        return BuildAggregateTile(tile, element, latest);
    }

    private MapDisplayTileViewModel BuildSensorTile(
        MonitoringMapTile tile,
        SensorElement sensor,
        IReadOnlyDictionary<Guid, SensorObservation> latest)
    {
        if (!latest.TryGetValue(sensor.Id, out var observation))
        {
            return CreateTile(tile, sensor, "unknown", "No data", sensor.SensorTypeKey, string.Empty, KindLabel(tile.Kind), "sensor");
        }

        var channel = observation.Channels.FirstOrDefault(candidate => candidate.IsDefault)
            ?? observation.Channels.FirstOrDefault(candidate => candidate.Value.HasValue);
        var value = FormatObservationValue(observation, channel);
        var subtitle = string.IsNullOrWhiteSpace(observation.Message)
            ? sensor.SensorTypeKey
            : observation.Message;
        var graph = tile.Kind == MonitoringMapTileKind.Graph
            ? BuildSparkline(sensor.Id)
            : Sparkline.Empty;
        var progressPercent = ResolveSensorProgressPercent(observation, channel);
        var progressLabel = progressPercent.HasValue
            ? $"{progressPercent.Value:0.#}%"
            : MonitoringStatePresentation.Label(observation.State);

        return new MapDisplayTileViewModel(
            tile,
            sensor,
            MonitoringStatePresentation.Key(observation.State),
            MonitoringStatePresentation.Label(observation.State),
            subtitle,
            value,
            KindLabel(tile.Kind),
            string.IsNullOrWhiteSpace(tile.IconKey) ? (tile.Kind == MonitoringMapTileKind.Graph ? "chart" : "sensor") : tile.IconKey!.Trim(),
            tile.GraphType == MonitoringMapTileGraphType.Smooth ? graph.SmoothLinePath : graph.LinePath,
            graph.AreaPath,
            graph.BarPath,
            MonitoringStatePresentation.Color(observation.State),
            progressPercent,
            progressLabel,
            ResolveEffectiveVisual(tile, sensor, channel));
    }

    private static MonitoringMapTileVisualType ResolveEffectiveVisual(
        MonitoringMapTile tile,
        SensorElement? sensor,
        SensorChannelValue? channel)
    {
        if (tile.VisualType != MonitoringMapTileVisualType.Auto)
        {
            return tile.VisualType;
        }

        // Auto: prefer the channel's configured visual (#31), else derive from its kind.
        var configured = sensor is not null && channel is not null
            ? MonitoringSettings.GetChannelVisual(sensor.Settings, channel.Key)
            : "auto";

        if (configured == "gauge")
        {
            return MonitoringMapTileVisualType.Gauge;
        }

        if (configured == "progress")
        {
            return MonitoringMapTileVisualType.ProgressBar;
        }

        if (configured is "value" or "graph")
        {
            return MonitoringMapTileVisualType.Card;
        }

        var kind = channel?.MeasurementKind ?? SensorMeasurementKind.Unknown;
        if (kind == SensorMeasurementKind.Unknown && channel is not null)
        {
            kind = SensorUnitConverter.GuessMeasurementKind(channel.Unit);
        }

        return kind == SensorMeasurementKind.Percent
            ? MonitoringMapTileVisualType.ProgressBar
            : MonitoringMapTileVisualType.Card;
    }

    private MapDisplayTileViewModel BuildAggregateTile(
        MonitoringMapTile tile,
        MonitoringElement element,
        IReadOnlyDictionary<Guid, SensorObservation> latest)
    {
        var sensors = Enumerate(element).OfType<SensorElement>().ToArray();
        return BuildAggregateFromSensors(tile, element, sensors, latest, IconForElement(element), element.Kind.ToString());
    }

    private MapDisplayTileViewModel BuildTagAggregateTile(
        MonitoringMapTile tile,
        IReadOnlyDictionary<Guid, SensorObservation> latest)
    {
        if (tile.Kind == MonitoringMapTileKind.Text)
        {
            return CreateTile(tile, null, "ok", "Info", tile.Text ?? string.Empty, string.Empty, "Text", "list");
        }

        var sensors = _workspaceStore.ResolveTargetSensors(MonitoringTargetResolver.TagPrefix + tile.TargetTag);
        return BuildAggregateFromSensors(tile, element: null, sensors, latest, "tag", $"# {tile.TargetTag}");
    }

    private MapDisplayTileViewModel BuildAggregateFromSensors(
        MonitoringMapTile tile,
        MonitoringElement? element,
        IReadOnlyList<SensorElement> sensors,
        IReadOnlyDictionary<Guid, SensorObservation> latest,
        string iconKey,
        string emptySubtitle)
    {
        var sensorStates = sensors
            .Select(sensor => latest.TryGetValue(sensor.Id, out var observation) ? observation.State : SensorState.Unknown)
            .ToArray();
        if (sensorStates.Length == 0)
        {
            return CreateTile(tile, element, "unknown", "No sensors", emptySubtitle, string.Empty, KindLabel(tile.Kind), iconKey);
        }

        var severity = sensorStates
            .Select(MonitoringStatePresentation.FromSensorState)
            .Aggregate(MonitoringSeverity.Ok, MonitoringStatePresentation.Max);
        var errors = sensorStates.Count(state => state is SensorState.Critical or SensorState.Disabled or SensorState.Unknown);
        var warnings = sensorStates.Count(state => state == SensorState.Warning);
        var healthy = sensorStates.Count(state => state is SensorState.Healthy or SensorState.Paused);
        var subtitle = errors > 0 || warnings > 0
            ? $"{errors} error / {warnings} warning / {sensorStates.Length} sensors"
            : $"{sensorStates.Length} sensors OK";
        var value = tile.Kind is MonitoringMapTileKind.Value or MonitoringMapTileKind.Status
            ? $"{healthy}/{sensorStates.Length}"
            : string.Empty;
        // sensorStates is guaranteed non-empty here (the Length == 0 case returned early above).
        double? progressPercent = Math.Clamp((double)healthy / sensorStates.Length * 100, 0, 100);
        var progressLabel = $"{healthy}/{sensorStates.Length} OK";

        return new MapDisplayTileViewModel(
            tile,
            element,
            MonitoringStatePresentation.Key(severity),
            MonitoringStatePresentation.Label(severity),
            subtitle,
            value,
            KindLabel(tile.Kind),
            iconKey,
            null,
            null,
            null,
            MonitoringStatePresentation.Color(severity),
            progressPercent,
            progressLabel,
            tile.VisualType == MonitoringMapTileVisualType.Auto ? MonitoringMapTileVisualType.ProgressBar : tile.VisualType);
    }

    private Sparkline BuildSparkline(Guid sensorId)
    {
        var points = _workspaceStore.GetSensorHistory(sensorId, TimeSpan.FromHours(24), 64)
            .Select(observation => observation.Value
                ?? observation.Channels.FirstOrDefault(channel => channel.Value.HasValue)?.Value)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        if (points.Length < 2)
        {
            return Sparkline.Empty;
        }

        var min = points.Min();
        var max = points.Max();
        var range = Math.Abs(max - min) < 0.00001 ? 1 : max - min;
        var step = SparklineWidth / Math.Max(1, points.Length - 1);
        var coordinates = points
            .Select((value, index) =>
            {
                var x = index * step;
                var y = SparklineHeight - ((value - min) / range * (SparklineHeight - 4)) - 2;
                return (X: x, Y: y);
            })
            .ToArray();

        var line = "M " + string.Join(" L ", coordinates.Select(point => FormatPoint(point.X, point.Y)));
        var smoothLine = BuildSmoothLine(coordinates);
        var area = $"{line} L {Format(SparklineWidth)} {Format(SparklineHeight)} L 0 {Format(SparklineHeight)} Z";
        var bars = string.Join(" ", coordinates.Select(point => $"M {Format(point.X)} {Format(point.Y)} V {Format(SparklineHeight)}"));
        return new Sparkline(line, smoothLine, area, bars);
    }

    private static MapDisplayTileViewModel CreateTile(
        MonitoringMapTile tile,
        MonitoringElement? element,
        string stateKey,
        string stateLabel,
        string subtitle,
        string value,
        string kindLabel,
        string iconKey)
    {
        return new MapDisplayTileViewModel(
            tile,
            element,
            stateKey,
            stateLabel,
            subtitle,
            value,
            kindLabel,
            string.IsNullOrWhiteSpace(tile.IconKey) ? iconKey : tile.IconKey!.Trim(),
            null,
            null,
            null,
            "#7c8eab",
            null,
            string.Empty,
            tile.VisualType == MonitoringMapTileVisualType.Auto ? MonitoringMapTileVisualType.Card : tile.VisualType);
    }

    private static double? ResolveSensorProgressPercent(SensorObservation observation, SensorChannelValue? channel)
    {
        var numeric = channel?.Value ?? observation.Value;
        if (numeric.HasValue && double.IsFinite(numeric.Value))
        {
            return Math.Clamp(numeric.Value, 0, 100);
        }

        return observation.State switch
        {
            SensorState.Healthy => 100,
            SensorState.Paused => 100,
            SensorState.Warning => 50,
            SensorState.Critical => 0,
            SensorState.Disabled => 0,
            SensorState.Unknown => null,
            _ => null
        };
    }

    private static string FormatObservationValue(SensorObservation observation, SensorChannelValue? channel)
    {
        if (channel?.Value is double channelValue)
        {
            return $"{channelValue:0.##} {channel.Unit}".Trim();
        }

        return observation.Value.HasValue
            ? $"{observation.Value.Value:0.##}"
            : string.Empty;
    }

    private static string KindLabel(MonitoringMapTileKind kind)
    {
        return kind switch
        {
            MonitoringMapTileKind.Element => "State",
            MonitoringMapTileKind.Status => "Summary",
            MonitoringMapTileKind.Value => "Value",
            MonitoringMapTileKind.Graph => "Graph",
            MonitoringMapTileKind.Text => "Text",
            _ => "Tile"
        };
    }

    private static string IconForElement(MonitoringElement element)
    {
        return element.Kind.ToString().ToLowerInvariant();
    }

    private static IEnumerable<MonitoringElement> Enumerate(MonitoringElement element)
    {
        yield return element;

        if (element is not MonitoringContainerElement container)
        {
            yield break;
        }

        foreach (var child in container.Children)
        {
            foreach (var descendant in Enumerate(child))
            {
                yield return descendant;
            }
        }
    }

    private static string FormatPoint(double x, double y)
    {
        return $"{Format(x)} {Format(y)}";
    }

    private static string BuildSmoothLine(IReadOnlyList<(double X, double Y)> coordinates)
    {
        if (coordinates.Count < 2)
        {
            return string.Empty;
        }

        var segments = new List<string>
        {
            "M " + FormatPoint(coordinates[0].X, coordinates[0].Y)
        };
        for (var index = 1; index < coordinates.Count; index++)
        {
            var previous = coordinates[index - 1];
            var current = coordinates[index];
            var midX = (previous.X + current.X) / 2;
            segments.Add($"Q {FormatPoint(previous.X, previous.Y)} {FormatPoint(midX, (previous.Y + current.Y) / 2)}");
        }

        var last = coordinates[^1];
        segments.Add("T " + FormatPoint(last.X, last.Y));
        return string.Join(" ", segments);
    }

    private static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private sealed record Sparkline(string? LinePath, string? SmoothLinePath, string? AreaPath, string? BarPath)
    {
        public static Sparkline Empty { get; } = new(null, null, null, null);
    }
}

public sealed record MapDisplayViewModel(
    MonitoringMap Map,
    IReadOnlyList<MapDisplayTileViewModel> Tiles,
    IReadOnlyList<MapDisplaySlideViewModel> Slides);

public sealed record MapDisplaySlideViewModel(
    Guid Id,
    string Name,
    IReadOnlyList<MapDisplayTileViewModel> Tiles);

public sealed record MapDisplayTileViewModel(
    MonitoringMapTile Tile,
    MonitoringElement? Element,
    string StateKey,
    string StateLabel,
    string Subtitle,
    string Value,
    string KindLabel,
    string IconKey,
    string? GraphLinePath,
    string? GraphAreaPath,
    string? GraphBarPath,
    string GraphColor,
    double? ProgressPercent,
    string ProgressLabel,
    MonitoringMapTileVisualType EffectiveVisualType);
