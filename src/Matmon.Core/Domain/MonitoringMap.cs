using System.Text.Json.Serialization;

namespace Matmon.Core.Domain;

public sealed class MonitoringMap
{
    /// <summary>The current tile-geometry layout scheme. 0 = legacy grid cells (Columns x Rows, no
    /// padding/margin concept), 1 = free positioning in logical px on a <see cref="LogicalWidth"/> x
    /// <see cref="LogicalHeight"/> canvas (Phase A), 2 = the current strict cell grid - Columns/Rows are
    /// authoritative again (<see cref="MonitoringMapTile.Column"/>/<see cref="MonitoringMapTile.Row"/>/
    /// <see cref="MonitoringMapTile.ColumnSpan"/>/<see cref="MonitoringMapTile.RowSpan"/>), with
    /// <see cref="TilePadding"/>/<see cref="OuterMargin"/> controlling the cell-to-px conversion
    /// (<see cref="MonitoringMapGeometry"/>). A map loaded with an older version is migrated once via
    /// <see cref="MonitoringMapLayoutMigration"/>.</summary>
    public const int CurrentLayoutVersion = 2;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Map";

    public string? Description { get; set; }

    public string PublicToken { get; set; } = string.Empty;

    public int LayoutVersion { get; set; }

    /// <summary>Grid column count for tile placement - see <see cref="MonitoringMapTile.Column"/>/
    /// <see cref="MonitoringMapTile.ColumnSpan"/> and <see cref="MonitoringMapGeometry"/>.</summary>
    public int Columns { get; set; } = 12;

    /// <summary>Grid row count - see <see cref="Columns"/>.</summary>
    public int Rows { get; set; } = 6;

    /// <summary>Logical px gap between adjacent cells (both axes) - see <see cref="MonitoringMapGeometry"/>.</summary>
    public int TilePadding { get; set; } = 16;

    /// <summary>Logical px margin around the whole grid (all four sides) - see <see cref="MonitoringMapGeometry"/>.</summary>
    public int OuterMargin { get; set; } = 24;

    /// <summary>Legacy full-board display preset, superseded by <see cref="AspectRatioWidth"/>/<see cref="AspectRatioHeight"/>
    /// and no longer written by the editor - see <see cref="Columns"/>.</summary>
    public MonitoringMapDisplayPreset DisplayPreset { get; set; } = MonitoringMapDisplayPreset.FullHd1080;

    /// <summary>Board aspect-ratio numerator / denominator (e.g. 16 / 9). 0 = derive from the legacy
    /// <see cref="DisplayPreset"/>. Only the RATIO matters - the board scales to fill whatever screen it is
    /// shown on (notebook, Full-HD wall, 4K), so there are no fixed pixels.</summary>
    public int AspectRatioWidth { get; set; }

    public int AspectRatioHeight { get; set; }

    /// <summary>The logical canvas width in px that tile <see cref="MonitoringMapTile.X"/>/<see cref="MonitoringMapTile.Width"/>
    /// are authored against. Fixed at 1920 (only the height varies with the aspect ratio) so a tile's px rect
    /// means the same thing regardless of the screen it is eventually scaled onto - see <see cref="LogicalSizeFor"/>.</summary>
    public int LogicalWidth { get; set; } = 1920;

    /// <summary>The logical canvas height in px - see <see cref="LogicalWidth"/>.</summary>
    public int LogicalHeight { get; set; } = 1080;

    /// <summary>Whether <see cref="PublicToken"/> is a live, reachable link. New maps default to off (opt-in);
    /// a map migrated from the legacy layout (which always had a live anonymous link) defaults to on so nothing
    /// already shared breaks - see <see cref="MonitoringMapLayoutMigration"/>.</summary>
    public bool PublicEnabled { get; set; }

    /// <summary>Whether a slide's own <see cref="MonitoringMapSlide.Title"/>/<see cref="MonitoringMapSlide.Subtitle"/>
    /// header is rendered at all (a per-slide <see cref="MonitoringMapSlide.ShowHeader"/> can still opt a single
    /// slide out even when this is on).</summary>
    public bool ShowSlideHeaders { get; set; } = true;

