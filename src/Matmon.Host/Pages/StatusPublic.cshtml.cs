using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>The anonymous status page. Answers only while the page's public link is on; an unknown or switched-off
/// token is a plain 404, like a page that does not exist.</summary>
[AllowAnonymous]
public sealed class StatusPublicModel(IMonitoringWorkspaceStore store, StatusPageProvider provider) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    public StatusPageView? View { get; private set; }

    public IActionResult OnGet()
    {
        var page = store.FindStatusPageByPublicToken(Token);
        if (page is null)
        {
            return NotFound();
        }

        View = provider.BuildCached(page);
        Response.Headers.CacheControl = "no-cache";
        return Page();
    }
}
