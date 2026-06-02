using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public class ProbesModel : PageModel
{
    private readonly IConfigurationOverviewProvider _configurationOverviewProvider;

    public ProbesModel(IConfigurationOverviewProvider configurationOverviewProvider)
    {
        _configurationOverviewProvider = configurationOverviewProvider;
    }

    public ConfigurationOverview Overview { get; private set; } = default!;

    public IReadOnlyList<SystemProbeOverview> Probes => Overview.Probes;

    public void OnGet()
    {
        Overview = _configurationOverviewProvider.GetOverview();
    }
}
