using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Where the sensors run - the local primary, remote Docker probes and agents - in ONE list. It used to be two (this page
/// for everyone, a System > Probes tab for admins with edit / install / token), which showed the same table twice with
/// different buttons; the admin actions are this page's row menu now, and the old tab sends its visitors here.
/// </summary>
public class ProbesModel : PageModel
{
    private readonly IConfigurationOverviewProvider _configurationOverviewProvider;

    public ProbesModel(IConfigurationOverviewProvider configurationOverviewProvider)
    {
        _configurationOverviewProvider = configurationOverviewProvider;
    }

    public ConfigurationOverview Overview { get; private set; } = default!;

    public IReadOnlyList<SystemProbeOverview> Probes => Overview.Probes;

    /// <summary>Set by the actions that live on other pages and come back here (System's token rotation).</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
        Overview = _configurationOverviewProvider.GetOverview();
    }
}
