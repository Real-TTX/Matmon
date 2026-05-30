using Matmon.Host.Services;
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
}
