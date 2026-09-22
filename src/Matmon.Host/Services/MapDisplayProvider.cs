using System.Collections.Concurrent;
using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;

namespace Matmon.Host.Services;

public sealed class MapDisplayProvider
{
    private const double SparklineWidth = 100;
    private const double SparklineHeight = 40;

    /// <summary>How long an SLA tile reuses its computed uptime - see ResolveUptime. A multi-day figure does
    /// not move between two page loads.</summary>
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
             or MonitoringMapTileKind.GeoMap;

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
            _ => BuildSensorListTile(tile, element, latest)
        };
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
        var key = $"{tile.Id}|{days}|{sensors.Count}";
        var now = DateTimeOffset.UtcNow;
        if (_slaCache.TryGetValue(key, out var cached) && now - cached.ComputedUtc < SlaCacheTtl)
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
    IReadOnlyList<MapPinDto>? Pins = null);

/// <param name="X">Position as a PERCENT of the tile (0..100) - an image pin is stored that way and a geo pin
/// is projected into it, so the renderer has one case and the pin holds at any rendered size.</param>
public sealed record MapPinDto(string Label, bool ShowLabel, string Tone, MonitoringSeverity Severity, double X, double Y, string Style, string? Value);

/// <param name="Tone">A state key ("ok"/"warning"/"error"/"unknown") for the row pill.</param>
/// <param name="TimeText">Pre-formatted relative age, e.g. "12m" - formatted server-side so the public
/// wallboard needs no locale handling in the browser.</param>
public sealed record MapTileRowDto(string Label, string? Detail, string? Value, string Tone, string? TimeText);

/// <param name="Percent">Null when the window holds no state samples - "unknown", which must NOT render as 0.</param>
public sealed record MapSlaDto(double? Percent, int WindowDays, long StateSamples, string Label);
