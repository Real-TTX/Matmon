using System.Collections.Concurrent;
using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;

namespace Matmon.Host.Services;

public sealed class MapDisplayProvider
{
    private const double SparklineWidth = 100;
    private const double SparklineHeight = 40;

    /// <summary>Default lifetime of a cached SLA figure when the tile does not set its own
    /// <see cref="MonitoringMapTile.RefreshSeconds"/> - see ResolveUptime. A multi-day figure does not move
    /// between two page loads.</summary>
    private static readonly TimeSpan SlaCacheTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (DateTimeOffset ComputedUtc, UptimeSummary Summary)> _slaCache = new();

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
            .OrderBy(tile => tile.Row)
            .ThenBy(tile => tile.Column)
            .Select(tile =>
            {
                if (IsCollectionKind(tile.Kind))
                {
                    return BuildCollectionTile(tile, elements, latest);
                }

                if (!string.IsNullOrWhiteSpace(tile.TargetTag))
                {
                    return BuildTagAggregateTile(tile, latest);
                }

                elements.TryGetValue(tile.ElementId ?? Guid.Empty, out var element);
                return ResolveTileState(tile, element, latest);
            })
            .ToArray();

        var slides = map.EffectiveSlides()
            .Select(slide => new MapDisplaySlideViewModel(slide, BuildTiles(slide.Tiles)))
            .ToArray();

