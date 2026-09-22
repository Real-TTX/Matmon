using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

[AllowAnonymous]
public sealed class MapPublicModel : PageModel
{
    private readonly MapDisplayProvider _displayProvider;
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public MapPublicModel(
        IMonitoringWorkspaceStore workspaceStore,
        MapDisplayProvider displayProvider)
    {
        _workspaceStore = workspaceStore;
        _displayProvider = displayProvider;
    }

    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    public MapDisplayViewModel? Display { get; private set; }

    public IActionResult OnGet()
    {
        var map = _workspaceStore.FindMapByPublicToken(Token);
        if (map is null)
        {
            return NotFound();
        }

        Display = _displayProvider.Build(map);
        return Page();
    }

    /// <summary>The live values the board patches itself with. Anonymous like the page, and gated by the very
    /// same token lookup - so turning the public link off kills this endpoint too.</summary>
    public IActionResult OnGetData()
    {
        var map = _workspaceStore.FindMapByPublicToken(Token);
        return map is null
            ? NotFound()
            : new JsonResult(MapLiveData.Build(_displayProvider.Build(map)));
    }
}
