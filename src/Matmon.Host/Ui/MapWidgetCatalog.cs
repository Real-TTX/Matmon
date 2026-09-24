using Matmon.Core.Domain;

namespace Matmon.Host.Ui;

/// <summary>
/// The single source of the map editor's widget library - what the left-hand palette offers, grouped and
/// searchable. Deliberately NOT the same list as <see cref="MonitoringMapTileKind"/>: several entries are the
/// same kind pre-set to a different <see cref="MonitoringMapTileVisualType"/> (Gauge and Progress are both a
/// Value tile), which is exactly the distinction a user cares about when picking a widget and the enum does
/// not express. Before this the palette was seven hand-written buttons in MapEditor.cshtml, so adding a widget
/// meant editing markup; now it is one entry here.
/// </summary>
public static class MapWidgetCatalog
{
    public static IReadOnlyList<MapWidgetDefinition> All { get; } =
    [
        new("state", "State", "One sensor, host, folder or probe", "sensor", MapWidgetCategory.Status,
            MonitoringMapTileKind.Element, SearchTerms: "status device host sensor probe up down"),
        new("group", "Group health", "Aggregated health below a target or tag", "dashboard", MapWidgetCategory.Status,
            MonitoringMapTileKind.Status, SearchTerms: "summary rollup folder aggregate tag"),

        new("value", "Value", "Large default-channel value", "square", MapWidgetCategory.Metrics,
            MonitoringMapTileKind.Value, SearchTerms: "number reading metric channel"),
        new("gauge", "Gauge", "Radial gauge of a percent channel", "chart", MapWidgetCategory.Metrics,
            MonitoringMapTileKind.Value, MonitoringMapTileVisualType.Gauge, "dial percent cpu memory disk"),
        new("progress", "Progress", "Progress bar of a percent channel", "signal", MapWidgetCategory.Metrics,
            MonitoringMapTileKind.Value, MonitoringMapTileVisualType.ProgressBar, "bar percent usage capacity"),
        new("auto", "Auto visual", "Picks gauge / bar / number from the channel's own visual", "spark", MapWidgetCategory.Metrics,
            MonitoringMapTileKind.Value, MonitoringMapTileVisualType.Auto, "automatic default channel visual"),
        new("graph", "Graph", "Sensor trend sparkline", "chart", MapWidgetCategory.Metrics,
            MonitoringMapTileKind.Graph, SearchTerms: "chart trend history sparkline line area bars"),
        new("multi-graph", "Multi graph", "Several sensors as lines in one chart, with a legend", "chart", MapWidgetCategory.Metrics,
            MonitoringMapTileKind.MultiGraph, SearchTerms: "chart compare lines legend series multi overlay trend"),

        new("list", "Sensor list", "Ranked list of the sensors under a target", "list", MapWidgetCategory.Metrics,
            MonitoringMapTileKind.SensorList, SearchTerms: "top talkers worst ranking table rows busiest"),
        new("sla", "SLA / uptime", "Availability over a window, from the statistics buckets", "chart", MapWidgetCategory.Metrics,
            MonitoringMapTileKind.Sla, SearchTerms: "uptime availability percent history slo"),

        new("alerts", "Alert feed", "Newest open alerts, optionally scoped to a target", "bell", MapWidgetCategory.Status,
            MonitoringMapTileKind.AlertFeed, SearchTerms: "incidents problems events open acknowledged"),

        new("text", "Text", "Label, notes or instructions", "list", MapWidgetCategory.Content,
            MonitoringMapTileKind.Text, SearchTerms: "label note caption instructions"),
        new("heading", "Heading", "Large section title for grouping a board", "list", MapWidgetCategory.Content,
            MonitoringMapTileKind.Heading, SearchTerms: "title section header caption"),
        new("clock", "Clock", "Current time in the board timezone", "clock", MapWidgetCategory.Content,
            MonitoringMapTileKind.Clock, SearchTerms: "time date wall now"),

        new("image", "Image", "An uploaded picture - logo, photo, diagram", "square", MapWidgetCategory.Places,
            MonitoringMapTileKind.Image, SearchTerms: "picture photo logo diagram upload png jpeg"),
        new("floorplan", "Floorplan + pins", "A picture with status pins placed on it", "probe", MapWidgetCategory.Places,
            MonitoringMapTileKind.Image, SearchTerms: "plan rack room office markers dots layout"),
        new("geomap", "World map", "Offline world map with pins by latitude/longitude", "network", MapWidgetCategory.Places,
            MonitoringMapTileKind.GeoMap, SearchTerms: "geo globe sites locations countries world")
    ];

