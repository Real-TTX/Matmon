using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Matmon.Host.Pages;

public sealed class MapEditorModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly MapDisplayProvider _displayProvider;
    private readonly ILogger<MapEditorModel> _logger;

    public MapEditorModel(IMonitoringWorkspaceStore workspaceStore, MapDisplayProvider displayProvider, ILogger<MapEditorModel> logger)
    {
        _workspaceStore = workspaceStore;
        _displayProvider = displayProvider;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? MapId { get; set; }

    [BindProperty]
    public MapEditorInput Input { get; set; } = new();

    public IReadOnlyList<Matmon.Host.Ui.ElementPickerOption> TilePickerOptions { get; private set; } = [];

    /// <summary>Designer render models for the CURRENT tiles, index-aligned with <see cref="MapEditorInput.Tiles"/>
    /// - combines each bound tile's own position/appearance with its live display data (state/value/graph) when
    /// one is already resolvable, so the designer shows the real tile instead of a hand-built mock.</summary>
    public IReadOnlyList<MapTileRenderModel> TileRenderModels { get; private set; } = [];

    public IReadOnlyList<SelectListItem> GraphTypeOptions { get; } =
    [
        new("Line", MonitoringMapTileGraphType.Line.ToString()),
        new("Area", MonitoringMapTileGraphType.Area.ToString()),
        new("Bars", MonitoringMapTileGraphType.Bars.ToString()),
        new("Smooth", MonitoringMapTileGraphType.Smooth.ToString())
    ];

    public IReadOnlyList<MonitoringMapDisplayPresetInfo> DisplayPresetOptions { get; } = MonitoringMapDisplayPresetCatalog.All;

    /// <summary>Everything the designer JS needs that is authored server-side, in ONE
    /// <c>&lt;script type="application/json" data-map-designer-config&gt;</c> block: the single
    /// <see cref="MonitoringMapTileConstraints"/> table (a second hand-duplicated JS copy could silently
    /// drift), the <see cref="MapWidgetCatalog"/> palette and its layout templates. The JS needs the widget
    /// catalog too because a template slot names a widget key, not a tile kind.</summary>
    public IReadOnlyDictionary<string, object> DesignerConfigJson { get; } = BuildDesignerConfigJson();

    public bool IsCreateMode => !Input.Id.HasValue || Input.Id.Value == Guid.Empty;

    public IActionResult OnGet()
    {
        LoadEditor(MapId);
        return Page();
    }

    /// <summary>Live tile preview for the designer: resolves the real value / state / graph for a target the
    /// moment it is picked, so designing shows actual data instead of only the last saved snapshot.</summary>
    public IActionResult OnGetTilePreview(string? token, MonitoringMapTileKind kind, MonitoringMapTileVisualType visualType, MonitoringMapTileGraphType graphType)
    {
        var tile = new MonitoringMapTile
        {
            Id = Guid.Empty,
            Kind = kind,
            VisualType = visualType,
            GraphType = graphType,
            ElementId = MonitoringTargetResolver.ElementId(token),
            TargetTag = MonitoringTargetResolver.TagName(token)
        };

        var vm = _displayProvider.ResolveTilePreview(tile);
        return new JsonResult(new
        {
            value = vm.Value,
            hasValue = !string.IsNullOrWhiteSpace(vm.Value),
            stateKey = vm.StateKey,
            stateLabel = vm.StateLabel,
            subtitle = vm.Subtitle,
            progressPercent = vm.ProgressPercent,
            progressLabel = vm.ProgressLabel,
            graphLinePath = vm.GraphLinePath
        });
    }

    /// <summary>AJAX image upload. A full form post would save the picture but throw away every unsaved tile
    /// position on the canvas, so this returns JSON and the designer patches the tile in place.</summary>
    public IActionResult OnPostUploadImage(IFormFile? file, [FromServices] MapAssetStore assets)
    {
        if (file is null || file.Length == 0)
        {
            return new JsonResult(new { error = "No file selected." });
        }

        using var stream = file.OpenReadStream();
        var id = assets.Save(stream, out var error);
        return new JsonResult(id is { } assetId
            ? new { id = (string?)assetId.ToString(), error = (string?)null }
            : new { id = (string?)null, error });
    }

    public IActionResult OnPostSave()
    {
        try
        {
            var draft = new MonitoringMap
            {
                Name = Input.Name,
                Description = Input.Description,
                AspectRatioWidth = Input.AspectRatioWidth,
                AspectRatioHeight = Input.AspectRatioHeight,
                WallboardFit = Input.WallboardFit,
                DisplayTimeZoneId = string.IsNullOrWhiteSpace(Input.DisplayTimeZoneId) ? null : Input.DisplayTimeZoneId.Trim(),
                AutoRotateSeconds = Input.AutoRotateSeconds,
                AutoRotateEnabled = Input.AutoRotateEnabled,
                PaginationMode = Input.PaginationMode,
                PublicEnabled = Input.PublicEnabled,
                ShowSlideHeaders = Input.ShowSlideHeaders,
                Columns = Input.Columns,
                Rows = Input.Rows,
                TilePadding = Input.TilePadding,
                OuterMargin = Input.OuterMargin,
                Slides = BuildSlidesFromInput().ToList()
            };

            var mapId = Input.Id ?? Guid.Empty;
            MonitoringMap map;
            if (mapId == Guid.Empty)
            {
                map = _workspaceStore.CreateMap(draft);
            }
            else
            {
                if (!_workspaceStore.UpdateMap(mapId, draft))
                {
                    return NotFound();
                }

                map = _workspaceStore.FindMap(mapId)!;
            }

            return RedirectToPage("/Maps", new { mapId = map.Id });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            LoadElementOptions();
            return Page();
        }
    }

    private IReadOnlyList<MonitoringMapSlide> BuildSlidesFromInput()
    {
        var slideDefs = Input.Slides
            .Where(slide => slide.Id != Guid.Empty)
            .GroupBy(slide => slide.Id)
            .Select(group => group.First())
            .ToList();

        if (slideDefs.Count == 0)
        {
            slideDefs.Add(new MapSlideInput { Id = Guid.NewGuid(), Name = "Slide 1" });
        }

        var validIds = slideDefs.Select(slide => slide.Id).ToHashSet();
        var firstId = slideDefs[0].Id;

        return slideDefs
            .Select(def => new MonitoringMapSlide
            {
                Id = def.Id,
                Name = def.Name,
                Title = def.Title,
                Subtitle = def.Subtitle,
                DurationSeconds = def.DurationSeconds,
                BackgroundColor = def.BackgroundColor,
                ShowHeader = def.ShowHeader,
                Tiles = Input.Tiles
                    .Where(tile => !tile.IsDeleted)
                    .Where(tile => (validIds.Contains(tile.SlideId) ? tile.SlideId : firstId) == def.Id)
                    .Select(ToTile)
                    .ToList()
            })
            .ToList();
    }

    /// <summary>Shared by the read and write side so the pin JSON round-trips symmetrically. The string-enum
    /// converter is NOT optional: JsonSerializerDefaults.Web does not include one, so "style":"Dot" throws and
    /// - before this - the catch below silently returned an empty list and every pin vanished on save.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions PinJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private MonitoringMapTile ToTile(MapTileInput tile) => new()
    {
        Id = tile.Id == Guid.Empty ? Guid.NewGuid() : tile.Id,
        Kind = tile.Kind,
        Title = tile.Title,
        ElementId = MonitoringTargetResolver.ElementId(tile.TargetToken)
            ?? (string.IsNullOrEmpty(tile.TargetToken) && tile.ElementId != Guid.Empty ? tile.ElementId : null),
        TargetTag = MonitoringTargetResolver.TagName(tile.TargetToken),
        Text = tile.Text,
        Column = Math.Max(1, tile.Column),
        Row = Math.Max(1, tile.Row),
        ColumnSpan = Math.Max(1, tile.ColumnSpan),
        RowSpan = Math.Max(1, tile.RowSpan),
        BackgroundColor = tile.BackgroundColor,
        AccentColor = tile.AccentColor,
        TextColor = tile.TextColor,
        GraphType = tile.GraphType,
        VisualType = tile.VisualType,
        IconKey = string.IsNullOrWhiteSpace(tile.IconKey) ? null : tile.IconKey.Trim(),
        ShowCard = tile.ShowCard,
        ShowTitle = tile.ShowTitle,
        ShowStateBadge = tile.ShowStateBadge,
        ShowElementName = tile.ShowElementName,
        ListMode = tile.ListMode,
        ListLimit = Math.Clamp(tile.ListLimit, 1, 50),
        ListChannelKey = string.IsNullOrWhiteSpace(tile.ListChannelKey) ? null : tile.ListChannelKey.Trim(),
        SlaWindowDays = Math.Clamp(tile.SlaWindowDays, 1, 365),
        ImageAssetId = tile.ImageAssetId,
        ImageFit = tile.ImageFit,
        Pins = ParsePins(tile.PinsJson),
        // Only real #rrggbb values are stored - an unparseable one would just be ignored at render time, so
        // keeping it would leave the user staring at a value that does nothing.
        ColorRules = tile.ColorRules
            .Where(entry => BrandingSafety.SafeHexColor(entry.Value) is not null)
            .ToDictionary(entry => entry.Key, entry => BrandingSafety.SafeHexColor(entry.Value)!),
        RefreshSeconds = Math.Clamp(tile.RefreshSeconds, 0, 3600)
    };

    /// <summary>Parses the designer's pin JSON defensively - a malformed blob costs the tile its pins, never
    /// the whole save.</summary>
    private List<MonitoringMapPin> ParsePins(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<MonitoringMapPin>>(json, PinJsonOptions) ?? [];
        }
        catch (System.Text.Json.JsonException exception)
        {
            // Losing the pins beats losing the whole save - but say so, because a silent empty list here is
            // indistinguishable from "the user deleted them".
            _logger.LogWarning(exception, "Could not parse the pin JSON for a map tile; its pins were dropped.");
            return [];
        }
    }

    public IActionResult OnPostDelete()
    {
        if (Input.Id is not Guid mapId || mapId == Guid.Empty)
        {
            return RedirectToPage("/Maps");
        }

        _workspaceStore.DeleteMap(mapId);
        return RedirectToPage("/Maps");
    }

    private void LoadEditor(Guid? mapId)
    {
        if (mapId is Guid id && _workspaceStore.FindMap(id) is { } map)
        {
            var slides = map.EffectiveSlides();
            Input = new MapEditorInput
            {
                Id = map.Id,
                Name = map.Name,
                Description = map.Description,
                AspectRatioWidth = map.AspectRatioWidth > 0 ? map.AspectRatioWidth : 16,
                AspectRatioHeight = map.AspectRatioHeight > 0 ? map.AspectRatioHeight : 9,
                WallboardFit = map.WallboardFit,
                DisplayTimeZoneId = map.DisplayTimeZoneId,
                AutoRotateSeconds = map.AutoRotateSeconds,
                AutoRotateEnabled = map.AutoRotateEnabled,
                PaginationMode = map.PaginationMode,
                PublicEnabled = map.PublicEnabled,
                ShowSlideHeaders = map.ShowSlideHeaders,
                Columns = map.Columns,
                Rows = map.Rows,
                TilePadding = map.TilePadding,
                OuterMargin = map.OuterMargin,
                Slides = slides.Select(slide => new MapSlideInput
                {
                    Id = slide.Id,
                    Name = slide.Name,
                    Title = slide.Title,
                    Subtitle = slide.Subtitle,
                    DurationSeconds = slide.DurationSeconds,
                    BackgroundColor = slide.BackgroundColor,
                    ShowHeader = slide.ShowHeader
                }).ToList(),
                Tiles = slides.SelectMany(slide => slide.Tiles.Select(tile => new MapTileInput
                {
                    Id = tile.Id,
                    SlideId = slide.Id,
                    Kind = tile.Kind,
                    Title = tile.Title,
                    ElementId = tile.ElementId,
                    TargetToken = tile.TargetTag is { } tag
                        ? MonitoringTargetResolver.ForTag(tag)
                        : tile.ElementId is { } eid ? MonitoringTargetResolver.ForElement(eid) : null,
                    Text = tile.Text,
                    Column = tile.Column,
                    Row = tile.Row,
                    ColumnSpan = tile.ColumnSpan,
                    RowSpan = tile.RowSpan,
                    BackgroundColor = tile.BackgroundColor,
                    AccentColor = tile.AccentColor,
                    TextColor = tile.TextColor,
                    GraphType = tile.GraphType,
                    VisualType = tile.VisualType,
                    IconKey = tile.IconKey,
                    ShowCard = tile.ShowCard,
                    ShowTitle = tile.ShowTitle,
                    ShowStateBadge = tile.ShowStateBadge,
                    ShowElementName = tile.ShowElementName,
                    ListMode = tile.ListMode,
                    ListLimit = tile.ListLimit,
                    ListChannelKey = tile.ListChannelKey,
                    SlaWindowDays = tile.SlaWindowDays,
                    ImageAssetId = tile.ImageAssetId,
                    ImageFit = tile.ImageFit,
                    PinsJson = tile.Pins.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(tile.Pins, PinJsonOptions),
                    ColorRules = new Dictionary<string, string>(tile.ColorRules),
                    RefreshSeconds = tile.RefreshSeconds
                })).ToList()
            };

            var display = _displayProvider.Build(map);
            var previewsByTileId = display.Tiles
                .Where(vm => vm.Tile.Id != Guid.Empty)
                .GroupBy(vm => vm.Tile.Id)
                .ToDictionary(group => group.Key, group => group.First());

            var renderMap = BuildInputMapForRender();
            TileRenderModels = Input.Tiles.Select((tileInput, index) =>
            {
                var domainTile = ToTile(tileInput);
                return previewsByTileId.TryGetValue(tileInput.Id, out var preview)
                    ? MapTileRenderModel.FromDisplay(preview, renderMap, editable: true, index: index, slideId: tileInput.SlideId, tileOverride: domainTile)
                    : MapTileRenderModel.Placeholder(domainTile, renderMap, index, tileInput.SlideId);
            }).ToArray();
        }
        else
        {
            var defaultSlideId = Guid.NewGuid();
            var defaultTileId = Guid.NewGuid();
            Input = new MapEditorInput
            {
                Id = null,
                Name = "New Map",
                Description = "Wall display for the office.",
                AutoRotateSeconds = 12,
                PaginationMode = MonitoringMapPaginationMode.Below,
                Slides = [new MapSlideInput { Id = defaultSlideId, Name = "Slide 1" }],
                Tiles =
                [
                    new MapTileInput
                    {
                        Id = defaultTileId,
                        SlideId = defaultSlideId,
                        Kind = MonitoringMapTileKind.Status,
                        Title = "Status",
                        Column = 1,
                        Row = 1,
                        ColumnSpan = 4,
                        RowSpan = 2
                    }
                ]
            };

            var renderMap = BuildInputMapForRender();
            TileRenderModels = Input.Tiles.Select((tileInput, index) =>
                MapTileRenderModel.Placeholder(ToTile(tileInput), renderMap, index, tileInput.SlideId)).ToArray();
        }

        LoadElementOptions();
    }

    /// <summary>A transient (unsaved) <see cref="MonitoringMap"/> reflecting the currently-bound <see cref="Input"/>
    /// geometry fields - just enough for <see cref="MonitoringMapGeometry.PixelRect"/> to compute the designer's
    /// tile render rects, in BOTH create mode (no real map exists yet) and edit mode (mirrors the loaded map).</summary>
    private MonitoringMap BuildInputMapForRender()
    {
        var (logicalWidth, logicalHeight) = MonitoringMap.LogicalSizeFor(Input.AspectRatioWidth, Input.AspectRatioHeight);
        return new MonitoringMap
        {
            Columns = Input.Columns,
            Rows = Input.Rows,
            TilePadding = Input.TilePadding,
            OuterMargin = Input.OuterMargin,
            LogicalWidth = logicalWidth,
            LogicalHeight = logicalHeight
        };
    }

    private void LoadElementOptions()
    {
        var root = _workspaceStore.GetAllElements().FirstOrDefault(element => element.ParentId is null);
        TilePickerOptions = Matmon.Host.Ui.ElementPickerOptions.Build(root);
    }

    private static IReadOnlyDictionary<string, object> BuildDesignerConfigJson()
    {
        var constraints = new Dictionary<string, object>();
        foreach (var kind in Enum.GetValues<MonitoringMapTileKind>())
        {
            var (minColumns, minRows, defaultColumns, defaultRows) = MonitoringMapTileConstraints.For(kind);
            constraints[kind.ToString()] = new { minColumns, minRows, defaultColumns, defaultRows };
        }

        var widgets = MapWidgetCatalog.All.ToDictionary(
            widget => widget.Key,
            object (widget) => new
            {
                kind = widget.Kind.ToString(),
                visual = widget.VisualType.ToString(),
                title = widget.Label
            });

        var templates = MapWidgetCatalog.LayoutTemplates.Select(template => new
        {
            key = template.Key,
            label = template.Label,
            minColumns = template.MinColumns,
            minRows = template.MinRows,
            slots = template.Slots.Select(slot => new
            {
                widget = slot.WidgetKey,
                column = slot.Column,
                row = slot.Row,
                columnSpan = slot.ColumnSpan,
                rowSpan = slot.RowSpan
            })
        });

        return new Dictionary<string, object>
        {
            ["constraints"] = constraints,
            ["widgets"] = widgets,
            ["templates"] = templates
        };
    }
}

