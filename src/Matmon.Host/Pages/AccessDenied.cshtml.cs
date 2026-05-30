using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

[AllowAnonymous]
public sealed class AccessDeniedModel : PageModel
{
    public void OnGet()
    {
    }
}

