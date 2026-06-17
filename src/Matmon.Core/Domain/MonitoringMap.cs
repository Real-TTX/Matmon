namespace Matmon.Core.Domain;

public sealed class MonitoringMap
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Map";

    public string? Description { get; set; }

    public string PublicToken { get; set; } = string.Empty;

    public int Columns { get; set; } = 12;

    public int Rows { get; set; } = 8;

    public MonitoringMapDisplayPreset DisplayPreset { get; set; } = MonitoringMapDisplayPreset.FullHd1080;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<MonitoringMapTile> Tiles { get; set; } = [];
}

public sealed class MonitoringMapTile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public MonitoringMapTileKind Kind { get; set; } = MonitoringMapTileKind.Element;

    public string Title { get; set; } = "Tile";

    public Guid? ElementId { get; set; }

    public string? Text { get; set; }

    public int X { get; set; } = 1;

    public int Y { get; set; } = 1;

    public int Width { get; set; } = 3;

    public int Height { get; set; } = 2;

    public string? BackgroundColor { get; set; }

    public string? AccentColor { get; set; }

    public string? TextColor { get; set; }

    public MonitoringMapTileGraphType GraphType { get; set; } = MonitoringMapTileGraphType.Line;

    public MonitoringMapTileVisualType VisualType { get; set; } = MonitoringMapTileVisualType.Card;

    public bool ShowTitle { get; set; } = true;

    public bool ShowStateBadge { get; set; } = true;

    public bool ShowElementName { get; set; } = true;
}

public enum MonitoringMapTileKind
{
    Text = 0,
    Element = 1,
    Status = 2,
    Value = 3,
    Graph = 4
}

public enum MonitoringMapTileGraphType
{
    Line = 0,
    Area = 1,
    Bars = 2,
    Smooth = 3
}

public enum MonitoringMapTileVisualType
{
    Card = 0,
    ProgressBar = 1,
    Gauge = 2,

    /// <summary>Derive the visual from the sensor's configured channel visual (see ChannelVisuals), or its measurement kind.</summary>
    Auto = 3
}
