using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public class WizardModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly DiscoveryJobStore _discoveryJobs;
    private readonly NetworkDiscoveryService _discoveryService;

    public WizardModel(
        IMonitoringWorkspaceStore workspaceStore,
        DiscoveryJobStore discoveryJobs,
        NetworkDiscoveryService discoveryService)
    {
        _workspaceStore = workspaceStore;
        _discoveryJobs = discoveryJobs;
        _discoveryService = discoveryService;
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

    /// <summary>Remote (non-primary) probes, with the values needed for their deploy command.</summary>
    public IReadOnlyList<WizardProbe> RemoteProbes { get; private set; } = [];

    public sealed record WizardProbe(string Name, string ProbeId, string Token);

    /// <summary>This server's base URL, used in the remote-probe deploy command.</summary>
    public string PrimaryUrl { get; private set; } = string.Empty;

    /// <summary>Whether e-mail notifications are already configured.</summary>
    public bool EmailConfigured { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public int StepIndex => Math.Max(0, Array.IndexOf(StepOrder, Step));

    public int ActionStepNumber => StepIndex;          // welcome = 0, first action = 1, …

    public int ActionStepCount => StepOrder.Length - 2; // exclude welcome + done

    public string? PrevStep => StepIndex > 0 ? StepOrder[StepIndex - 1] : null;

    public string? NextStep => StepIndex < StepOrder.Length - 1 ? StepOrder[StepIndex + 1] : null;

    public void OnGet(string? step)
    {
        PrimaryUrl = $"{Request.Scheme}://{Request.Host}";
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

    public IActionResult OnPostStartDiscovery()
    {
        var root = PrimaryNode();
        var subnets = _workspaceStore.GetPrimaryProbeSubnets();
        if (root is null || subnets.Count == 0)
        {
            StatusMessage = "Add at least one network first, then start a scan.";
            return RedirectToPage(new { step = subnets.Count == 0 ? "networks" : "discovery" });
        }

        var request = new NetworkDiscoveryRequest(
            Guid.NewGuid(),
            string.Join(", ", subnets),
            DiscoveryDefaults.Options,
            ScopeElementId: root.Id,
            ScopeKind: MonitoringElementKind.Probe);
        var job = _discoveryJobs.Create(root.Id, root.ProbeId, root.Name, request);
        StartScan(job);

        StatusMessage = $"Scan started on {subnets.Count} network{(subnets.Count == 1 ? string.Empty : "s")}. Found devices appear in Discovery.";
        return RedirectToPage(new { step = "discovery" });
    }

    // Mirrors DiscoveryModel.StartLocalDiscovery: run the scan on the primary off the request thread,
    // streaming results/progress into the shared DiscoveryJobStore so Discovery shows them.
    private void StartScan(DiscoveryJobSnapshot job)
    {
        _ = Task.Run(async () =>
        {
            var cancellationToken = _discoveryJobs.GetCancellationToken(job.JobId);
            try
            {
                _discoveryJobs.Start(job.JobId, "Discovery is running on the primary probe.");
                await _discoveryService.DiscoverAsync(
                    job.Request,
                    (result, _) =>
                    {
                        _discoveryJobs.AddResult(job.JobId, result);
                        return ValueTask.CompletedTask;
                    },
                    cancellationToken,
                    (progress, _) =>
                    {
                        _discoveryJobs.UpdateProgress(job.JobId, progress.ScannedHosts, progress.TotalHosts);
                        return ValueTask.CompletedTask;
                    });

                if (!_discoveryJobs.IsCancelled(job.JobId))
                {
                    _discoveryJobs.Complete(job.JobId, [], null);
                }
            }
            catch (OperationCanceledException) when (_discoveryJobs.IsCancelled(job.JobId))
            {
                // Cancelled from the UI.
            }
            catch (Exception ex)
            {
                _discoveryJobs.Complete(job.JobId, [], ex.Message);
            }
        });
    }

    public IActionResult OnPostCreateProbe(string? name)
    {
        var root = PrimaryNode();
        if (root is null)
        {
            StatusMessage = "No primary node found.";
            return RedirectToPage(new { step = "probes" });
        }

        var probe = _workspaceStore.CreateProbe(root.Id, string.IsNullOrWhiteSpace(name) ? "Remote probe" : name.Trim(), null);
        StatusMessage = $"Created remote probe '{probe.Name}'. Deploy it with the command below.";
        return RedirectToPage(new { step = "probes" });
    }

    public IActionResult OnPostConfigureNotifications(string? smtpHost, int? smtpPort, string? username, string? password, bool useSsl, string? fromEmail, string? toEmail)
    {
        var from = (fromEmail ?? string.Empty).Trim();
        var to = (toEmail ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(smtpHost) || !from.Contains('@') || !to.Contains('@'))
        {
            StatusMessage = "Enter an SMTP host and valid from/to e-mail addresses.";
            return RedirectToPage(new { step = "notifications" });
        }

        _workspaceStore.ConfigureEmailNotifications(smtpHost!.Trim(), smtpPort, username, password, useSsl, from, to);
        StatusMessage = $"E-mail alerts set up — {to} will be notified on Warning/Critical.";
        return RedirectToPage(new { step = "notifications" });
    }

    private void Load(string? step)
    {
        Step = (step ?? string.Empty).Trim().ToLowerInvariant();
        if (!StepOrder.Contains(Step))
        {
            Step = "welcome";
        }

        if (Step == "probes")
        {
            RemoteProbes = _workspaceStore.GetAllElements()
                .OfType<ProbeElement>()
                .Where(probe => probe.ParentId is not null)
                .Select(probe => new WizardProbe(probe.Name, probe.ProbeId ?? string.Empty, probe.EnrollmentToken ?? string.Empty))
                .ToArray();
        }

        if (Step == "notifications")
        {
            EmailConfigured = _workspaceStore.HasEmailNotifications();
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

        if (Step is "networks" or "discovery")
        {
            ConfiguredSubnets = _workspaceStore.GetPrimaryProbeSubnets();
        }

        if (Step == "networks")
        {
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
