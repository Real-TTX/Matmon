using Matmon.Core.Domain;
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
    public static readonly string[] StepOrder = ["welcome", "structure", "networks", "discovery", "probes", "notifications", "done"];

    /// <summary>Suggested starter folder tree (one level of children), created under the primary node.</summary>
    public static readonly (string Name, string[] Children)[] StarterTree =
    [
        ("Infrastructure", []),
        ("Servers", ["Hypervisors", "Virtual Machines"]),
        ("Network", []),
        ("Security", []),
        ("Storage & NAS", []),
        ("Clients", []),
        ("Peripherals", []),
    ];

    public string Step { get; private set; } = "welcome";

    /// <summary>Top-level folder names already present under the primary node.</summary>
    public IReadOnlyList<string> ExistingFolders { get; private set; } = [];

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

    public IActionResult OnPostCreateStructure()
    {
        var root = PrimaryNode();
        if (root is null)
        {
            StatusMessage = "No primary node found.";
            return RedirectToPage(new { step = "structure" });
        }

        var created = 0;
        foreach (var (name, children) in StarterTree)
        {
            var parent = FindOrCreateFolder(root.Id, name, ref created);
            foreach (var child in children)
            {
                FindOrCreateFolder(parent.Id, child, ref created);
            }
        }

        StatusMessage = created > 0
            ? $"Created {created} folder{(created == 1 ? string.Empty : "s")}."
            : "Those folders already exist.";
        return RedirectToPage(new { step = "structure" });
    }

    public IActionResult OnPostAddNetwork(string? cidr)
    {
        _workspaceStore.AddPrimaryProbeSubnet(cidr ?? string.Empty);
        StatusMessage = string.IsNullOrWhiteSpace(cidr) ? null : $"Added network {cidr.Trim()} to this node.";
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

        if (Step == "structure")
        {
            var root = PrimaryNode();
            ExistingFolders = root is null
                ? []
                : _workspaceStore.GetAllElements()
                    .OfType<FolderElement>()
                    .Where(folder => folder.ParentId == root.Id)
                    .Select(folder => folder.Name)
                    .ToArray();
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

    private ProbeElement? PrimaryNode() =>
        _workspaceStore.GetAllElements().OfType<ProbeElement>().FirstOrDefault(probe => probe.ParentId is null);

    private FolderElement FindOrCreateFolder(Guid parentId, string name, ref int created)
    {
        var existing = _workspaceStore.GetAllElements()
            .OfType<FolderElement>()
            .FirstOrDefault(folder => folder.ParentId == parentId &&
                                      string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        created++;
        return _workspaceStore.CreateFolder(parentId, name, null);
    }
}