    /// <summary>How the public wallboard fills a screen whose ratio differs from the map's. Defaults to Fit
    /// (keep the aspect ratio, letterboxed) - the ratio is the point; Stretch (fill, distort) is opt-in.</summary>
    public MonitoringMapWallboardFit WallboardFit { get; set; } = MonitoringMapWallboardFit.Fit;

    /// <summary>The effective aspect ratio (numerator, denominator): the explicit ratio when set, otherwise the
    /// legacy display preset's dimensions used purely as a ratio (Full HD/QHD/4K -> 16:9, ultrawide -> ~21:9).</summary>
    public (int Width, int Height) EffectiveAspect()
    {
        if (AspectRatioWidth > 0 && AspectRatioHeight > 0)
        {
            return (AspectRatioWidth, AspectRatioHeight);
        }

        var info = MonitoringMapDisplayPresetCatalog.Resolve(DisplayPreset);
        return (info.Width, info.Height);
    }

    /// <summary>The logical canvas size for a given aspect ratio: a fixed 1920 width, with the height derived
    /// from the ratio (16:9 -> 1080, 16:10 -> 1200, 21:9 -> 823, 4:3 -> 1440, ...). Every tile position/size is
    /// authored in this space, so the same map always means the same px rects regardless of screen.</summary>
    public static (int Width, int Height) LogicalSizeFor(int aspectWidth, int aspectHeight)
    {
        var w = aspectWidth > 0 ? aspectWidth : 16;
        var h = aspectHeight > 0 ? aspectHeight : 9;
        const int baseWidth = 1920;
        var height = (int)Math.Round(baseWidth * (double)h / w, MidpointRounding.AwayFromZero);
        return (baseWidth, Math.Max(1, height));
    }

    /// <summary>Seconds each slide is shown before the public wallboard auto-advances to the next slide.</summary>
    public int AutoRotateSeconds { get; set; } = 12;

    /// <summary>How the public wallboard shows the slide pagination / page indicator.</summary>
    public MonitoringMapPaginationMode PaginationMode { get; set; } = MonitoringMapPaginationMode.Below;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Legacy single-board tiles. Authoritative only when <see cref="Slides"/> is empty (pre-multi-slide maps).</summary>
    public List<MonitoringMapTile> Tiles { get; set; } = [];

    /// <summary>Ordered slides for the carousel. When non-empty this is authoritative and <see cref="Tiles"/> mirrors slide 1.</summary>
    public List<MonitoringMapSlide> Slides { get; set; } = [];

    /// <summary>
    /// The slides to render: <see cref="Slides"/> when present, otherwise a single
    /// synthetic slide wrapping the legacy <see cref="Tiles"/>. Always returns at
    /// least one slide so consumers can iterate uniformly.
    /// </summary>
    public IReadOnlyList<MonitoringMapSlide> EffectiveSlides()
    {
        if (Slides.Count > 0)
        {
            return Slides;
        }

        return [new MonitoringMapSlide { Name = "Slide 1", Tiles = Tiles }];
    }

    /// <summary>Deep, detached copy. Single source of truth for map cloning so a newly added field is
    /// copied everywhere at once (dropping a field here silently reset it to its default on every read -
    /// e.g. WallboardFit fell back to Fit, so a "stretch" wallboard never stretched). Covered by a
    /// reflection parity test.</summary>
    public MonitoringMap Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        PublicToken = PublicToken,
        LayoutVersion = LayoutVersion,
        Columns = Columns,
        Rows = Rows,
        TilePadding = TilePadding,
        OuterMargin = OuterMargin,
        DisplayPreset = DisplayPreset,
        AspectRatioWidth = AspectRatioWidth,
        AspectRatioHeight = AspectRatioHeight,
        LogicalWidth = LogicalWidth,
        LogicalHeight = LogicalHeight,
        PublicEnabled = PublicEnabled,
        ShowSlideHeaders = ShowSlideHeaders,
        WallboardFit = WallboardFit,
        AutoRotateSeconds = AutoRotateSeconds,
        PaginationMode = PaginationMode,
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
        Tiles = Tiles.Select(tile => tile.Clone()).ToList(),
        Slides = Slides.Select(slide => slide.Clone()).ToList()
    };
}

