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

    public MapEditorModel(IMonitoringWorkspaceStore workspaceStore, MapDisplayProvider displayProvider)
    {
        _workspaceStore = workspaceStore;
        _displayProvider = displayProvider;
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

    /// <summary>The single <see cref="MonitoringMapTileConstraints"/> table serialized for the designer's JS
    /// (a <c>&lt;script type="application/json" data-map-constraints&gt;</c> block) - replaces a second,
    /// hand-duplicated JS table that could silently drift from the Core one.</summary>
    public IReadOnlyDictionary<string, object> TileConstraintsJson { get; } = BuildTileConstraintsJson();

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
                AutoRotateSeconds = Input.AutoRotateSeconds,
                PaginationMode = Input.PaginationMode,
                PublicEnabled = Input.PublicEnabled,
                ShowSlideHeaders = Input.ShowSlideHeaders,
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

    private static MonitoringMapTile ToTile(MapTileInput tile) => new()
    {
        Id = tile.Id == Guid.Empty ? Guid.NewGuid() : tile.Id,
        Kind = tile.Kind,
        Title = tile.Title,
        ElementId = MonitoringTargetResolver.ElementId(tile.TargetToken)
            ?? (string.IsNullOrEmpty(tile.TargetToken) && tile.ElementId != Guid.Empty ? tile.ElementId : null),
        TargetTag = MonitoringTargetResolver.TagName(tile.TargetToken),
        Text = tile.Text,
        X = tile.X,
        Y = tile.Y,
        Width = Math.Max(1, tile.Width),
        Height = Math.Max(1, tile.Height),
        BackgroundColor = tile.BackgroundColor,
        AccentColor = tile.AccentColor,
        TextColor = tile.TextColor,
        GraphType = tile.GraphType,
        VisualType = tile.VisualType,
        IconKey = string.IsNullOrWhiteSpace(tile.IconKey) ? null : tile.IconKey.Trim(),
        ShowCard = tile.ShowCard,
        ShowTitle = tile.ShowTitle,
        ShowStateBadge = tile.ShowStateBadge,
        ShowElementName = tile.ShowElementName
    };

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
                AutoRotateSeconds = map.AutoRotateSeconds,
                PaginationMode = map.PaginationMode,
                PublicEnabled = map.PublicEnabled,
                ShowSlideHeaders = map.ShowSlideHeaders,
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
                    X = tile.X,
                    Y = tile.Y,
                    Width = tile.Width,
                    Height = tile.Height,
                    BackgroundColor = tile.BackgroundColor,
                    AccentColor = tile.AccentColor,
                    TextColor = tile.TextColor,
                    GraphType = tile.GraphType,
                    VisualType = tile.VisualType,
                    IconKey = tile.IconKey,
                    ShowCard = tile.ShowCard,
                    ShowTitle = tile.ShowTitle,
                    ShowStateBadge = tile.ShowStateBadge,
                    ShowElementName = tile.ShowElementName
                })).ToList()
            };

            var display = _displayProvider.Build(map);
            var previewsByTileId = display.Tiles
                .Where(vm => vm.Tile.Id != Guid.Empty)
                .GroupBy(vm => vm.Tile.Id)
                .ToDictionary(group => group.Key, group => group.First());

            TileRenderModels = Input.Tiles.Select((tileInput, index) =>
            {
                var domainTile = ToTile(tileInput);
                return previewsByTileId.TryGetValue(tileInput.Id, out var preview)
                    ? MapTileRenderModel.FromDisplay(preview, editable: true, index: index, slideId: tileInput.SlideId, tileOverride: domainTile)
                    : MapTileRenderModel.Placeholder(domainTile, index, tileInput.SlideId);
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
                        X = 24,
                        Y = 24,
                        Width = 400,
                        Height = 200
                    }
                ]
            };

            TileRenderModels = Input.Tiles.Select((tileInput, index) =>
                MapTileRenderModel.Placeholder(ToTile(tileInput), index, tileInput.SlideId)).ToArray();
        }

        LoadElementOptions();
    }

    private void LoadElementOptions()
    {
        var root = _workspaceStore.GetAllElements().FirstOrDefault(element => element.ParentId is null);
        TilePickerOptions = Matmon.Host.Ui.ElementPickerOptions.Build(root);
    }

    private static IReadOnlyDictionary<string, object> BuildTileConstraintsJson()
    {
        var dict = new Dictionary<string, object>();
        foreach (var kind in Enum.GetValues<MonitoringMapTileKind>())
        {
            var (minWidth, minHeight, defaultWidth, defaultHeight) = MonitoringMapTileConstraints.For(kind);
            dict[kind.ToString()] = new { minWidth, minHeight, defaultWidth, defaultHeight };
        }

        dict["snapGrid"] = MonitoringMapTileConstraints.SnapGrid;
        return dict;
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

    public int AutoRotateSeconds { get; set; } = 12;

    public MonitoringMapPaginationMode PaginationMode { get; set; } = MonitoringMapPaginationMode.Below;

    /// <summary>Round-trips unchanged through save (no visible toggle yet - that is Phase D's public-link
    /// opt-in/copy/QR UI); a hidden form field carries it so editing a map never silently resets it.</summary>
    public bool PublicEnabled { get; set; }

    public bool ShowSlideHeaders { get; set; } = true;

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

    /// <summary>Logical px - see <see cref="MonitoringMap.LogicalWidth"/>.</summary>
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; } = 320;

    public int Height { get; set; } = 160;

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

    public bool IsDeleted { get; set; }
}
