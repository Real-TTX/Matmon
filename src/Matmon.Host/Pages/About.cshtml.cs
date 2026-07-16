using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

[AllowAnonymous]
public sealed class AboutModel : PageModel
{
    public string Version => MatmonVersion.Current;

    public string Channel => MatmonVersion.Channel;

    public int Year => DateTime.UtcNow.Year;
}