    /// <summary>Which schematic the palette draws for an entry. Derived from what the widget actually
    /// RENDERS rather than from its <see cref="MonitoringMapTileKind"/> alone - a gauge and a progress bar are
    /// both Value tiles and look nothing alike, which is the very reason they are separate palette entries.
    /// Keeping it here means a new catalog entry gets a sensible preview without a second table to keep in
    /// sync with this one.</summary>
    public static string PreviewKey(MapWidgetDefinition widget) => widget switch
    {
        { VisualType: MonitoringMapTileVisualType.Gauge } => "gauge",
        { VisualType: MonitoringMapTileVisualType.ProgressBar } => "progress",
        { Kind: MonitoringMapTileKind.Graph } => "graph",
        { Kind: MonitoringMapTileKind.MultiGraph } => "multigraph",
        { Kind: MonitoringMapTileKind.SensorList } => "list",
        { Kind: MonitoringMapTileKind.AlertFeed } => "alerts",
        { Kind: MonitoringMapTileKind.Sla } => "sla",
        { Kind: MonitoringMapTileKind.Clock } => "clock",
        { Kind: MonitoringMapTileKind.Heading } => "heading",
        { Kind: MonitoringMapTileKind.Text } => "text",
        { Kind: MonitoringMapTileKind.GeoMap } => "geo",
        // Both are Image tiles; "floorplan" is the one that exists for its pins, so it previews them.
        { Kind: MonitoringMapTileKind.Image } => widget.Key == "floorplan" ? "pins" : "image",
        { Kind: MonitoringMapTileKind.Status } => "group",
        { Kind: MonitoringMapTileKind.Value } => "value",
        _ => "state"
    };

    public static MapWidgetDefinition? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(widget => widget.Key == key);

    public static IEnumerable<IGrouping<MapWidgetCategory, MapWidgetDefinition>> ByCategory() =>
        All.GroupBy(widget => widget.Category).OrderBy(group => (int)group.Key);

    public static string CategoryLabel(MapWidgetCategory category) => category switch
    {
        MapWidgetCategory.Status => "Status",
        MapWidgetCategory.Metrics => "Metrics",
        MapWidgetCategory.Content => "Content",
        MapWidgetCategory.Places => "Places",
        _ => "Other"
    };

    /// <summary>
    /// Ready-made arrangements that fill the CURRENT slide with empty, correctly-sized widgets, so a new board
    /// starts from a layout instead of an empty canvas. Every slot is authored against the default 12x6 grid
    /// and honours the widget kind's minimum span (<see cref="MonitoringMapTileConstraints"/>) - on a smaller
    /// custom grid the designer clamps them, which can overlap, so the editor offers templates only while the
    /// grid is at least as large as the template needs.
    /// </summary>
    public static IReadOnlyList<MapLayoutTemplate> LayoutTemplates { get; } =
    [
        new("kpi-wall", "KPI wall", "Six headline numbers over one wide trend", 12, 6,
        [
            new("value", 1, 1, 2, 2), new("value", 3, 1, 2, 2), new("value", 5, 1, 2, 2),
            new("value", 7, 1, 2, 2), new("value", 9, 1, 2, 2), new("value", 11, 1, 2, 2),
            new("graph", 1, 3, 12, 4)
        ]),
        new("noc-wall", "NOC wall", "Twelve equal state tiles, four across", 12, 6,
        [
            new("state", 1, 1, 3, 2), new("state", 4, 1, 3, 2), new("state", 7, 1, 3, 2), new("state", 10, 1, 3, 2),
            new("state", 1, 3, 3, 2), new("state", 4, 3, 3, 2), new("state", 7, 3, 3, 2), new("state", 10, 3, 3, 2),
            new("state", 1, 5, 3, 2), new("state", 4, 5, 3, 2), new("state", 7, 5, 3, 2), new("state", 10, 5, 3, 2)
        ]),
        new("two-columns", "Two columns", "Two groups, each with its own trend", 12, 6,
        [
            new("group", 1, 1, 6, 2), new("graph", 1, 3, 6, 4),
            new("group", 7, 1, 6, 2), new("graph", 7, 3, 6, 4)
        ]),
        new("overview", "Overview", "Summary and trend on top, four numbers below", 12, 6,
        [
            new("group", 1, 1, 5, 3), new("graph", 6, 1, 7, 3),
            new("value", 1, 4, 3, 3), new("value", 4, 4, 3, 3), new("value", 7, 4, 3, 3), new("value", 10, 4, 3, 3)
        ])
    ];
}

public enum MapWidgetCategory
{
    Status = 0,
    Metrics = 1,
    Content = 2,
    Places = 3
}

/// <param name="Key">Stable palette key - also what a layout template's slot refers to.</param>
/// <param name="SearchTerms">Extra words the palette search matches besides label and description, so
/// "cpu" finds the gauge and "rollup" finds group health.</param>
public sealed record MapWidgetDefinition(
    string Key,
    string Label,
    string Description,
    string IconKey,
    MapWidgetCategory Category,
    MonitoringMapTileKind Kind,
    MonitoringMapTileVisualType VisualType = MonitoringMapTileVisualType.Card,
    string? SearchTerms = null);

/// <param name="MinColumns">Grid size the arrangement is authored for; the editor hides the template on a
/// smaller grid rather than silently piling clamped tiles on top of each other.</param>
public sealed record MapLayoutTemplate(
    string Key,
    string Label,
    string Description,
    int MinColumns,
    int MinRows,
    IReadOnlyList<MapLayoutSlot> Slots);

public sealed record MapLayoutSlot(string WidgetKey, int Column, int Row, int ColumnSpan, int RowSpan);
