using Matmon.Core.Domain;
using Matmon.Host.Services;
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

    /// <summary>Live display view-models for the SAVED tiles, keyed by tile id. Lets the designer render each
    /// tile's real state / value / graph (dimmed) underneath the edit chrome, so designing is WYSIWYG.</summary>
    public IReadOnlyDictionary<Guid, MapDisplayTileViewModel> TilePreviews { get; private set; } =
        new Dictionary<Guid, MapDisplayTileViewModel>();

    [BindProperty(SupportsGet = true)]
    public Guid? MapId { get; set; }

    [BindProperty]
    public MapEditorInput Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> ElementOptions { get; private set; } = [];

    public IReadOnlyList<Matmon.Host.Ui.ElementPickerOption> TilePickerOptions { get; private set; } = [];

    public IReadOnlyList<SelectListItem> TileKindOptions { get; } =
    [
        new("State", MonitoringMapTileKind.Element.ToString()),
        new("Value", MonitoringMapTileKind.Value.ToString()),
        new("Graph", MonitoringMapTileKind.Graph.ToString()),
        new("Summary", MonitoringMapTileKind.Status.ToString()),
        new("Text", MonitoringMapTileKind.Text.ToString())
    ];

    public IReadOnlyList<SelectListItem> GraphTypeOptions { get; } =
    [
        new("Line", MonitoringMapTileGraphType.Line.ToString()),
        new("Area", MonitoringMapTileGraphType.Area.ToString()),
        new("Bars", MonitoringMapTileGraphType.Bars.ToString()),
        new("Smooth", MonitoringMapTileGraphType.Smooth.ToString())
    ];

    public IReadOnlyList<SelectListItem> VisualTypeOptions { get; } =
    [
        new("Auto (from sensor)", MonitoringMapTileVisualType.Auto.ToString()),
        new("Card", MonitoringMapTileVisualType.Card.ToString()),
        new("Progress bar", MonitoringMapTileVisualType.ProgressBar.ToString()),
        new("Gauge", MonitoringMapTileVisualType.Gauge.ToString())
    ];

    public IReadOnlyList<MonitoringMapDisplayPresetInfo> DisplayPresetOptions { get; } = MonitoringMapDisplayPresetCatalog.All;

    public bool IsCreateMode => !Input.Id.HasValue || Input.Id.Value == Guid.Empty;

    public IActionResult OnGet()
    {
        LoadEditor(MapId);
        return Page();
    }

    public IActionResult OnPostAddTile()
    {
        Input.Tiles.Add(new MapTileInput
        {
            Id = Guid.NewGuid(),
            SlideId = Input.Slides.FirstOrDefault()?.Id ?? Guid.Empty,
            Kind = MonitoringMapTileKind.Element,
            Title = $"Tile {Input.Tiles.Count + 1}",
            X = 1,
            Y = Math.Clamp(Input.Tiles.Count + 1, 1, Math.Max(Input.Rows, 1)),
            Width = 3,
            Height = 2
        });

        LoadElementOptions();
        return Page();
    }

    public IActionResult OnPostSave()
    {
        try
        {
            var slides = BuildSlidesFromInput();

            var mapId = Input.Id ?? Guid.Empty;
            MonitoringMap map;
            if (mapId == Guid.Empty)
            {
                map = _workspaceStore.CreateMapWithSlides(Input.Name, Input.Description, Input.Columns, Input.Rows, Input.DisplayPreset, Input.AspectRatioWidth, Input.AspectRatioHeight, Input.WallboardFit, Input.AutoRotateSeconds, Input.PaginationMode, slides);
            }
            else
            {
                if (!_workspaceStore.UpdateMapWithSlides(mapId, Input.Name, Input.Description, Input.Columns, Input.Rows, Input.DisplayPreset, Input.AspectRatioWidth, Input.AspectRatioHeight, Input.WallboardFit, Input.AutoRotateSeconds, Input.PaginationMode, slides))
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
                Columns = map.Columns,
                Rows = map.Rows,
                DisplayPreset = map.DisplayPreset,
                AspectRatioWidth = map.AspectRatioWidth > 0 ? map.AspectRatioWidth : (map.DisplayPreset == MonitoringMapDisplayPreset.Ultrawide3440x1440 ? 21 : 16),
                AspectRatioHeight = map.AspectRatioHeight > 0 ? map.AspectRatioHeight : 9,
                WallboardFit = map.WallboardFit,
                AutoRotateSeconds = map.AutoRotateSeconds,
                PaginationMode = map.PaginationMode,
                Slides = slides.Select(slide => new MapSlideInput { Id = slide.Id, Name = slide.Name }).ToList(),
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
            TilePreviews = display.Tiles
                .Where(vm => vm.Tile.Id != Guid.Empty)
                .GroupBy(vm => vm.Tile.Id)
                .ToDictionary(group => group.Key, group => group.First());
        }
        else
        {
            var defaultSlideId = Guid.NewGuid();
            Input = new MapEditorInput
            {
                Id = null,
                Name = "New Map",
                Description = "Wall display for the office.",
                Columns = 12,
                Rows = 8,
                DisplayPreset = MonitoringMapDisplayPreset.FullHd1080,
                AutoRotateSeconds = 12,
                PaginationMode = MonitoringMapPaginationMode.Below,
                Slides = [new MapSlideInput { Id = defaultSlideId, Name = "Slide 1" }],
                Tiles =
                [
                    new MapTileInput
                    {
                        Id = Guid.NewGuid(),
                        SlideId = defaultSlideId,
                        Kind = MonitoringMapTileKind.Status,
                        Title = "Status",
                        X = 1,
                        Y = 1,
                        Width = 4,
                        Height = 2
                    }
                ]
            };
        }

        LoadElementOptions();
    }

    private void LoadElementOptions()
    {
        ElementOptions = _workspaceStore.GetAllElements()
            .OrderBy(element => BuildPath(element), StringComparer.OrdinalIgnoreCase)
            .Select(element => new SelectListItem(
                $"{BuildPath(element)} ({element.Kind})",
                element.Id.ToString()))
            .Prepend(new SelectListItem("No element", string.Empty))
            .ToArray();

        var root = _workspaceStore.GetAllElements().FirstOrDefault(element => element.ParentId is null);
        TilePickerOptions = Matmon.Host.Ui.ElementPickerOptions.Build(root);
    }

    private string BuildPath(MonitoringElement element)
    {
        var all = _workspaceStore.GetAllElements().ToDictionary(candidate => candidate.Id);
        var parts = new List<string>();
        var current = element;
        while (true)
        {
            parts.Add(current.Name);
            if (current.ParentId is not Guid parentId || !all.TryGetValue(parentId, out var parent))
            {
                break;
            }

            current = parent;
        }

        parts.Reverse();
        return string.Join(" / ", parts);
    }
}

public sealed class MapEditorInput
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = "Map";

    public string? Description { get; set; }

    public int Columns { get; set; } = 12;

    public int Rows { get; set; } = 8;

    public MonitoringMapDisplayPreset DisplayPreset { get; set; } = MonitoringMapDisplayPreset.FullHd1080;

    public int AspectRatioWidth { get; set; } = 16;

    public int AspectRatioHeight { get; set; } = 9;

    public MonitoringMapWallboardFit WallboardFit { get; set; } = MonitoringMapWallboardFit.Fit;

    public int AutoRotateSeconds { get; set; } = 12;

    public MonitoringMapPaginationMode PaginationMode { get; set; } = MonitoringMapPaginationMode.Below;

    public List<MapTileInput> Tiles { get; set; } = [];

    public List<MapSlideInput> Slides { get; set; } = [];
}

public sealed class MapSlideInput
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "Slide";
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

    public int X { get; set; } = 1;

    public int Y { get; set; } = 1;

    public int Width { get; set; } = 3;

    public int Height { get; set; } = 2;

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
