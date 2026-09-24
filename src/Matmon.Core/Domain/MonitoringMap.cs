using System.Text.Json.Serialization;

namespace Matmon.Core.Domain;

public sealed class MonitoringMap
{
    /// <summary>The current tile-geometry layout scheme. 0 = legacy grid cells, 1 = free positioning in
    /// logical px on a <see cref="LogicalWidth"/> x <see cref="LogicalHeight"/> canvas (Phase A), 2 = strict
    /// cell grid, 3 = a pure column FLOW with no positions at all, 4 = the current scheme: explicit cells
    /// again (<see cref="MonitoringMapTile.Column"/>/<see cref="MonitoringMapTile.Row"/> +
    /// <see cref="MonitoringMapTile.ColumnSpan"/>/<see cref="MonitoringMapTile.RowSpan"/>) on the SAME real
    /// CSS grid v3 introduced - so a widget goes where you put it and empty space stays empty, without the
    /// scaled canvas coming back. A map loaded with an older version is migrated once via
    /// <see cref="MonitoringMapLayoutMigration"/>.</summary>
    public const int CurrentLayoutVersion = 4;

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
    /// <summary>Whether the public wallboard advances slides on its own. Separate from the interval so
    /// turning rotation off does not mean losing the configured cadence.</summary>
    public bool AutoRotateEnabled { get; set; } = true;

    public int AutoRotateSeconds { get; set; } = 12;

    /// <summary>How the public wallboard shows the slide pagination / page indicator.</summary>
    public MonitoringMapPaginationMode PaginationMode { get; set; } = MonitoringMapPaginationMode.Below;

    /// <summary>IANA timezone the board renders times in (clock widget, timestamps). Per MAP, not per user:
    /// the public wallboard has nobody signed in, and a browser-local clock would contradict every
    /// server-rendered timestamp next to it. Null = the platform timezone.</summary>
    public string? DisplayTimeZoneId { get; set; }

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
        AutoRotateEnabled = AutoRotateEnabled,
        DisplayTimeZoneId = DisplayTimeZoneId,
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

    /// <summary>READING order (0-based, dense) - derived from <see cref="Row"/>/<see cref="Column"/> by
    /// <see cref="MonitoringMapPlacement.Normalize"/>, never edited directly. It is what the stacked
    /// one-column view (a phone, a narrow embedded console) reads the board in, and it fixes the DOM order so
    /// stacking needs no re-sorting in CSS.</summary>
    [JsonPropertyName("order")]
    public int Order { get; set; }

    /// <summary>The tile's 1-based grid column. Authoritative again in v4: the widget sits here, and the
    /// cells around it stay empty if that is what you laid out. Clamped into the slide's
    /// <see cref="MonitoringMap.Columns"/> by <see cref="MonitoringMapPlacement.Normalize"/>; 0 means "not
    /// placed yet" and gets the first free spot.</summary>
    [JsonPropertyName("x")]
    public int Column { get; set; }

    /// <summary>The tile's 1-based grid row, measured in the same ROW UNITS as <see cref="RowSpan"/>. Not
    /// bounded by <see cref="MonitoringMap.Rows"/> - that is how many fill one wallboard screen, not a
    /// ceiling; a taller board scrolls.</summary>
    [JsonPropertyName("y")]
    public int Row { get; set; }

    /// <summary>How many of the slide's columns this tile occupies (&gt;= 1, floored to the kind's minimum by
    /// <see cref="MonitoringMapTileConstraints.Clamp"/> and capped at <see cref="MonitoringMap.Columns"/>).</summary>
    [JsonPropertyName("width")]
    public int ColumnSpan { get; set; } = 3;

    /// <summary>The tile's height in ROW UNITS. Not a grid coordinate - a unit is a fixed slice of height
    /// (<see cref="MonitoringMap.Rows"/> of them fill one screen on a wallboard), so rows of tiles line up
    /// instead of every card finding its own height.</summary>
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

    // --- Widget-specific options ------------------------------------------------------------------------
    // Deliberately flat properties on the tile rather than a loose key/value bag: there are a handful of
    // them, they are strongly typed, and workspace.json stays readable.

    /// <summary>How a <see cref="MonitoringMapTileKind.SensorList"/> tile picks and orders its rows.</summary>
    public MonitoringMapListMode ListMode { get; set; } = MonitoringMapListMode.Worst;