public sealed class MapEditorInput
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = "Map";

    public string? Description { get; set; }

    public int AspectRatioWidth { get; set; } = 16;

    public int AspectRatioHeight { get; set; } = 9;

    public MonitoringMapWallboardFit WallboardFit { get; set; } = MonitoringMapWallboardFit.Fit;

    /// <summary>IANA timezone for the board clock/timestamps - per map, because the public wallboard has no
    /// signed-in user whose preference could apply.</summary>
    public string? DisplayTimeZoneId { get; set; }

    public bool AutoRotateEnabled { get; set; } = true;

    public int AutoRotateSeconds { get; set; } = 12;

    public MonitoringMapPaginationMode PaginationMode { get; set; } = MonitoringMapPaginationMode.Below;

    /// <summary>Round-trips unchanged through save (no visible toggle yet - that is Phase D's public-link
    /// opt-in/copy/QR UI); a hidden form field carries it so editing a map never silently resets it.</summary>
    public bool PublicEnabled { get; set; }

    public bool ShowSlideHeaders { get; set; } = true;

    /// <summary>Grid column/row count and cell gap/margin - authoritative geometry under v2, but there is no
    /// dedicated "grid size" UI yet (that is the mockup editor rebuild); hidden fields round-trip the map's
    /// current values so editing a map never silently resets its grid to the defaults below.</summary>
    public int Columns { get; set; } = 12;

    public int Rows { get; set; } = 6;

    public int TilePadding { get; set; } = 16;

    public int OuterMargin { get; set; } = 24;

    public List<MapTileInput> Tiles { get; set; } = [];

    public List<MapSlideInput> Slides { get; set; } = [];
}

