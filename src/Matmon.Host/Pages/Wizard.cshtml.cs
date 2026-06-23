using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public class WizardModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public WizardModel(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    /// <summary>Ordered wizard steps. "welcome" and "done" bookend the optional action steps.</summary>
    public static readonly string[] StepOrder = ["welcome", "networks", "discovery", "probes", "notifications", "done"];

    public string Step { get; private set; } = "welcome";

    /// <summary>Subnets already configured on the primary probe (what discovery will scan).</summary>
    public IReadOnlyList<string> ConfiguredSubnets { get; private set; } = [];

    /// <summary>Auto-detected local networks not yet configured — offered as one-click suggestions.</summary>
    public IReadOnlyList<string> SuggestedNetworks { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public int StepIndex => Math.Max(0, Array.IndexOf(StepOrder, Step));

    public int ActionStepNumber => StepIndex;          // welcome = 0, first action = 1, …

    public int ActionStepCount => StepOrder.Length - 2; // exclude welcome + done

    public string? PrevStep => StepIndex > 0 ? StepOrder[StepIndex - 1] : null;

    public string? NextStep => StepIndex < StepOrder.Length - 1 ? StepOrder[StepIndex + 1] : null;

    public void OnGet(string? step)
    {
        Load(step);
    }

    public IActionResult OnPostAddNetwork(string? step, string? cidr)
    {
        _workspaceStore.AddPrimaryProbeSubnet(cidr ?? string.Empty);
        StatusMessage = string.IsNullOrWhiteSpace(cidr) ? null : $"Added network {cidr.Trim()}.";
        return RedirectToPage(new { step = "networks" });
    }

    public IActionResult OnPostRemoveNetwork(string? cidr)
    {
        _workspaceStore.RemovePrimaryProbeSubnet(cidr ?? string.Empty);
        StatusMessage = string.IsNullOrWhiteSpace(cidr) ? null : $"Removed network {cidr.Trim()}.";
        return RedirectToPage(new { step = "networks" });
    }

    private void Load(string? step)
    {
        Step = (step ?? string.Empty).Trim().ToLowerInvariant();
        if (!StepOrder.Contains(Step))
        {
            Step = "welcome";
        }

        if (Step == "networks")
        {
            ConfiguredSubnets = _workspaceStore.GetPrimaryProbeSubnets();

            IReadOnlyList<string> detected;
            try
            {
                detected = ProbeSystemInfoProvider.Collect().Networks;
            }
            catch
            {
                detected = [];
            }

            SuggestedNetworks = detected
                .Where(net => !ConfiguredSubnets.Any(existing => string.Equals(existing, net, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
    }
}