public sealed class MonitoringMapSlide
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Slide";

    /// <summary>Optional big headline rendered on the slide itself (the wallboard header), independent of the
    /// designer-only <see cref="Name"/> tab label. Null/empty = no headline.</summary>
    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    /// <summary>Seconds this slide is shown before the carousel advances. Null = fall back to the map's
    /// <see cref="MonitoringMap.AutoRotateSeconds"/>, so most slides need no per-slide override.</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Optional per-slide background colour (#RRGGBB). Null = the theme default.</summary>
    public string? BackgroundColor { get; set; }

    /// <summary>Whether this slide's own <see cref="Title"/>/<see cref="Subtitle"/> header renders, provided
    /// the map-level <see cref="MonitoringMap.ShowSlideHeaders"/> is also on.</summary>
    public bool ShowHeader { get; set; } = true;

    public List<MonitoringMapTile> Tiles { get; set; } = [];

    public MonitoringMapSlide Clone() => new()
    {
        Id = Id,
        Name = Name,
        Title = Title,
        Subtitle = Subtitle,
        DurationSeconds = DurationSeconds,
        BackgroundColor = BackgroundColor,
        ShowHeader = ShowHeader,
        Tiles = Tiles.Select(tile => tile.Clone()).ToList()
    };
}

public sealed class MonitoringMapTile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public MonitoringMapTileKind Kind { get; set; } = MonitoringMapTileKind.Element;

    public string Title { get; set; } = "Tile";

    public Guid? ElementId { get; set; }

    /// <summary>
    /// When set, the tile targets a tag instead of a single element: it aggregates every
    /// sensor whose effective tags include this tag (cross-tree). Mutually exclusive with
    /// <see cref="ElementId"/>.
    /// </summary>
    public string? TargetTag { get; set; }

    public string? Text { get; set; }

    /// <summary>Optional glyph key (a <c>MatmonIcons</c> name) shown on the tile - chosen via the icon picker.
    /// Null/empty = no icon. Scales with the tile size at render time.</summary>
    public string? IconKey { get; set; }

    /// <summary>When false the tile drops its card chrome (background/border/shadow) and renders "bare" -
    /// e.g. a section heading placed at height 1 with no visible tile. Defaults to true (a normal card).</summary>
    public bool ShowCard { get; set; } = true;

    /// <summary>1-based grid column this tile starts in - see <see cref="MonitoringMap.Columns"/> and
    /// <see cref="MonitoringMapGeometry"/>. Kept under the JSON key "x" (its name under both the v0 grid-cell
    /// scheme and the v1 free-px scheme) so an old workspace.json still deserializes into this field; the
    /// value is only actually a cell index once <see cref="MonitoringMapLayoutMigration"/> has run (a v1 map's
    /// raw px value is reinterpreted by the migration, not by this attribute).</summary>
    [JsonPropertyName("x")]
    public int Column { get; set; } = 1;

    /// <summary>1-based grid row this tile starts in - see <see cref="Column"/>.</summary>
    [JsonPropertyName("y")]
    public int Row { get; set; } = 1;

    /// <summary>How many grid columns this tile spans (&gt;= 1, floored to the kind's minimum by
    /// <see cref="MonitoringMapTileConstraints.Clamp"/>) - see <see cref="Column"/>.</summary>
    [JsonPropertyName("width")]
    public int ColumnSpan { get; set; } = 2;

    /// <summary>How many grid rows this tile spans - see <see cref="ColumnSpan"/>.</summary>
    [JsonPropertyName("height")]
    public int RowSpan { get; set; } = 2;

    public string? BackgroundColor { get; set; }

    public string? AccentColor { get; set; }

    public string? TextColor { get; set; }

    public MonitoringMapTileGraphType GraphType { get; set; } = MonitoringMapTileGraphType.Line;

    public MonitoringMapTileVisualType VisualType { get; set; } = MonitoringMapTileVisualType.Card;

    public bool ShowTitle { get; set; } = true;

    public bool ShowStateBadge { get; set; } = true;

    public bool ShowElementName { get; set; } = true;

    public MonitoringMapTile Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
        ElementId = ElementId,
        TargetTag = TargetTag,
        Text = Text,
        IconKey = IconKey,
        ShowCard = ShowCard,
        Column = Column,
        Row = Row,
        ColumnSpan = ColumnSpan,
        RowSpan = RowSpan,
        BackgroundColor = BackgroundColor,
        AccentColor = AccentColor,
        TextColor = TextColor,
        GraphType = GraphType,
        VisualType = VisualType,
        ShowTitle = ShowTitle,
        ShowStateBadge = ShowStateBadge,
        ShowElementName = ShowElementName
    };
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

