using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Host.Services;

namespace Matmon.Host.Ui;

/// <summary>
/// Small render helpers that used to be triplicated as identical <c>@functions</c> blocks in
/// <c>Maps.cshtml</c>, <c>MapPublic.cshtml</c> and <c>MapEditor.cshtml</c>. Framework-free formatting only -
/// no store access - so it can be shared by the view models below and the razor partials directly.
/// </summary>
public static class MapTileRender
{
    /// <summary>CSS object-fit for an image tile - Contain is the default because cropping a floorplan would
    /// silently move every pin relative to what the user placed it against.</summary>
    public static string ImageFitCss(MonitoringMapImageFit fit) => fit switch
    {
        MonitoringMapImageFit.Cover => "cover",
        MonitoringMapImageFit.Stretch => "fill",
        _ => "contain"
    };

    /// <param name="stateKey">The resolved presentation state, so a tile-level colour rule can override the
    /// theme tone for exactly the state the tile is currently in. Passed separately because the tile itself
    /// does not know its state - that is resolved by the display provider.</param>
    public static string TileStyle(MonitoringMapTile tile, string? stateKey = null)
    {
        var parts = new List<string>();
        if (MonitoringMapColorRules.ResolveByKey(tile.ColorRules, stateKey) is { } ruleColor)
        {
            // Same custom property the theme sets per data-state, so the rule simply wins without a second
            // styling path - and it goes through BrandingSafety, so nothing unvalidated reaches CSS.
            parts.Add($"--map-tile-tone: {ruleColor}");
        }
        if (!string.IsNullOrWhiteSpace(tile.BackgroundColor))
        {
            parts.Add($"--map-tile-custom-bg: {tile.BackgroundColor}");
        }

        if (!string.IsNullOrWhiteSpace(tile.AccentColor))
        {
            parts.Add($"--map-tile-custom-accent: {tile.AccentColor}");
        }

        if (!string.IsNullOrWhiteSpace(tile.TextColor))
        {
            parts.Add($"--map-tile-custom-text: {tile.TextColor}");
        }

        return parts.Count == 0 ? string.Empty : string.Join("; ", parts) + ";";
    }

    public static string FormatProgressPercent(double? value)
    {
        return Math.Clamp(value.GetValueOrDefault(), 0, 100).ToString("0.##", CultureInfo.InvariantCulture);
    }

    public static string FormatProgressLabel(double? value)
    {
        return $"{Math.Clamp(value.GetValueOrDefault(), 0, 100):0.#}%";
    }

    public static string KindLabel(MonitoringMapTileKind kind)
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

    public static string IconForKind(MonitoringMapTileKind kind)
    {
        return kind switch
        {
            MonitoringMapTileKind.Graph => "chart",
            MonitoringMapTileKind.Text => "list",
            MonitoringMapTileKind.Status => "dashboard",
            MonitoringMapTileKind.Value => "square",
            MonitoringMapTileKind.SensorList => "list",
            MonitoringMapTileKind.AlertFeed => "bell",
            MonitoringMapTileKind.Sla => "chart",
            MonitoringMapTileKind.Clock => "clock",
            MonitoringMapTileKind.Heading => "list",
            MonitoringMapTileKind.Image => "square",
            MonitoringMapTileKind.GeoMap => "network",
            _ => "sensor"
        };
    }

    /// <summary>Resolves a map timezone id to a TimeZoneInfo, falling back to the platform zone. Silently
    /// tolerant on purpose: an id that is valid on Linux ("Europe/Berlin") and one that is valid on Windows
    /// ("W. Europe Standard Time") both occur in the wild, and a wallboard must not go blank over it.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

}

/// <summary>
/// The model behind the single shared <c>_MapTile.cshtml</c> partial. Bundles the domain tile with its
/// resolved display data (state/value/graph/...) so the SAME markup renders identically on the dashboard
/// viewer, the public wallboard and (in editable mode) the designer - true WYSIWYG instead of the designer
/// showing a separate hand-built "mock" of what the tile might look like.
/// </summary>
public sealed class MapTileRenderModel
{
    public required MonitoringMapTile Tile { get; init; }

    public string StateKey { get; init; } = "unknown";

    public string StateLabel { get; init; } = "Unknown";

