using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Matmon.Host.Pages;

public sealed class MapEditorModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public MapEditorModel(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? MapId { get; set; }

    [BindProperty]
    public MapEditorInput Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> ElementOptions { get; private set; } = [];

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
        new("Card", MonitoringMapTileVisualType.Card.ToString()),
        new("Progress bar", MonitoringMapTileVisualType.ProgressBar.ToString()),
        new("Gauge", MonitoringMapTileVisualType.Gauge.ToString())
    ];

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
            var tiles = Input.Tiles
                .Where(tile => !tile.IsDeleted)
                .Select(tile => new MonitoringMapTile
                {
                    Id = tile.Id == Guid.Empty ? Guid.NewGuid() : tile.Id,
                    Kind = tile.Kind,
                    Title = tile.Title,
                    ElementId = tile.ElementId == Guid.Empty ? null : tile.ElementId,
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
                    ShowTitle = tile.ShowTitle,
                    ShowStateBadge = tile.ShowStateBadge,
                    ShowElementName = tile.ShowElementName
                })
                .ToArray();

            var mapId = Input.Id ?? Guid.Empty;
            MonitoringMap map;
            if (mapId == Guid.Empty)
            {
                map = _workspaceStore.CreateMap(Input.Name, Input.Description, Input.Columns, Input.Rows, tiles);
            }
            else
            {
                if (!_workspaceStore.UpdateMap(mapId, Input.Name, Input.Description, Input.Columns, Input.Rows, tiles))
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
            Input = new MapEditorInput
            {
                Id = map.Id,
                Name = map.Name,
                Description = map.Description,
                Columns = map.Columns,
                Rows = map.Rows,
                Tiles = map.Tiles.Select(tile => new MapTileInput
                {
                    Id = tile.Id,
                    Kind = tile.Kind,
                    Title = tile.Title,
                    ElementId = tile.ElementId,
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
                    ShowTitle = tile.ShowTitle,
                    ShowStateBadge = tile.ShowStateBadge,
                    ShowElementName = tile.ShowElementName
                }).ToList()
            };
        }
        else
        {
            Input = new MapEditorInput
            {
                Id = null,
                Name = "New Map",
                Description = "Wall display for the office.",
                Columns = 12,
                Rows = 8,
                Tiles =
                [
                    new MapTileInput
                    {
                        Id = Guid.NewGuid(),
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

    public List<MapTileInput> Tiles { get; set; } = [];
}

public sealed class MapTileInput
{
    public Guid Id { get; set; }

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

    public bool IsDeleted { get; set; }
}