public sealed class MapSlideInput
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "Slide";

    /// <summary>Round-trips unchanged through save - no editing UI yet (Phase B's slide-properties panel).</summary>
    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    public int? DurationSeconds { get; set; }

    public string? BackgroundColor { get; set; }

    public bool ShowHeader { get; set; } = true;
}

public sealed class MapTileInput
{
    public Guid Id { get; set; }

    public Guid SlideId { get; set; }

    public MonitoringMapTileKind Kind { get; set; } = MonitoringMapTileKind.Element;

    public string Title { get; set; } = "Tile";

    public Guid? ElementId { get; set; }

    /// <summary>
    /// The tile's target token from the picker: a GUID (element) or "tag:&lt;name&gt;" (tag).
    /// Parsed into <see cref="MonitoringMapTile.ElementId"/> / <see cref="MonitoringMapTile.TargetTag"/>.
    /// </summary>
    public string? TargetToken { get; set; }

    public string? Text { get; set; }

    /// <summary>1-based grid cell coordinates - see <see cref="MonitoringMap.Columns"/>/<see cref="MonitoringMap.Rows"/>.</summary>
    public int Column { get; set; } = 1;

    public int Row { get; set; } = 1;

    public int ColumnSpan { get; set; } = 2;

    public int RowSpan { get; set; } = 2;

