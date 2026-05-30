using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public sealed class MapsModel : PageModel
{
    private readonly MapDisplayProvider _displayProvider;
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public MapsModel(
        IMonitoringWorkspaceStore workspaceStore,
        MapDisplayProvider displayProvider)
    {
        _workspaceStore = workspaceStore;
        _displayProvider = displayProvider;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? MapId { get; set; }

    public IReadOnlyList<MonitoringMap> Maps { get; private set; } = [];

    public MapDisplayViewModel? Display { get; private set; }

    public MonitoringMap? SelectedMap => Display?.Map;

    public bool CanEditMaps => MatmonSecurity.IsAdmin(User);

    public IActionResult OnGet()
    {
        LoadMaps();
        return Page();
    }

    public IActionResult OnPostRotatePublicLink(Guid mapId)
    {
        if (!CanEditMaps)
        {
            return Forbid();
        }

        _workspaceStore.RotateMapPublicToken(mapId);
        return RedirectToPage("/Maps", new { mapId });
    }

    private void LoadMaps()
    {
        Maps = _workspaceStore.GetMaps();
        var selectedMap = MapId is Guid requestedId
            ? Maps.FirstOrDefault(map => map.Id == requestedId)
            : null;

        Display = selectedMap is null ? null : _displayProvider.Build(selectedMap);
    }
}
