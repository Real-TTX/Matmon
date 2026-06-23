using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public class WizardModel : PageModel
{
    /// <summary>Ordered wizard steps. "welcome" and "done" bookend the optional action steps.</summary>
    public static readonly string[] StepOrder = ["welcome", "networks", "discovery", "probes", "notifications", "done"];

    public string Step { get; private set; } = "welcome";

    public IReadOnlyList<string> DetectedNetworks { get; private set; } = [];

    public int StepIndex => Math.Max(0, Array.IndexOf(StepOrder, Step));

    public int ActionStepNumber => StepIndex;          // welcome = 0, first action = 1, …

    public int ActionStepCount => StepOrder.Length - 2; // exclude welcome + done

    public string? PrevStep => StepIndex > 0 ? StepOrder[StepIndex - 1] : null;

    public string? NextStep => StepIndex < StepOrder.Length - 1 ? StepOrder[StepIndex + 1] : null;

    public void OnGet(string? step)
    {
        Step = (step ?? string.Empty).Trim().ToLowerInvariant();
        if (!StepOrder.Contains(Step))
        {
            Step = "welcome";
        }

        if (Step == "networks")
        {
            try
            {
                DetectedNetworks = ProbeSystemInfoProvider.Collect().Networks;
            }
            catch
            {
                DetectedNetworks = [];
            }
        }
    }
}
