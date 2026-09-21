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

        new("text", "Text", "Label, notes or instructions", "list", MapWidgetCategory.Content,
            MonitoringMapTileKind.Text, SearchTerms: "label heading note caption instructions")
    ];

    public static MapWidgetDefinition? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(widget => widget.Key == key);

    public static IEnumerable<IGrouping<MapWidgetCategory, MapWidgetDefinition>> ByCategory() =>
        All.GroupBy(widget => widget.Category).OrderBy(group => (int)group.Key);

    public static string CategoryLabel(MapWidgetCategory category) => category switch
    {
        MapWidgetCategory.Status => "Status",
        MapWidgetCategory.Metrics => "Metrics",
        MapWidgetCategory.Content => "Content",
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
    Content = 2
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