        var firstTiles = slides.Length > 0 ? slides[0].Tiles : [];
        return new MapDisplayViewModel(map, firstTiles, slides);
    }

    /// <summary>Resolve a single tile's live display view-model on demand - used by the map designer to show a
    /// tile's real value / state / graph the moment its target is picked, without needing a whole saved map.
    /// Mirrors the per-tile branch of <see cref="Build"/>.</summary>
    public MapDisplayTileViewModel ResolveTilePreview(MonitoringMapTile tile)
    {
        var latest = _workspaceStore.GetLatestSensorObservations();

        if (IsCollectionKind(tile.Kind))
        {
            return BuildCollectionTile(tile, _workspaceStore.GetAllElements().ToDictionary(element => element.Id), latest);
        }

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

        var channel = ResolveTileChannel(observation, tile);
        var value = FormatObservationValue(observation, channel);
        var subtitle = string.IsNullOrWhiteSpace(observation.Message)
            ? sensor.SensorTypeKey
            : observation.Message;
        // The line plots the SAME channel the tile's number shows - it used to plot the observation's bare
        // value, so a graph tile pointed at "memory" drew CPU under a memory reading.
        var graph = tile.Kind == MonitoringMapTileKind.Graph
            ? BuildSparkline(sensor.Id, channel?.Key, MonitoringMapTile.NormalizeGraphWindowHours(tile.GraphWindowHours))
            : Sparkline.Empty;
        var progressPercent = ResolveSensorProgressPercent(tile, channel);
        // The dial's big figure is the reading itself, so this caption says what the dial is SCALED to -
        // without it a needle two thirds round is unreadable, because nothing on the tile says two thirds of
        // what. It falls back to the state only when there is no dial at all.
        var progressLabel = progressPercent.HasValue
            ? FormatGaugeScale(tile, channel)
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

    /// <summary>
    /// A single sensor's trend over the tile's window (<see cref="MonitoringMapTile.GraphWindowHours"/>),
    /// placed by TIME - the same reading path as the multi-graph. It used to be the newest 64 readings spaced
    /// evenly, which is "the last half hour" for a 30 s ping and "the last five hours" for a 5 min sensor,
    /// with nothing on the tile saying which. The Y range stays the line's own (a single line has nothing to
    /// be compared against), padded so a flat line sits in the middle.
    /// </summary>
    private Sparkline BuildSparkline(Guid sensorId, string? channelKey, int windowHours)
    {
        var window = TimeSpan.FromHours(windowHours);
        var points = ReadSeries(sensorId, channelKey, DateTimeOffset.UtcNow - window, window);
        if (points.Count < 2)
        {
            return Sparkline.Empty;
        }

        var min = points.Min(point => point.Value);
        var max = points.Max(point => point.Value);
        var range = Math.Abs(max - min) < 0.00001 ? 1 : max - min;
        var coordinates = points
            .Select(point => (
                X: Math.Clamp(point.X, 0, 1) * SparklineWidth,
                Y: SparklineHeight - ((point.Value - min) / range * (SparklineHeight - 4)) - 2))
            .ToArray();

        var line = "M " + string.Join(" L ", coordinates.Select(point => FormatPoint(point.X, point.Y)));
        var smoothLine = BuildSmoothLine(coordinates);
        // Closed under the line's OWN span: with a real time axis the first reading need not sit at the left
        // edge (a sensor added an hour ago), and closing at 0..100 painted a wedge where there was no data.
        var area = $"{line} L {Format(coordinates[^1].X)} {Format(SparklineHeight)} L {Format(coordinates[0].X)} {Format(SparklineHeight)} Z";
        var bars = string.Join(" ", coordinates.Select(point => $"M {Format(point.X)} {Format(point.Y)} V {Format(SparklineHeight)}"));
        return new Sparkline(line, smoothLine, area, bars);
    }

    // --- Collection widgets (list / alert feed / SLA / clock / heading) --------------------------------
    // These differ from the tiles above in that their content is a LIST or an aggregate over a window rather
    // than one element's current state, which is why MapDisplayTileViewModel grew optional Rows/Sla instead
    // of a parallel view-model hierarchy.

    private static bool IsCollectionKind(MonitoringMapTileKind kind) =>
        kind is MonitoringMapTileKind.SensorList
             or MonitoringMapTileKind.AlertFeed
             or MonitoringMapTileKind.Sla
             or MonitoringMapTileKind.Clock
             or MonitoringMapTileKind.Heading
             or MonitoringMapTileKind.Image
             or MonitoringMapTileKind.GeoMap
             or MonitoringMapTileKind.MultiGraph;

    /// <summary>The tile's target as the single token <see cref="IMonitoringWorkspaceStore.ResolveTargetSensors"/>
    /// understands - an element id or "tag:name". Null when the tile has no target at all, which for an alert
    /// feed means "the whole workspace" rather than "nothing".</summary>
    private static string? TargetToken(MonitoringMapTile tile) =>
        !string.IsNullOrWhiteSpace(tile.TargetTag)
            ? MonitoringTargetResolver.TagPrefix + tile.TargetTag
            : tile.ElementId is { } id ? id.ToString() : null;

    private MapDisplayTileViewModel BuildCollectionTile(
        MonitoringMapTile tile,
        IReadOnlyDictionary<Guid, MonitoringElement> elements,
        IReadOnlyDictionary<Guid, SensorObservation> latest)
    {
        elements.TryGetValue(tile.ElementId ?? Guid.Empty, out var element);
        return tile.Kind switch
        {
            MonitoringMapTileKind.Heading => CreateTile(tile, element, "ok", "Heading", tile.Text ?? string.Empty, string.Empty, "Heading", "list"),
            MonitoringMapTileKind.Clock => CreateTile(tile, element, "ok", "Clock", string.Empty, string.Empty, "Clock", "clock"),
            MonitoringMapTileKind.AlertFeed => BuildAlertFeedTile(tile, element),
            MonitoringMapTileKind.Image => BuildImageTile(tile, element, latest, geo: false),
            MonitoringMapTileKind.GeoMap => BuildImageTile(tile, element, latest, geo: true),
            MonitoringMapTileKind.Sla => BuildSlaTile(tile, element),
            MonitoringMapTileKind.MultiGraph => BuildMultiGraphTile(tile, element, latest),
            _ => BuildSensorListTile(tile, element, latest)
        };
    }

    /// <summary>At most this many lines. Each one is its own history query, and past a handful of lines a
    /// chart stops answering "which is moving?" and starts being a plate of spaghetti - which is the only
    /// question this widget exists to answer.</summary>
    private const int MultiGraphMaxSeries = 8;

    /// <summary>Deliberately fixed and deterministic by index, so a series keeps its colour from one render
    /// to the next - a legend whose colours reshuffle is worse than no legend. Picked to stay apart on a dark
    /// wall at distance, including for the common red/green confusions.</summary>
    private static readonly string[] MultiGraphPalette =
    [
        "#4FB3FF", "#F2B138", "#7BD88F", "#E36D9B", "#B79BFF", "#4FD8D2", "#FF8A5B", "#9FB3C8"
    ];

    /// <summary>
    /// Several sensors as lines in one chart - "which of these machines is busy?". Three things make that
    /// question answerable, and each was once wrong:
    /// <list type="bullet">
    /// <item>ONE scale for every line (<see cref="SparklineGeometry.NiceScale"/>): normalised per series they
    /// all look the same and the spike is invisible. It is rounded to readable bounds and labelled, so the
    /// chart says how busy, not just which.</item>
    /// <item>the SAME measurement from every sensor, even when their types name it differently - a channel
    /// FAMILY (<see cref="ChannelFamilies"/>) picks each sensor's own CPU / memory / ... channel. An exact
    /// key still works for a set of identical sensors.</item>
    /// <item>X by TIME over the real window: the old chart spaced the newest 64 readings evenly and labelled
    /// them "last 24h" - for a 30 s ping that was the last half hour, and a sensor that only started an hour
    /// ago was stretched over the whole width.</item>
    /// </list>
    /// </summary>
    private MapDisplayTileViewModel BuildMultiGraphTile(
        MonitoringMapTile tile,
        MonitoringElement? element,
        IReadOnlyDictionary<Guid, SensorObservation> latest)
    {
        var sensors = ResolveMultiTargetSensors(tile);
        if (sensors.Count == 0)
        {
            return CreateTile(tile, element, "unknown", "No target", "No sensors under these targets", string.Empty, KindLabel(tile.Kind), "chart");
        }

        var family = ChannelFamilies.Parse(tile.ListChannelKey);
        var exactKey = family is null && !string.IsNullOrWhiteSpace(tile.ListChannelKey) ? tile.ListChannelKey.Trim() : null;
        var windowHours = MonitoringMapTile.NormalizeGraphWindowHours(tile.GraphWindowHours);
        var window = TimeSpan.FromHours(windowHours);
        var endUtc = DateTimeOffset.UtcNow;
        var startUtc = endUtc - window;
        var limit = Math.Clamp(tile.ListLimit, 1, MultiGraphMaxSeries);

        // Filter FIRST, limit after: a host carries a ping, a health sensor, a disk sensor... and taking the
        // first N by name before asking which of them has the channel regularly cut the one line that did.
        var collected = sensors
            .OrderBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Select(sensor =>
            {
                latest.TryGetValue(sensor.Id, out var observation);
                // The family's channel from the LATEST reading - or, when that one carries none (a timeout, a
                // failed run: no channels at all), from the newest reading in the window that does. Otherwise
                // one failed poll removed a machine from the chart although its whole day is right there.
                var channel = family is null
                    ? null
                    : (observation is null ? null : ChannelFamilies.Pick(observation.Channels, family))
                        ?? ReadWindow(sensor.Id, window)
                            .Reverse()
                            .Select(candidate => ChannelFamilies.Pick(candidate.Channels, family))
                            .FirstOrDefault(candidate => candidate is not null);
                var key = family is not null ? channel?.Key : exactKey;
                var unit = family?.Unit
                    ?? (exactKey is not null
                        ? observation?.Channels.FirstOrDefault(candidate => string.Equals(candidate.Key, exactKey, StringComparison.OrdinalIgnoreCase))?.Unit
                        : null);
                IReadOnlyList<(double X, double Value)> points = family is not null && key is null
                    ? []
                    : ReadSeries(sensor.Id, key, startUtc, window);
                return (sensor, key, unit, points);
            })
            .Where(entry => entry.points.Count >= 2)
            .Take(limit)
            .ToArray();

        if (collected.Length == 0)
        {
            var missing = family is not null
                ? $"None of these sensors reports {family.Label.ToLowerInvariant()} yet"
                : exactKey is not null
                    ? $"No history on channel \"{exactKey}\" in the last {WindowLabel(windowHours)}"
                    : $"No history in the last {WindowLabel(windowHours)}";
            return CreateTile(tile, element, "unknown", "No data", missing, string.Empty, KindLabel(tile.Kind), "chart");
        }

        // One unit for the axis: the family's, or the exact channel's when every line agrees on it.
        var units = collected
            .Select(entry => entry.unit?.Trim())
            .Where(unit => !string.IsNullOrEmpty(unit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var axisUnit = family?.Unit ?? (units.Length == 1 ? units[0] : null);

        var (rawMin, rawMax) = SparklineGeometry.Scale(collected.Select(entry => (IReadOnlyList<double>)entry.points.Select(point => point.Value).ToArray()));
        var (min, max) = SparklineGeometry.NiceScale(rawMin, rawMax, percent: axisUnit == "%");

        var labels = SeriesLabels(collected.Select(entry => entry.sensor).ToArray());

        // Biggest last reading first: the legend is read top-down, and the line you are looking for is
        // almost always the one that is currently highest. Colours stay tied to the (name-ordered) index.
        var series = collected
            .Select((entry, index) => new { entry, Color = MultiGraphPalette[index % MultiGraphPalette.Length], Label = labels[index] })
            .OrderByDescending(item => item.entry.points[^1].Value)
            .Select(item => new MapGraphSeriesDto(
                item.Label,
                item.Color,
                SparklineGeometry.TimeLine(item.entry.points, min, max),
                FormatWithUnit(item.entry.points[^1].Value, item.entry.unit ?? axisUnit),
                string.Join(";", item.entry.points.Select(point =>
                    $"{point.X.ToString("0.####", CultureInfo.InvariantCulture)},{Format(point.Value)}"))))
            .ToArray();

        var measured = family?.Label ?? exactKey;
        var subtitle = measured is null
            ? $"{series.Length} sensors · last {WindowLabel(windowHours)}"
            : $"{series.Length} sensors · {measured} · last {WindowLabel(windowHours)}";
        var axis = new MapGraphAxisDto(
            FormatWithUnit(max, axisUnit),
            FormatWithUnit((min + max) / 2, axisUnit),
            FormatWithUnit(min, axisUnit),
            axisUnit,
            startUtc.ToUnixTimeMilliseconds(),
            endUtc.ToUnixTimeMilliseconds(),
            WindowLabel(windowHours));

        return CreateTile(tile, element, "ok", "Chart", subtitle, string.Empty, KindLabel(tile.Kind), "chart")
            with { Series = series, Axis = axis };
    }

    /// <summary>
    /// What each line is called. The chart compares MACHINES, and every machine's health sensor has the same
    /// name - a legend reading "Windows Health, Windows Health, Synology Health" names nothing. So a line is
    /// named after its machine: the host it sits under, else the sensor's own target, else its parent. The
    /// sensor's name is added only where that is still ambiguous (two sensors on one host), and a number only
    /// where even that repeats.
    /// </summary>
    private string[] SeriesLabels(IReadOnlyList<SensorElement> sensors)
    {
        var machines = sensors.Select(MachineName).ToArray();
        var labels = sensors
            .Select((sensor, index) => machines.Count(name => string.Equals(name, machines[index], StringComparison.OrdinalIgnoreCase)) > 1
                ? $"{machines[index]} · {sensor.Name}"
                : machines[index])
            .ToArray();

        return labels
            .Select((label, index) =>
            {
                var before = labels.Take(index).Count(other => string.Equals(other, label, StringComparison.OrdinalIgnoreCase));
                return before == 0 ? label : $"{label} ({before + 1})";
            })
            .ToArray();
    }

    private string MachineName(SensorElement sensor)
    {
        // Walk up to the nearest host: that IS the machine. Bounded, in case of a malformed tree.
        var parentId = sensor.ParentId;
        MonitoringElement? parent = null;
        for (var depth = 0; parentId is Guid id && depth < 16; depth++)
        {
            var element = _workspaceStore.FindElement(id);
            parent ??= element;
            if (element is HostElement host)
            {
                return host.Name;
            }

            parentId = element?.ParentId;
        }

        return !string.IsNullOrWhiteSpace(sensor.Target) ? sensor.Target : parent?.Name ?? sensor.Name;
    }

    private static string WindowLabel(int hours) => hours switch
    {
        72 => "3 days",
        _ => $"{hours} h"
    };

    private static string FormatWithUnit(double value, string? unit)
    {
        var number = Math.Abs(value) >= 100 ? value.ToString("0", CultureInfo.InvariantCulture)
            : Math.Abs(value) >= 10 ? value.ToString("0.#", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(unit) ? number : unit == "%" ? $"{number}%" : $"{number} {unit}";
    }

    /// <summary>Every sensor behind the tile's main target AND its extra ones, in the order the targets were
    /// given and deduplicated - the same host picked twice, or a host inside a folder that is also listed,
    /// must not become two identical lines.</summary>
    private IReadOnlyList<SensorElement> ResolveMultiTargetSensors(MonitoringMapTile tile)
    {
        var seen = new HashSet<Guid>();
        var result = new List<SensorElement>();

        foreach (var token in new[] { TargetToken(tile) }.Concat(tile.TargetTokens))
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            foreach (var sensor in _workspaceStore.ResolveTargetSensors(token))
            {
                if (seen.Add(sensor.Id))
                {
                    result.Add(sensor);
                }
            }
        }

        return result;
    }

    /// <summary>At most this many points per line: enough for a smooth line at wallboard size, few enough
    /// that eight lines stay a small payload on every live poll.</summary>
    private const int MultiGraphPointsPerLine = 120;

    /// <summary>
    /// One sensor's readings over the window as (position in window 0..1, value), averaged into
    /// <see cref="MultiGraphPointsPerLine"/> time buckets. The WHOLE window is read - the old query asked for
    /// the newest 64 observations, which is not a time window at all. Raw history is cached per sensor and
    /// window for a minute: a wallboard polls every 30 s, and three days of a 30 s ping is thousands of rows.
    /// With a channel key it reads that channel; without one the observation's own value.
    /// </summary>
    private IReadOnlyList<(double X, double Value)> ReadSeries(Guid sensorId, string? channelKey, DateTimeOffset startUtc, TimeSpan window)
    {
        var windowMs = window.TotalMilliseconds;
        var raw = ReadWindow(sensorId, window)
            .Select(observation => (
                observation.TimestampUtc,
                Value: channelKey is null
                    ? observation.Value ?? observation.Channels.FirstOrDefault(channel => channel.Value.HasValue && !channel.IsVirtual)?.Value
                    : observation.Channels.FirstOrDefault(channel =>
                        string.Equals(channel.Key, channelKey, StringComparison.OrdinalIgnoreCase))?.Value))
            .Where(entry => entry.Value.HasValue && entry.TimestampUtc >= startUtc)
            .Select(entry => (X: (entry.TimestampUtc - startUtc).TotalMilliseconds / windowMs, Value: entry.Value!.Value))
            .OrderBy(point => point.X)
            .ToArray();

        return SparklineGeometry.Downsample(raw, MultiGraphPointsPerLine);
    }

    private readonly ConcurrentDictionary<(Guid SensorId, int WindowHours), (DateTimeOffset ReadUtc, IReadOnlyList<SensorObservation> Observations)> _historyCache = new();

    private IReadOnlyList<SensorObservation> ReadWindow(Guid sensorId, TimeSpan window)
    {
        var key = (sensorId, (int)window.TotalHours);
        var now = DateTimeOffset.UtcNow;
        if (_historyCache.TryGetValue(key, out var cached) && now - cached.ReadUtc < TimeSpan.FromMinutes(1))
        {
            return cached.Observations;
        }

        var observations = _workspaceStore.GetSensorHistory(sensorId, window);
        if (_historyCache.Count > 512)
        {
            _historyCache.Clear();
        }

        _historyCache[key] = (now, observations);
        return observations;
    }

    private MapDisplayTileViewModel BuildSensorListTile(
        MonitoringMapTile tile,
        MonitoringElement? element,
        IReadOnlyDictionary<Guid, SensorObservation> latest)
    {
        var sensors = _workspaceStore.ResolveTargetSensors(TargetToken(tile));
        if (sensors.Count == 0)
        {
            return CreateTile(tile, element, "unknown", "No target", "No sensors under this target", string.Empty, KindLabel(tile.Kind), "list");
        }

        var limit = Math.Clamp(tile.ListLimit, 1, 50);
        var entries = sensors
            .Select(sensor =>
            {
                latest.TryGetValue(sensor.Id, out var observation);
                var channel = PickChannel(observation, tile.ListChannelKey);
                return new
                {
                    Sensor = sensor,
                    Observation = observation,
                    Channel = channel,
                    Numeric = channel?.Value ?? observation?.Value,
                    State = observation?.State ?? SensorState.Unknown
                };
            })
            .ToArray();

        // A value-ranked list must not let "no reading" win the top spot, so a missing number sorts to the
        // far end in BOTH directions rather than being treated as zero.
        var ordered = tile.ListMode switch
        {
            MonitoringMapListMode.TopValue => entries
                .OrderByDescending(entry => entry.Numeric ?? double.MinValue)
                .ThenBy(entry => entry.Sensor.Name, StringComparer.OrdinalIgnoreCase),
            MonitoringMapListMode.BottomValue => entries
                .OrderBy(entry => entry.Numeric ?? double.MaxValue)
                .ThenBy(entry => entry.Sensor.Name, StringComparer.OrdinalIgnoreCase),
            MonitoringMapListMode.Alphabetical => entries
                .OrderBy(entry => entry.Sensor.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Sensor.Id),
            _ => entries
                .OrderByDescending(entry => (int)MonitoringStatePresentation.FromSensorState(entry.State))
                .ThenBy(entry => entry.Sensor.Name, StringComparer.OrdinalIgnoreCase)
        };

        var rows = ordered
            .Take(limit)
            .Select(entry => new MapTileRowDto(
                entry.Sensor.Name,
                entry.Channel?.Label ?? entry.Sensor.SensorTypeKey,
                entry.Observation is null ? string.Empty : FormatObservationValue(entry.Observation, entry.Channel),
                MonitoringStatePresentation.Key(MonitoringStatePresentation.FromSensorState(entry.State)),
                null))
            .ToArray();

        var severity = entries
            .Select(entry => MonitoringStatePresentation.FromSensorState(entry.State))
            .Aggregate(MonitoringSeverity.Ok, MonitoringStatePresentation.Max);
        var subtitle = entries.Length > rows.Length
            ? $"Top {rows.Length} of {entries.Length} sensors"
            : $"{entries.Length} sensors";

        return CreateTile(tile, element, MonitoringStatePresentation.Key(severity), MonitoringStatePresentation.Label(severity),
            subtitle, string.Empty, KindLabel(tile.Kind), "list", rows: rows);
    }

    private MapDisplayTileViewModel BuildAlertFeedTile(MonitoringMapTile tile, MonitoringElement? element)
    {
        var limit = Math.Clamp(tile.ListLimit, 1, 50);
        var token = TargetToken(tile);
        // No target = the whole workspace. Scoping to a target means "this element and everything under it",
        // so the element's own id goes in alongside its sensors - a folder can carry an alert itself.
        HashSet<Guid>? scope = null;
        if (token is not null)
        {
            scope = _workspaceStore.ResolveTargetSensors(token).Select(sensor => sensor.Id).ToHashSet();
            if (tile.ElementId is { } elementId)
            {
                scope.Add(elementId);
            }
        }

        var alerts = _workspaceStore.Workspace.Alerts
            .Where(alert => alert.IsActive)
            .Where(alert => scope is null || scope.Contains(alert.ElementId))
            .OrderByDescending(alert => (int)MonitoringStatePresentation.FromSensorState(alert.State))
            .ThenByDescending(alert => alert.LastSeenUtc)
            .Take(limit)
            .ToArray();

        var rows = alerts
            .Select(alert => new MapTileRowDto(
                alert.ElementName,
                alert.Message,
                alert.IsAcknowledged ? "ack" : null,
                MonitoringStatePresentation.Key(MonitoringStatePresentation.FromSensorState(alert.State)),
                FormatRelative(alert.LastSeenUtc)))
            .ToArray();

        if (rows.Length == 0)
        {
            return CreateTile(tile, element, "ok", "All clear", "No open alerts", string.Empty, KindLabel(tile.Kind), "bell", rows: rows);
        }

        var severity = alerts
            .Select(alert => MonitoringStatePresentation.FromSensorState(alert.State))
            .Aggregate(MonitoringSeverity.Ok, MonitoringStatePresentation.Max);
        return CreateTile(tile, element, MonitoringStatePresentation.Key(severity), MonitoringStatePresentation.Label(severity),
            $"{rows.Length} open", string.Empty, KindLabel(tile.Kind), "bell", rows: rows);
    }

    private MapDisplayTileViewModel BuildSlaTile(MonitoringMapTile tile, MonitoringElement? element)
    {
        var days = Math.Clamp(tile.SlaWindowDays, 1, 365);
        var sensors = _workspaceStore.ResolveTargetSensors(TargetToken(tile));
        if (sensors.Count == 0)
        {
            return CreateTile(tile, element, "unknown", "No target", "No sensors under this target", string.Empty, KindLabel(tile.Kind), "chart");
        }

        var summary = ResolveUptime(tile, sensors, days);
        var label = sensors.Count == 1 ? $"{days}d uptime" : $"{days}d uptime, {sensors.Count} sensors";
        var sla = new MapSlaDto(summary.Percent, days, summary.StateSamples, label);

        if (!summary.HasData)
        {
            return CreateTile(tile, element, "unknown", "No history", $"No statistics in the last {days} days", string.Empty,
                KindLabel(tile.Kind), "chart", sla: sla);
        }

        // Thresholds are presentation only - an SLA tile reports history, it does not raise anything, so this
        // deliberately does not touch the sensor's own alert state.
        var percent = summary.Percent!.Value;
        var stateKey = percent >= 99.5 ? "ok" : percent >= 95 ? "warning" : "error";
        var stateLabel = percent >= 99.5 ? "On target" : percent >= 95 ? "Degraded" : "Breached";

        return new MapDisplayTileViewModel(
            tile,
            element,
            stateKey,
            stateLabel,
            label,
            $"{percent:0.##} %",
            KindLabel(tile.Kind),
            string.IsNullOrWhiteSpace(tile.IconKey) ? "chart" : tile.IconKey!.Trim(),
            null, null, null, "#7c8eab",
            Math.Clamp(percent, 0, 100),
            $"{summary.StateSamples} samples",
            tile.VisualType == MonitoringMapTileVisualType.Auto ? MonitoringMapTileVisualType.ProgressBar : tile.VisualType,
            null,
            sla);
    }

    /// <summary>
    /// Uptime for an SLA tile, cached per (tile, window, sensor set) for <see cref="SlaCacheTtl"/>.
    /// This is the ONE query on a wallboard that scales with the number of sensors behind a target - a tag
    /// matching 200 sensors is 200 statistics reads - and a multi-day uptime figure simply does not move
    /// between two page loads, so re-reading it every render would be pure waste. Everything else a tile
    /// shows comes from the latest-observation cache and is already paid for.
    /// </summary>
    private UptimeSummary ResolveUptime(MonitoringMapTile tile, IReadOnlyList<SensorElement> sensors, int days)
    {
        // The tile's own RefreshSeconds wins when set - that field exists precisely so an expensive or
        // slow-changing widget can be told how stale its answer may be. It is part of the KEY as well as the
        // comparison, so shortening it takes effect immediately instead of waiting out the old entry.
        var ttl = tile.RefreshSeconds > 0 ? TimeSpan.FromSeconds(tile.RefreshSeconds) : SlaCacheTtl;
        var key = $"{tile.Id}|{days}|{sensors.Count}|{(int)ttl.TotalSeconds}";
        var now = DateTimeOffset.UtcNow;
        if (_slaCache.TryGetValue(key, out var cached) && now - cached.ComputedUtc < ttl)
        {
            return cached.Summary;
        }

        var fromUtc = now.AddDays(-days);
        var summary = SensorUptime.Combine(sensors.Select(sensor =>
            SensorUptime.FromBuckets(_workspaceStore.GetSensorStatistics(sensor.Id, fromUtc), fromUtc)));

        // Bounded so a workspace that churns through tiles cannot grow this without limit.
        if (_slaCache.Count > 256)
        {
            _slaCache.Clear();
        }

        _slaCache[key] = (now, summary);
        return summary;
    }

    private static SensorChannelValue? PickChannel(SensorObservation? observation, string? channelKey)
    {
        if (observation is null)
        {
            return null;
        }

        // A family ranks a mixed list ("top CPU across these machines") by each sensor's own channel for it.
        if (ChannelFamilies.Parse(channelKey) is { } family)
        {
            return ChannelFamilies.Pick(observation.Channels, family);
        }

        if (!string.IsNullOrWhiteSpace(channelKey))
        {
            return observation.Channels.FirstOrDefault(channel =>
                string.Equals(channel.Key, channelKey, StringComparison.OrdinalIgnoreCase));
        }

        return observation.Channels.FirstOrDefault(channel => channel.IsDefault)
            ?? observation.Channels.FirstOrDefault(channel => !channel.IsVirtual);
    }

    private static string FormatRelative(DateTimeOffset timestampUtc)
    {
        var age = DateTimeOffset.UtcNow - timestampUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes}m";
        }

        return age.TotalDays < 1 ? $"{(int)age.TotalHours}h" : $"{(int)age.TotalDays}d";
    }

    private MapDisplayTileViewModel BuildImageTile(
        MonitoringMapTile tile,
        MonitoringElement? element,
        IReadOnlyDictionary<Guid, SensorObservation> latest,
        bool geo)
    {
        var pins = BuildPins(tile, latest, geo);
        var severity = pins
            .Select(pin => pin.Severity)
            .DefaultIfEmpty(MonitoringSeverity.Ok)
            .Aggregate(MonitoringSeverity.Ok, MonitoringStatePresentation.Max);

        var subtitle = pins.Count switch
        {
            0 when geo => "No locations placed",
            0 => tile.ImageAssetId is null ? "No image uploaded" : "No pins placed",
            1 => "1 pin",
            _ => $"{pins.Count} pins"
        };

        return CreateTile(tile, element,
            MonitoringStatePresentation.Key(severity),
            MonitoringStatePresentation.Label(severity),
            subtitle, string.Empty, KindLabel(tile.Kind), geo ? "network" : "square",
            pins: pins);
    }

    /// <summary>
    /// Resolves every pin's target to a state, and its position to a FRACTION of the tile. An image pin is
    /// already stored as a fraction; a geo pin is projected from lat/lon by
    /// <see cref="MonitoringMapGeoProjection"/>. Both end up in the same 0..1 space, so the renderer has one
    /// case to handle and a pin stays put at any rendered size.
    /// </summary>
    private IReadOnlyList<MapPinDto> BuildPins(
        MonitoringMapTile tile,
        IReadOnlyDictionary<Guid, SensorObservation> latest,
        bool geo)
    {
        if (tile.Pins.Count == 0)
        {
            return [];
        }

        var pins = new List<MapPinDto>(tile.Pins.Count);
        foreach (var pin in tile.Pins)
        {
            double x;
            double y;
            if (geo)
            {
                // A geo pin without coordinates has nowhere to go - dropping it beats stacking every
                // unplaced pin in the top-left corner of the Atlantic.
                if (!MonitoringMapGeoProjection.IsValidLatitude(pin.Latitude) ||
                    !MonitoringMapGeoProjection.IsValidLongitude(pin.Longitude))
                {
                    continue;
                }

                (x, y) = MonitoringMapGeoProjection.ToFraction(pin.Latitude!.Value, pin.Longitude!.Value);
            }
            else
            {
                x = Math.Clamp(pin.X, 0, 1);
                y = Math.Clamp(pin.Y, 0, 1);
            }

            var sensors = _workspaceStore.ResolveTargetSensors(pin.TargetToken);
            var states = sensors
                .Select(sensor => latest.TryGetValue(sensor.Id, out var observation) ? observation.State : SensorState.Unknown)
                .ToArray();
            var severity = states.Length == 0
                ? MonitoringSeverity.Ok
                : states.Select(MonitoringStatePresentation.FromSensorState).Aggregate(MonitoringSeverity.Ok, MonitoringStatePresentation.Max);

            // An unplaced/untargeted pin reads as "unknown" rather than as a healthy green dot, which would
            // be a lie on a wallboard.
            var tone = sensors.Count == 0 ? "unknown" : MonitoringStatePresentation.Key(severity);

            string? value = null;
            if (pin.Style == MonitoringMapPinStyle.Tile && sensors.Count > 0)
            {
                value = sensors.Count == 1 && latest.TryGetValue(sensors[0].Id, out var single)
                    ? FormatObservationValue(single, PickChannel(single, null))
                    : $"{states.Count(state => state is SensorState.Healthy or SensorState.Paused)}/{states.Length}";
            }

            pins.Add(new MapPinDto(
                string.IsNullOrWhiteSpace(pin.Label) ? sensors.FirstOrDefault()?.Name ?? "Pin" : pin.Label!,
                pin.ShowLabel,
                tone,
                sensors.Count == 0 ? MonitoringSeverity.Ok : severity,
                Math.Round(x * 100, 3),
                Math.Round(y * 100, 3),
                pin.Style == MonitoringMapPinStyle.Tile ? "tile" : "dot",
                value));
        }

        return pins;
    }

    private static MapDisplayTileViewModel CreateTile(
        MonitoringMapTile tile,
        MonitoringElement? element,
        string stateKey,
        string stateLabel,
        string subtitle,
        string value,
        string kindLabel,
        string iconKey,
        IReadOnlyList<MapTileRowDto>? rows = null,
        MapSlaDto? sla = null,
        IReadOnlyList<MapPinDto>? pins = null)
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
            tile.VisualType == MonitoringMapTileVisualType.Auto ? MonitoringMapTileVisualType.Card : tile.VisualType,
            rows,
            sla,
            pins);
    }

    /// <summary>The channel a tile reads: the one it was pointed at, else the sensor's default, else the
    /// first with a number. The VIRTUAL sensorState channel is never picked - it is a 1/0 health flag, not a
    /// reading, and letting it win meant a failing sensor rendered a great big "0" as though that were its
    /// measurement.</summary>
    private static SensorChannelValue? ResolveTileChannel(SensorObservation observation, MonitoringMapTile tile)
    {
        if (!string.IsNullOrWhiteSpace(tile.ChannelKey))
        {
            var requested = observation.Channels.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, tile.ChannelKey, StringComparison.OrdinalIgnoreCase));
            if (requested is not null)
            {
                return requested;
            }
        }

        return observation.Channels.FirstOrDefault(candidate => candidate.IsDefault && !candidate.IsVirtual)
            ?? observation.Channels.FirstOrDefault(candidate => !candidate.IsVirtual && candidate.Value.HasValue);
    }

    private static bool IsPercentChannel(SensorChannelValue? channel) =>
        channel?.Unit is { } unit && unit.Trim() is "%" or "percent";

    /// <summary>Where the needle sits, 0..100 - or null for "do not draw a dial".
    /// A dial needs a scale, and only two things provide one: an explicit <see cref="MonitoringMapTile.GaugeMin"/>
    /// / <see cref="MonitoringMapTile.GaugeMax"/>, or a channel that is already a percentage. The old code
    /// clamped ANY number into 0..100 and called it a percent, so 12 ms of latency drew a dial at 12 % and
    /// 4 500 ms drew a full one - and with no numeric channel at all it invented a position from the sensor
    /// state, painting a needle that measured nothing. Both are made-up readings; on a wall display a made-up
    /// reading is worse than an empty one.</summary>
    private static double? ResolveSensorProgressPercent(MonitoringMapTile tile, SensorChannelValue? channel)
    {
        if (channel?.Value is not { } numeric || !double.IsFinite(numeric))
        {
            return null;
        }

        var (min, max) = ResolveGaugeScale(tile, channel);
        if (max is null || min is null || max <= min)
        {
            return null;
        }

        return Math.Clamp((numeric - min.Value) / (max.Value - min.Value) * 100, 0, 100);
    }

    private static (double? Min, double? Max) ResolveGaugeScale(MonitoringMapTile tile, SensorChannelValue? channel)
    {
        if (tile.GaugeMin is { } min && tile.GaugeMax is { } max && max > min)
        {
            return (min, max);
        }

        return IsPercentChannel(channel) ? (0d, 100d) : (null, null);
    }

    private static string FormatGaugeScale(MonitoringMapTile tile, SensorChannelValue? channel)
    {
        var (min, max) = ResolveGaugeScale(tile, channel);
        if (min is null || max is null)
        {
            return string.Empty;
        }

        var unit = string.IsNullOrWhiteSpace(channel?.Unit) ? string.Empty : " " + channel!.Unit!.Trim();
        return $"{min.Value:0.##} - {max.Value:0.##}{unit}";
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
            MonitoringMapTileKind.SensorList => "List",
            MonitoringMapTileKind.AlertFeed => "Alerts",
            MonitoringMapTileKind.Sla => "SLA",
            MonitoringMapTileKind.Clock => "Clock",
            MonitoringMapTileKind.Heading => "Heading",
            MonitoringMapTileKind.Image => "Image",
            MonitoringMapTileKind.GeoMap => "World map",
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

/// <summary>Carries the domain <see cref="MonitoringMapSlide"/> itself (not just Id/Name) so a view can render
/// its Title/Subtitle/BackgroundColor/DurationSeconds header without a second lookup.</summary>
public sealed record MapDisplaySlideViewModel(
    MonitoringMapSlide Slide,
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
    MonitoringMapTileVisualType EffectiveVisualType,
    /// <summary>Rows of a list-style widget (sensor list, alert feed). Null for every other kind - these are
    /// optional collections on the ONE tile view-model rather than a parallel hierarchy, because every other
    /// field (state, icon, colours, card chrome) is shared.</summary>
    IReadOnlyList<MapTileRowDto>? Rows = null,
    MapSlaDto? Sla = null,
    IReadOnlyList<MapPinDto>? Pins = null,
    /// <summary>Lines of a multi-series chart, already drawn against one shared scale and ordered by their
    /// last reading. Null for every other kind.</summary>
    IReadOnlyList<MapGraphSeriesDto>? Series = null,
    /// <summary>The multi-graph's shared Y scale (rounded, labelled) and its time window. Null elsewhere.</summary>
    MapGraphAxisDto? Axis = null);

/// <param name="Color">Fixed per series index so a line keeps its colour between renders - a legend whose
/// colours reshuffle every poll is worse than no legend at all.</param>
/// <param name="LinePath">Already on the chart's SHARED scale, so the lines can be compared by eye.</param>
/// <param name="Points">The same points the line is drawn from, as "x,value;x,value" (x = 0..1 across the
/// window) - what the hover tooltip reads, so it shows the real reading rather than one guessed from the path.</param>
public sealed record MapGraphSeriesDto(string Label, string Color, string? LinePath, string? Value, string? Points = null);

/// <param name="Top">Label of the scale's upper bound (with unit), likewise Middle and Bottom - the bounds are
/// already rounded to readable numbers.</param>
/// <param name="StartMs">Window start / end as Unix milliseconds, so the tooltip can turn a position into a time.</param>
public sealed record MapGraphAxisDto(string Top, string Middle, string Bottom, string? Unit, long StartMs, long EndMs, string WindowLabel);

/// <param name="X">Position as a PERCENT of the tile (0..100) - an image pin is stored that way and a geo pin
/// is projected into it, so the renderer has one case and the pin holds at any rendered size.</param>
public sealed record MapPinDto(string Label, bool ShowLabel, string Tone, MonitoringSeverity Severity, double X, double Y, string Style, string? Value);

/// <param name="Tone">A state key ("ok"/"warning"/"error"/"unknown") for the row pill.</param>
/// <param name="TimeText">Pre-formatted relative age, e.g. "12m" - formatted server-side so the public
/// wallboard needs no locale handling in the browser.</param>
public sealed record MapTileRowDto(string Label, string? Detail, string? Value, string Tone, string? TimeText);

/// <param name="Percent">Null when the window holds no state samples - "unknown", which must NOT render as 0.</param>
public sealed record MapSlaDto(double? Percent, int WindowDays, long StateSamples, string Label);