public enum MonitoringMapPaginationMode
{
    /// <summary>Page controls sit under the board (default).</summary>
    Below = 0,

    /// <summary>Page controls overlay the board and stay visible.</summary>
    OverlayAlways = 1,

    /// <summary>Page controls overlay the board, appear on mouse-move / slide change, then fade out.</summary>
    OverlayOnActivity = 2,

    /// <summary>No page controls - the board just auto-rotates.</summary>
    Hidden = 3
}

/// <summary>How the public wallboard fills a screen whose aspect ratio differs from the map's.</summary>
public enum MonitoringMapWallboardFit
{
    /// <summary>Keep the map's aspect ratio, centered, with slim bars if the screen ratio differs (no distortion).</summary>
    Fit = 0,

    /// <summary>Stretch the map to fill the whole screen, distorting the ratio if needed (never any bars).</summary>
    Stretch = 1
}

public enum MonitoringMapTileVisualType
{
    Card = 0,
    ProgressBar = 1,
    Gauge = 2,

    /// <summary>Derive the visual from the sensor's configured channel visual (see ChannelVisuals), or its measurement kind.</summary>
    Auto = 3
}

/// <summary>
/// The single source of truth for map-tile sizing, in grid CELLS (see <see cref="MonitoringMap.Columns"/>/
/// <see cref="MonitoringMap.Rows"/>). Replaces two now-deleted rival tables (the store's grid-cell
/// <c>MapTileSizeLimits</c> and the JS <c>sizeLimits</c> object) that could silently drift apart. Only a
/// per-kind minimum floor is defined - a tile may otherwise grow to fill the whole grid, so <see cref="Clamp"/>
/// derives the ceiling from the grid size passed in rather than from a second per-kind maximum.
/// </summary>
public static class MonitoringMapTileConstraints
{
    /// <summary>(MinColumns, MinRows, DefaultColumns, DefaultRows) in grid cells for a tile kind. Every kind
    /// has a floor of at least 2x2 cells (the mockup's "no smaller than 2x2" rule) - a wider/taller floor is
    /// only set where the tile kind genuinely needs the room (a graph needs room for an axis, a summary tile
    /// needs room for its rollup counts).</summary>
    public static (int MinColumns, int MinRows, int DefaultColumns, int DefaultRows) For(MonitoringMapTileKind kind)
    {
        return kind switch
        {
            MonitoringMapTileKind.Text => (2, 2, 2, 2),
            MonitoringMapTileKind.Element => (2, 2, 3, 2),
            MonitoringMapTileKind.Value => (2, 2, 2, 2),
            MonitoringMapTileKind.Status => (3, 2, 4, 2),
            MonitoringMapTileKind.Graph => (4, 3, 4, 3),
            _ => (2, 2, 2, 2)
        };
    }