    /// <summary>How many rows a list / alert-feed tile shows. Clamped at render time - a tile that is two
    /// cells tall cannot honestly show twenty rows.</summary>
    public int ListLimit { get; set; } = 5;

    /// <summary>Which channel a Value / Gauge / Progress tile reads. Null = the sensor's own default channel.
    /// Without it a dial could only ever show whichever channel happened to be default, which on a
    /// multi-channel sensor (CPU + memory + disk + SMART) is a coin toss - and the reason a gauge pointed at
    /// a host so often showed a number nobody asked for.</summary>
    public string? ChannelKey { get; set; }

    /// <summary>Gauge / progress-bar scale, in the CHANNEL'S OWN unit. Null+null means "0..100 if the channel
    /// is a percentage, otherwise no dial at all" - deliberately not a silent 0..100 fallback: treating 12 ms
    /// of latency as 12 % is a made-up reading, and a made-up reading on a wall display is worse than none.
    /// Set both to put a non-percent channel (latency, throughput, temperature) on a dial.</summary>
    public double? GaugeMin { get; set; }

    /// <inheritdoc cref="GaugeMin"/>
    public double? GaugeMax { get; set; }

    /// <summary>Channel key a <see cref="MonitoringMapListMode.TopValue"/> list ranks by. Null = the sensor's
    /// default channel. Channel keys differ per sensor type and there is no cross-type catalog, so the editor
    /// fills this picker from the channels actually observed under the target.</summary>
    public string? ListChannelKey { get; set; }

    /// <summary>Window a <see cref="MonitoringMapTileKind.Sla"/> tile reports uptime over.</summary>
    public int SlaWindowDays { get; set; } = 7;

    /// <summary>The uploaded picture behind an <see cref="MonitoringMapTileKind.Image"/> tile. Only the ID is
    /// stored - the bytes live in the MapAssetStore on disk, never in workspace.json, which is fully
    /// re-serialised on a 750ms debounce and re-parsed on every start.</summary>
    public Guid? ImageAssetId { get; set; }

    public MonitoringMapImageFit ImageFit { get; set; } = MonitoringMapImageFit.Contain;

    /// <summary>
    /// Per-state colour overrides for this tile, keyed by the bucket names in
    /// <see cref="MonitoringMapColorRules"/>. Empty = the theme's own state colours.
    /// </summary>
    public Dictionary<string, string> ColorRules { get; set; } = [];

    /// <summary>How often this tile's data may be recomputed, in seconds. This is a server-side CACHE TTL,
    /// not a per-widget timer: the board polls on one schedule, and a widget whose data is expensive (SLA)
    /// or slow-changing simply reuses its last answer for this long. 0 = the board's own cadence.</summary>
    public int RefreshSeconds { get; set; }

    /// <summary>Status markers on an image or geo tile.</summary>
    public List<MonitoringMapPin> Pins { get; set; } = [];

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
        Order = Order,
        ColumnSpan = ColumnSpan,
        RowSpan = RowSpan,
        BackgroundColor = BackgroundColor,
        AccentColor = AccentColor,
        TextColor = TextColor,
        GraphType = GraphType,
        VisualType = VisualType,
        ShowTitle = ShowTitle,
        ShowStateBadge = ShowStateBadge,
        ShowElementName = ShowElementName,
        ListMode = ListMode,
        ListLimit = ListLimit,
        ChannelKey = ChannelKey,
        GaugeMin = GaugeMin,
        GaugeMax = GaugeMax,
        ListChannelKey = ListChannelKey,
        SlaWindowDays = SlaWindowDays,
        ImageAssetId = ImageAssetId,
        ImageFit = ImageFit,
        Pins = Pins.Select(pin => pin.Clone()).ToList(),
        ColorRules = new Dictionary<string, string>(ColorRules),
        RefreshSeconds = RefreshSeconds
    };
}

public enum MonitoringMapTileKind
{
    Text = 0,
    Element = 1,
    Status = 2,
    Value = 3,
    Graph = 4,

    /// <summary>A ranked list of the sensors under a target - see <see cref="MonitoringMapListMode"/>.</summary>
    SensorList = 5,

    /// <summary>The newest active alerts, optionally scoped to a target subtree.</summary>
    AlertFeed = 6,