    public string? BackgroundColor { get; set; }

    public string? AccentColor { get; set; }

    public string? TextColor { get; set; }

    public MonitoringMapTileGraphType GraphType { get; set; } = MonitoringMapTileGraphType.Line;

    public MonitoringMapTileVisualType VisualType { get; set; } = MonitoringMapTileVisualType.Card;

    public string? IconKey { get; set; }

    public bool ShowCard { get; set; } = true;

    public bool ShowTitle { get; set; } = true;

    public bool ShowStateBadge { get; set; } = true;

    public bool ShowElementName { get; set; } = true;

    public MonitoringMapListMode ListMode { get; set; } = MonitoringMapListMode.Worst;

    public int ListLimit { get; set; } = 5;

    public string? ListChannelKey { get; set; }

    public int SlaWindowDays { get; set; } = 7;

    public Guid? ImageAssetId { get; set; }

    public MonitoringMapImageFit ImageFit { get; set; } = MonitoringMapImageFit.Contain;

    /// <summary>The tile's pins as JSON, not as bound <c>Pins[i].X</c> fields. Pins are an unbounded
    /// per-tile collection edited entirely client-side (you place one by clicking the picture), and the
    /// designer clones whole tiles by rewriting their field names - index-based binding would mean
    /// re-indexing a nested collection on every clone and delete. One opaque string moves with the tile.</summary>
    public string? PinsJson { get; set; }

    /// <summary>Per-state colour overrides, keyed by the MonitoringMapColorRules buckets. Bound as an indexed
    /// dictionary (Input.Tiles[i].ColorRules[up]) - it is a fixed, tiny key set, so unlike the pins it does
    /// not need the JSON treatment.</summary>
    public Dictionary<string, string> ColorRules { get; set; } = [];

    public int RefreshSeconds { get; set; }

    public bool IsDeleted { get; set; }
}