    /// <summary>Clamps a tile's cell geometry in place: enforces the kind's minimum span (shrinking it below
    /// the grid size only if the grid itself is smaller than the minimum), then keeps the whole span inside the
    /// 1..columns / 1..rows grid. Mutates <paramref name="tile"/> - used by both the layout migration and the
    /// store's save-time normalization so an off-grid or too-small tile always lands somewhere sane rather than
    /// being rejected.</summary>
    public static void Clamp(MonitoringMapTile tile, int columns, int rows)
    {
        var (minColumns, minRows, _, _) = For(tile.Kind);
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        // Enforce the floor first, then cap to the grid - in that order, so a grid smaller than the kind's
        // usual minimum (a tiny/degenerate board) still yields a tile no bigger than the grid itself rather
        // than one that overflows it.
        var columnSpan = Math.Min(Math.Max(minColumns, tile.ColumnSpan), columns);
        var rowSpan = Math.Min(Math.Max(minRows, tile.RowSpan), rows);
        tile.ColumnSpan = columnSpan;
        tile.RowSpan = rowSpan;
        tile.Column = Math.Clamp(tile.Column, 1, Math.Max(1, columns - columnSpan + 1));
        tile.Row = Math.Clamp(tile.Row, 1, Math.Max(1, rows - rowSpan + 1));
    }
}

/// <summary>
/// The single cell &lt;-&gt; logical-px conversion for a map's tiles, implemented once so the render partials
/// (<see cref="MonitoringMap.LogicalWidth"/>-based - see <c>Ui/MapTileRender.cs</c> in Matmon.Host), the
/// designer JS (mirrored, since JS cannot reference Core) and the v1-&gt;v2 layout migration below can never
/// disagree about what a given (Column, Row, ColumnSpan, RowSpan) rect looks like in px.
/// </summary>
public static class MonitoringMapGeometry
{
    /// <summary>The width/height of a single grid cell in logical px, derived from the map's canvas size, its
    /// Columns/Rows and the padding/margin gaps.</summary>
    public static (double CellWidth, double CellHeight) CellSize(MonitoringMap map)
    {
        var columns = Math.Max(1, map.Columns);
        var rows = Math.Max(1, map.Rows);
        var cellWidth = (map.LogicalWidth - 2.0 * map.OuterMargin - (columns - 1) * map.TilePadding) / columns;
        var cellHeight = (map.LogicalHeight - 2.0 * map.OuterMargin - (rows - 1) * map.TilePadding) / rows;
        return (cellWidth, cellHeight);
    }

    /// <summary>Converts a tile's cell geometry to its logical-px render rect:
    /// <c>x = margin + (col-1) * (cellW+gap)</c>, <c>w = span*cellW + (span-1)*gap</c> (and the y/h equivalents).</summary>
    public static (int X, int Y, int W, int H) PixelRect(MonitoringMap map, MonitoringMapTile tile)
    {
        var (cellWidth, cellHeight) = CellSize(map);
        var x = map.OuterMargin + (tile.Column - 1) * (cellWidth + map.TilePadding);
        var y = map.OuterMargin + (tile.Row - 1) * (cellHeight + map.TilePadding);
        var w = tile.ColumnSpan * cellWidth + (tile.ColumnSpan - 1) * map.TilePadding;
        var h = tile.RowSpan * cellHeight + (tile.RowSpan - 1) * map.TilePadding;
        return (Round(x), Round(y), Round(w), Round(h));
    }

    /// <summary>Inverts <see cref="PixelRect"/>: given a v1 (Phase A free-px) tile's raw px rect, finds the
    /// nearest cell rect using the map's CURRENT Columns/Rows/TilePadding/OuterMargin. Used only by
    /// <see cref="MonitoringMapLayoutMigration"/> - a v2-native tile is always authored directly in cells.</summary>
    public static (int Column, int Row, int ColumnSpan, int RowSpan) CellRectFromPixels(MonitoringMap map, int x, int y, int width, int height)
    {
        var (cellWidth, cellHeight) = CellSize(map);
        var columnStep = cellWidth + map.TilePadding;
        var rowStep = cellHeight + map.TilePadding;
        var column = columnStep > 0 ? Round((x - map.OuterMargin) / columnStep) + 1 : 1;
        var row = rowStep > 0 ? Round((y - map.OuterMargin) / rowStep) + 1 : 1;
        var columnSpan = columnStep > 0 ? Round((width + map.TilePadding) / columnStep) : 1;
        var rowSpan = rowStep > 0 ? Round((height + map.TilePadding) / rowStep) : 1;
        return (Math.Max(1, column), Math.Max(1, row), Math.Max(1, columnSpan), Math.Max(1, rowSpan));
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
