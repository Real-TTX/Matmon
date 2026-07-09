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

    /// <summary>Board aspect-ratio numerator / denominator (e.g. 16 / 9). 0 = derive from the legacy
    /// <see cref="DisplayPreset"/>. Only the RATIO matters - the board scales to fill whatever screen it is
    /// shown on (notebook, Full-HD wall, 4K), so there are no fixed pixels.</summary>
    public int AspectRatioWidth { get; set; }

    public int AspectRatioHeight { get; set; }

    /// <summary>How the public wallboard fills a screen whose ratio differs from the map's. Defaults to Stretch
    /// so existing wallboards keep filling the screen exactly as before; Fit (letterbox) is opt-in per map.</summary>
    public MonitoringMapWallboardFit WallboardFit { get; set; } = MonitoringMapWallboardFit.Stretch;

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
}

public sealed class MonitoringMapSlide
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Slide";

    public List<MonitoringMapTile> Tiles { get; set; } = [];
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
/// Per-kind sizing rules for map tiles, in grid cells. One consistent source of truth for the designer's
/// resize clamp (previously ad-hoc / "random") and any validation, so each tile kind has a sensible floor
/// and ceiling. Maxima are capped to the board (columns/rows).
/// </summary>
public static class MonitoringMapTileConstraints
{
    /// <summary>(minWidth, minHeight, maxWidth, maxHeight) in grid cells for a tile kind. The only per-kind
    /// rule is a readability floor (a graph narrower than 3x2 or an aggregate under 2x1 is useless); every
    /// tile may grow up to the whole board, so the maximum is simply the grid size.</summary>
    public static (int MinWidth, int MinHeight, int MaxWidth, int MaxHeight) For(MonitoringMapTileKind kind, int columns, int rows)
    {
        var cols = Math.Max(1, columns);
        var rws = Math.Max(1, rows);
        var (minWidth, minHeight) = kind switch
        {
            MonitoringMapTileKind.Graph => (3, 2),
            MonitoringMapTileKind.Status => (2, 1),
            _ => (1, 1), // Text / Value / Element can be as small as a single cell
        };
        return (Math.Min(minWidth, cols), Math.Min(minHeight, rws), cols, rws);
    }
}