    /// <summary>Uptime over a window, from the downsampled statistics buckets.</summary>
    Sla = 7,

    /// <summary>A clock in the map timezone. Ticks client-side - a server-rendered time would be stale the
    /// moment the page is cached or the wallboard stops reloading.</summary>
    Clock = 8,

    /// <summary>A large section heading. Text with a display treatment, not a data widget.</summary>
    Heading = 9,

    /// <summary>An uploaded picture (floorplan, rack photo, office plan) with optional status pins on it.</summary>
    Image = 10,

    /// <summary>The shipped offline world map with pins placed by latitude/longitude.</summary>
    GeoMap = 11
}

public enum MonitoringMapListMode
{
    /// <summary>Worst state first, then by name - the "what needs attention" list.</summary>
    Worst = 0,

    /// <summary>Highest channel value first - the "top talkers" list.</summary>
    TopValue = 1,

    /// <summary>Lowest channel value first.</summary>
    BottomValue = 2,

    /// <summary>Plain alphabetical, for a stable roster that does not reshuffle on every poll.</summary>
    Alphabetical = 3
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
            // Rows need width to be readable and height to show more than one line, so the list-style widgets
            // start larger than a value tile - a 2x2 "top talkers" would show exactly one truncated row.
            MonitoringMapTileKind.SensorList => (3, 3, 4, 4),
            MonitoringMapTileKind.AlertFeed => (4, 3, 5, 4),
            MonitoringMapTileKind.Sla => (2, 2, 3, 2),
            MonitoringMapTileKind.Clock => (2, 2, 2, 2),
            MonitoringMapTileKind.Heading => (3, 1, 6, 1),
            // A picture with pins on it is useless small - you cannot hit a pin, let alone read its label.
            MonitoringMapTileKind.Image => (3, 3, 6, 4),
            MonitoringMapTileKind.GeoMap => (4, 3, 6, 4),
            _ => (2, 2, 2, 2)
        };
    }

    /// <summary>Clamps a tile's cell geometry in place: enforces the kind's minimum span (shrinking it below
    /// the grid size only if the grid itself is smaller than the minimum), then keeps the whole span inside the
    /// 1..columns / 1..rows grid. Mutates <paramref name="tile"/> - used by both the layout migration and the
    /// store's save-time normalization so an off-grid or too-small tile always lands somewhere sane rather than
    /// being rejected.</summary>
    /// <summary>The tallest a single tile may be, in row units. A tile is allowed to be taller than one
    /// screen (<see cref="MonitoringMap.Rows"/>) - a long sensor list is a legitimate thing to scroll to -
    /// but not unboundedly so, because the wallboard scales a slide to fit and one runaway tile would shrink
    /// every other tile on the board to nothing.</summary>
    public const int MaxRowSpan = 24;

    /// <summary>Clamps a tile's flow geometry in place: the kind's minimum width, capped at the slide's column
    /// count, and a height of at least the kind's minimum. v3 has no x/y to clamp - a tile cannot be placed
    /// off the board because it is never placed at all, only ordered.</summary>
    public static void Clamp(MonitoringMapTile tile, int columns, int rows)
    {
        var (minColumns, minRows, _, _) = For(tile.Kind);
        columns = Math.Max(1, columns);

        // Floor first, then cap to the slide width - in that order, so a slide narrower than the kind's usual
        // minimum still yields a tile no wider than the slide rather than one that overflows it.
        tile.ColumnSpan = Math.Min(Math.Max(minColumns, tile.ColumnSpan), columns);
        tile.RowSpan = Math.Clamp(tile.RowSpan, minRows, MaxRowSpan);
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

    /// <summary>Given a v1 (Phase A free-px) tile's raw px rect, finds the
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

/// <summary>
/// A marker on an <see cref="MonitoringMapTileKind.Image"/> (floorplan / rack photo / office plan) or a
/// <see cref="MonitoringMapTileKind.GeoMap"/> tile. Its colour follows the resolved state of
/// <see cref="TargetToken"/>, exactly like a status tile - a pin IS a status tile, just positioned on a
/// picture instead of in the grid.
/// </summary>
public sealed class MonitoringMapPin
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Element id or "tag:name" - the same single token the element picker writes and
    /// <c>IMonitoringWorkspaceStore.ResolveTargetSensors</c> understands.</summary>
    public string? TargetToken { get; set; }

    public string? Label { get; set; }

    public bool ShowLabel { get; set; } = true;

    public MonitoringMapPinStyle Style { get; set; } = MonitoringMapPinStyle.Dot;

    /// <summary>Position on the IMAGE as a fraction (0..1) of its width/height - relative, not pixels, so a
    /// pin stays where it was put no matter what size the board renders at or how the image is fitted.
    /// Ignored by a geo tile, which positions from <see cref="Latitude"/>/<see cref="Longitude"/>.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public MonitoringMapPin Clone() => new()
    {
        Id = Id,
        TargetToken = TargetToken,
        Label = Label,
        ShowLabel = ShowLabel,
        Style = Style,
        X = X,
        Y = Y,
        Latitude = Latitude,
        Longitude = Longitude
    };
}