    public string Subtitle { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    public string KindLabel { get; init; } = string.Empty;

    public string IconKey { get; init; } = "square";

    public string? GraphLinePath { get; init; }

    public string? GraphAreaPath { get; init; }

    public string? GraphBarPath { get; init; }

    public double? ProgressPercent { get; init; }

    public string ProgressLabel { get; init; } = string.Empty;

    public MonitoringMapTileVisualType EffectiveVisualType { get; init; } = MonitoringMapTileVisualType.Card;

    public string? ElementName { get; init; }

    // --- Precomputed logical-px render rect (Tile.Column/Row/ColumnSpan/RowSpan -> px via
    // v3 places a tile with the grid (span columns x span row units), so there is no px rect to precompute
    // and no TileX/Y/Width/Height on this model any more - _MapTile reads the spans straight off the tile. ---




    // --- Editor-only (Phase C's live-JSON patch hooks target the same DOM regardless of this flag) ---

    /// <summary>True when rendered inside the designer: adds the drag handle, remove button, size badge,
    /// resize handle and the position/identity hidden inputs the POST binds back to <c>Input.Tiles[Index]</c>.</summary>
    public bool Editable { get; init; }

    public int Index { get; init; }

    public Guid SlideId { get; init; }

    public bool IsDeleted { get; init; }

    /// <summary>Rows of a list-style widget (sensor list, alert feed); null for every other kind.</summary>
    public IReadOnlyList<MapTileRowDto>? Rows { get; init; }

    public MapSlaDto? Sla { get; init; }

    /// <summary>Status markers on an image / world-map tile, already positioned as a percent of the tile.</summary>
    public IReadOnlyList<MapPinDto>? Pins { get; init; }

    /// <summary>Lines of a multi-series chart, already on one shared scale; null for every other kind.</summary>
    public IReadOnlyList<MapGraphSeriesDto>? Series { get; init; }

    public MapGraphAxisDto? Axis { get; init; }

    /// <summary>IANA timezone the board renders times in - carried down to the clock widget, which ticks
    /// client-side and therefore needs the MAP timezone rather than the browser one.</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>The model-binding prefix for this tile's hidden inputs, e.g. <c>Input.Tiles[3]</c>.</summary>
    public string FieldNamePrefix => $"Input.Tiles[{Index}]";

    public static MapTileRenderModel FromDisplay(
        MapDisplayTileViewModel vm,
        MonitoringMap map,
        bool editable = false,
        int index = 0,
        Guid slideId = default,
        bool isDeleted = false,
        MonitoringMapTile? tileOverride = null)
    {
        var tile = tileOverride ?? vm.Tile;
        return new()
        {
            Tile = tile,
            StateKey = vm.StateKey,
            StateLabel = vm.StateLabel,
            Subtitle = vm.Subtitle,
            Value = vm.Value,
            KindLabel = vm.KindLabel,
            IconKey = vm.IconKey,
            GraphLinePath = vm.GraphLinePath,
            GraphAreaPath = vm.GraphAreaPath,
            GraphBarPath = vm.GraphBarPath,
            ProgressPercent = vm.ProgressPercent,
            ProgressLabel = vm.ProgressLabel,
            EffectiveVisualType = vm.EffectiveVisualType,
            ElementName = vm.Element?.Name,
            Editable = editable,
            Index = index,
            SlideId = slideId,
            IsDeleted = isDeleted,
            Rows = vm.Rows,
            Sla = vm.Sla,
            Pins = vm.Pins,
            Series = vm.Series,
            Axis = vm.Axis,
            TimeZoneId = map.DisplayTimeZoneId
        };
    }

    /// <summary>A tile with no resolved live data yet - a just-added designer tile before its first
    /// live-preview fetch resolves, or one whose target could not be found.</summary>
    public static MapTileRenderModel Placeholder(MonitoringMapTile tile, MonitoringMap map, int index, Guid slideId, bool isDeleted = false)
    {
        return new()
        {
            Tile = tile,
            StateKey = "unknown",
            StateLabel = "Unknown",
            Subtitle = tile.Kind == MonitoringMapTileKind.Text
                ? tile.Text ?? string.Empty
                : (tile.ElementId.HasValue || !string.IsNullOrWhiteSpace(tile.TargetTag) ? string.Empty : "No target selected"),
            KindLabel = MapTileRender.KindLabel(tile.Kind),
            IconKey = string.IsNullOrWhiteSpace(tile.IconKey) ? MapTileRender.IconForKind(tile.Kind) : tile.IconKey.Trim(),
            EffectiveVisualType = tile.VisualType == MonitoringMapTileVisualType.Auto ? MonitoringMapTileVisualType.Card : tile.VisualType,
            Editable = true,
            Index = index,
            SlideId = slideId,
            IsDeleted = isDeleted
        };
    }
}

/// <summary>The model behind the shared <c>_MapSlide.cshtml</c> partial: a slide plus its already-rendered
/// tile models, and the map it belongs to (for the shared logical canvas size / ShowSlideHeaders switch).</summary>
public sealed class MapSlideRenderModel
{
    public required MonitoringMapSlide Slide { get; init; }

    public required MonitoringMap Map { get; init; }

    public required IReadOnlyList<MapTileRenderModel> Tiles { get; init; }

    public int SlideIndex { get; init; }

    public bool Editable { get; init; }
}

/// <param name="Mode">Decides BOTH the styling and where the caller renders it: "Below the board" must sit
/// outside the .map-stage, because the stage is a uniformly scaled canvas and anything inside it scales with
/// the board instead of sitting under it.</param>
public sealed record MapCarouselNavModel(IReadOnlyList<string> SlideNames, MonitoringMapPaginationMode Mode);
