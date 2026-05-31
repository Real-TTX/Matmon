using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public sealed class ProbeInstallModel : PageModel
{
    private readonly IConfigurationOverviewProvider _configurationOverviewProvider;

    public ProbeInstallModel(IConfigurationOverviewProvider configurationOverviewProvider)
    {
        _configurationOverviewProvider = configurationOverviewProvider;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? ProbeId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public ConfigurationOverview Overview { get; private set; } = default!;

    public SystemProbeOverview? Probe { get; private set; }

    public bool CanInstall => Probe is not null && ProbeInstallCommandBuilder.CanInstallProbe(Probe);

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        LoadView();
        if (Probe is null)
        {
            ErrorMessage = "Probe not found.";
        }

        return Page();
    }

    public string BuildDockerRun()
    {
        if (Probe is null)
        {
            return string.Empty;
        }

        return ProbeInstallCommandBuilder.BuildDockerRun(Request, Overview, Probe);
    }

    public string BuildCompose()
    {
        if (Probe is null)
        {
            return string.Empty;
        }

        return ProbeInstallCommandBuilder.BuildCompose(Request, Overview, Probe);
    }

    public string BuildCurlInstaller()
    {
        if (Probe is null)
        {
            return string.Empty;
        }

        return ProbeInstallCommandBuilder.BuildCurlInstaller(Request, Overview, Probe);
    }

    public string GetSafeReturnUrl(string fallbackPage)
    {
        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return ReturnUrl;
        }

        return fallbackPage;
    }

    private void LoadView()
    {
        Overview = _configurationOverviewProvider.GetOverview();
        Probe = ProbeId is null
            ? null
            : Overview.Probes.FirstOrDefault(probe => probe.ElementId == ProbeId.Value);
    }
}
