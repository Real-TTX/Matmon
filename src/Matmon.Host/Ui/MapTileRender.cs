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
    public static string TileStyle(MonitoringMapTile tile)
    {
        var parts = new List<string>();
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
            _ => "sensor"
        };
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

    // --- Editor-only (Phase C's live-JSON patch hooks target the same DOM regardless of this flag) ---

    /// <summary>True when rendered inside the designer: adds the drag handle, remove button, size badge,
    /// resize handle and the position/identity hidden inputs the POST binds back to <c>Input.Tiles[Index]</c>.</summary>
    public bool Editable { get; init; }

    public int Index { get; init; }

    public Guid SlideId { get; init; }

    public bool IsDeleted { get; init; }

    /// <summary>The model-binding prefix for this tile's hidden inputs, e.g. <c>Input.Tiles[3]</c>.</summary>
    public string FieldNamePrefix => $"Input.Tiles[{Index}]";

    public static MapTileRenderModel FromDisplay(
        MapDisplayTileViewModel vm,
        bool editable = false,
        int index = 0,
        Guid slideId = default,
        bool isDeleted = false,
        MonitoringMapTile? tileOverride = null) => new()
    {
        Tile = tileOverride ?? vm.Tile,
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
        IsDeleted = isDeleted
    };

    /// <summary>A tile with no resolved live data yet - a just-added designer tile before its first
    /// live-preview fetch resolves, or one whose target could not be found.</summary>
    public static MapTileRenderModel Placeholder(MonitoringMapTile tile, int index, Guid slideId, bool isDeleted = false) => new()
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