public enum MonitoringMapPinStyle
{
    /// <summary>A small coloured dot with an optional label.</summary>
    Dot = 0,

    /// <summary>A miniature value card - the "mini tile" on a floorplan.</summary>
    Tile = 1
}

/// <summary>How an uploaded image fills its tile.</summary>
public enum MonitoringMapImageFit
{
    /// <summary>Whole image visible, letterboxed. The default, because a floorplan must not be cropped -
    /// cropping would silently move every pin relative to what the user sees.</summary>
    Contain = 0,

    /// <summary>Fills the tile, cropping the overflow.</summary>
    Cover = 1,

    /// <summary>Distorts to fill exactly.</summary>
    Stretch = 2
}

/// <summary>
/// The four colour buckets a map tile can override. Deliberately FOUR, while <see cref="SensorState"/> has
/// six: "Up / Warning / Down / Unknown" is how someone configuring a wallboard thinks, and asking them to
/// colour Disabled and Paused separately is asking a question they do not have an opinion about. The mapping
/// below is where the six collapse into the four, in one place.
/// </summary>
public static class MonitoringMapColorRules
{
    public const string Up = "up";
    public const string Warning = "warning";
    public const string Down = "down";
    public const string Unknown = "unknown";

    public static IReadOnlyList<(string Key, string Label)> Buckets { get; } =
    [
        (Up, "Up / healthy"),
        (Warning, "Warning"),
        (Down, "Down / critical"),
        (Unknown, "Unknown / paused")
    ];

    /// <summary>A representative swatch for a bucket - only what the editor's colour picker opens on when
    /// the tile has no override for it. Never used at render time: an empty rule means "keep the theme
    /// colour", which a colour input cannot express, so this is a starting point and not a default value.</summary>
    public static string DefaultHex(string bucketKey) => bucketKey switch
    {
        Up => "#3FB950",
        Warning => "#D29922",
        Down => "#F85149",
        _ => "#8B949E"
    };

    /// <summary>Which bucket a concrete sensor state falls into. Paused counts as Unknown rather than as Up:
    /// a paused sensor is not reporting, and painting it green would be a lie on a wall display.</summary>
    public static string BucketFor(SensorState state) => state switch
    {
        SensorState.Healthy => Up,
        SensorState.Warning => Warning,
        SensorState.Critical => Down,
        SensorState.Disabled => Down,
        _ => Unknown
    };

    /// <summary>The tile's override for a state, or null to keep the theme colour. Validated through
    /// <see cref="BrandingSafety.SafeHexColor"/> at the point of use, never trusted raw into CSS.</summary>
    public static string? Resolve(IReadOnlyDictionary<string, string>? rules, SensorState state) =>
        rules is not null && rules.TryGetValue(BucketFor(state), out var color)
            ? BrandingSafety.SafeHexColor(color)
            : null;

    public static string? ResolveByKey(IReadOnlyDictionary<string, string>? rules, string? stateKey)
    {
        if (rules is null || string.IsNullOrWhiteSpace(stateKey))
        {
            return null;
        }

        // The render pipeline speaks the presentation keys ("ok"/"warning"/"error"/"unknown"), not SensorState.
        var bucket = stateKey.ToLowerInvariant() switch
        {
            "ok" => Up,
            "warning" => Warning,
            "error" => Down,
            _ => Unknown
        };

        return rules.TryGetValue(bucket, out var color) ? BrandingSafety.SafeHexColor(color) : null;
    }
}
