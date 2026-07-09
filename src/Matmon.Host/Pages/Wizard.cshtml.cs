using System.Net.Http.Json;
using System.Security.Claims;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public class WizardModel : PageModel
{
    private static readonly HttpClient CloudHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string TotpIssuer = "Matmon";

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly DiscoveryJobStore _discoveryJobs;
    private readonly NetworkDiscoveryService _discoveryService;
    private readonly ILicenseService _licenseService;

    public WizardModel(
        IMonitoringWorkspaceStore workspaceStore,
        DiscoveryJobStore discoveryJobs,
        NetworkDiscoveryService discoveryService,
        ILicenseService licenseService)
    {
        _workspaceStore = workspaceStore;
        _discoveryJobs = discoveryJobs;
        _discoveryService = discoveryService;
        _licenseService = licenseService;
    }

    // Reworked flow: the cloud decision comes first (it determines whether SMTP + a manual license are even
    // needed), then the merged setup steps. "welcome"/"done" bookend the optional action steps.
    //   cloud          - connect to Matmon.Cloud (+ license note, offline-notify)
    //   notifications  - alert delivery: via the cloud relay when connected, else local SMTP
    //   structure      - folder tree + the networks to monitor (merged)
    //   discovery      - scan those networks + add remote probes (merged)
    //   twofactor      - secure the admin account (TOTP)
    public static readonly string[] StepOrder = ["welcome", "cloud", "notifications", "structure", "discovery", "twofactor", "done"];

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

    /// <summary>Per-folder present/missing status of the starter tree (idempotent: only missing ones get created).</summary>
    public IReadOnlyList<StarterFolderStatus> StarterFolders { get; private set; } = [];

    /// <summary>How many starter folders are still missing (0 = the whole structure already exists).</summary>
    public int MissingFolderCount { get; private set; }

    /// <summary>True once every starter folder exists - the create button then shows a done state.</summary>
    public bool AllFoldersExist => StarterFolders.Count > 0 && MissingFolderCount == 0;

    public sealed record StarterFolderStatus(string Label, int Depth, bool Exists);

    /// <summary>Subnets already configured on the primary probe (what discovery will scan).</summary>
    public IReadOnlyList<string> ConfiguredSubnets { get; private set; } = [];

    /// <summary>Auto-detected local networks not yet configured - offered as one-click suggestions.</summary>
    public IReadOnlyList<string> SuggestedNetworks { get; private set; } = [];

    /// <summary>Remote (non-primary) probes, with the values needed for their deploy command.</summary>
    public IReadOnlyList<WizardProbe> RemoteProbes { get; private set; } = [];

    public sealed record WizardProbe(string Name, string ProbeId, string Token);

    /// <summary>This server's base URL, used in the remote-probe deploy command.</summary>
    public string PrimaryUrl { get; private set; } = string.Empty;

    /// <summary>Whether e-mail notifications are already configured.</summary>
    public bool EmailConfigured { get; private set; }

    /// <summary>Whether this instance is already linked to Matmon.Cloud.</summary>
    public bool CloudConnected { get; private set; }

    /// <summary>Whether alerts are being relayed through Matmon.Cloud (so local SMTP isn't needed).</summary>
    public bool CloudRelayEnabled { get; private set; }

    /// <summary>The cloud URL this instance is linked to (when connected).</summary>
    public string CloudLinkUrl { get; private set; } = string.Empty;

    /// <summary>Default cloud address offered in the connect form.</summary>
    public string DefaultCloudUrl => "https://cloud.matmon.eu";

    /// <summary>Suggested instance name for the cloud link (this node's name / host name).</summary>
    public string SuggestedInstanceName { get; private set; } = string.Empty;

    // --- 2FA (TOTP) enrollment - same store API as the Account page ---
    public bool TwoFactorEnabled { get; private set; }
    public TotpEnrollmentInfo? Enrollment { get; private set; }
    public string? QrSvg => Enrollment is null ? null : TotpQr.Svg(Enrollment.OtpauthUri);
    [BindProperty] public string? TotpCode { get; set; }

    // --- License (shown inside the cloud step) ---
    public LicenseInfo License { get; private set; } = LicenseInfo.Fallback();
    /// <summary>While connected the cloud owns the license (re-issued each heartbeat), so manual entry is refused.</summary>
    public bool CloudManagesLicense { get; private set; }
    [BindProperty] public string? LicenseTokenInput { get; set; }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public int StepIndex => Math.Max(0, Array.IndexOf(StepOrder, Step));

    /// <summary>Human step number (1-based) and total, for the "2 / 7" progress counter.</summary>
    public int StepNumber => StepIndex + 1;

    public int StepCount => StepOrder.Length;

    /// <summary>Progress-bar fill 0-100, reaching 100% on the final step.</summary>
    public int ProgressPercent => (int)Math.Round(100.0 * StepNumber / StepCount);

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
        return RedirectToPage(new { step = "structure" });
    }

    public IActionResult OnPostRemoveNetwork(string? cidr)
    {
        _workspaceStore.RemovePrimaryProbeSubnet(cidr ?? string.Empty);
        StatusMessage = string.IsNullOrWhiteSpace(cidr) ? null : $"Removed network {cidr.Trim()}.";
        return RedirectToPage(new { step = "structure" });
    }

    public IActionResult OnPostStartDiscovery()
    {
        var root = PrimaryNode();
        var subnets = _workspaceStore.GetPrimaryProbeSubnets();
        if (root is null || subnets.Count == 0)
        {
            StatusMessage = "Add at least one network first (previous step), then start a scan.";
            return RedirectToPage(new { step = subnets.Count == 0 ? "structure" : "discovery" });
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
            return RedirectToPage(new { step = "discovery" });
        }

        var probe = _workspaceStore.CreateProbe(root.Id, string.IsNullOrWhiteSpace(name) ? "Remote probe" : name.Trim(), null);
        StatusMessage = $"Created remote probe '{probe.Name}'. Deploy it with the command below.";
        return RedirectToPage(new { step = "discovery" });
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
        StatusMessage = $"E-mail alerts set up - {to} will be notified on Warning/Critical.";
        return RedirectToPage(new { step = "notifications" });
    }

    /// <summary>Notifications step: route alert e-mail through the connected Matmon.Cloud gateway (no local SMTP
    /// needed) by enabling the built-in "Matmon Cloud" relay sender. Toggle off to disable it again.</summary>
    public IActionResult OnPostEnableCloudRelay(bool enable)
    {
        var cloud = _workspaceStore.GetCloudConnectionSettings();
        if (enable && !(cloud.Enabled && cloud.HasToken))
        {
            StatusMessage = "Connect to Matmon.Cloud first (previous step) to relay alerts through it.";
            return RedirectToPage(new { step = "notifications" });
        }

        _workspaceStore.SetCloudRelaySettings(enable);
        StatusMessage = enable
            ? "Alerts will be delivered through Matmon.Cloud - no local SMTP server needed."
            : "Cloud alert delivery turned off.";
        return RedirectToPage(new { step = "notifications" });
    }

    /// <summary>Cloud step: enable/disable the cloud-side offline notification for this instance (token-authed call
    /// to the just-connected cloud). Default on, so a fresh Free instance is watched out of the box.</summary>
    public async Task<IActionResult> OnPostCloudMonitoringAsync(bool notifyOnProblems, CancellationToken cancellationToken)
    {
        var settings = _workspaceStore.GetCloudConnectionSettings();
        var url = (settings.Url ?? string.Empty).Trim().TrimEnd('/');
        var token = _workspaceStore.GetCloudConnectionToken();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(settings.InstanceId) || string.IsNullOrWhiteSpace(token))
        {
            StatusMessage = "Connect to Matmon.Cloud first, then choose notifications.";
            return RedirectToPage(new { step = "cloud" });
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/instances/{settings.InstanceId}/monitoring")
            {
                Content = JsonContent.Create(new { notifyOnProblems })
            };
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await CloudHttp.SendAsync(request, cancellationToken);
            StatusMessage = response.IsSuccessStatusCode
                ? (notifyOnProblems ? "Matmon.Cloud will e-mail you if this instance goes offline." : "Offline notifications turned off.")
                : $"Matmon.Cloud rejected the change ({(int)response.StatusCode}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not reach Matmon.Cloud: {ex.Message}";
        }

        return RedirectToPage(new { step = "cloud" });
    }

    /// <summary>2FA step: begin TOTP enrollment (generate + store a secret, show its QR). 2FA stays OFF until a
    /// code confirms it.</summary>
    public IActionResult OnPostStartTotp()
    {
        PrimaryUrl = $"{Request.Scheme}://{Request.Host}";
        Load("twofactor");
        if (TwoFactorEnabled) { return RedirectToPage(new { step = "twofactor" }); }
        Enrollment = _workspaceStore.BeginTotpEnrollment(UserId, TotpIssuer);
        return Page();
    }

    /// <summary>2FA step: confirm enrollment with a code from the authenticator; turns 2FA on.</summary>
    public IActionResult OnPostConfirmTotp()
    {
        PrimaryUrl = $"{Request.Scheme}://{Request.Host}";
        Load("twofactor");
        if (_workspaceStore.ConfirmTotp(UserId, TotpCode ?? string.Empty))
        {
            StatusMessage = "Two-factor authentication is now enabled.";
            return RedirectToPage(new { step = "twofactor" });
        }

        ErrorMessage = "That code wasn't valid - enter a fresh one from your authenticator.";
        Enrollment = _workspaceStore.GetPendingTotpEnrollment(UserId, TotpIssuer);
        return Page();
    }

    /// <summary>Cloud step (offline path): apply a signed license token by hand. Verified against the baked public
    /// key first; refused while the cloud link owns the license.</summary>
    public IActionResult OnPostSetLicenseToken()
    {
        Load("cloud");
        if (CloudManagesLicense)
        {
            StatusMessage = "The cloud manages your license while connected - disconnect first for a manual token.";
            return RedirectToPage(new { step = "cloud" });
        }

        var token = (LicenseTokenInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ErrorMessage = "Paste a license token to apply.";
            return RedirectToPage(new { step = "cloud" });
        }

        var verified = LicenseCrypto.Verify(token, LicensePublicKey.Spki);
        if (verified is null)
        {
            ErrorMessage = "That token isn't valid (wrong signature, expired, or malformed). Nothing changed.";
            return RedirectToPage(new { step = "cloud" });
        }

        _workspaceStore.SetLicenseToken(token);
        StatusMessage = $"License applied: {verified.DisplayName}.";
        return RedirectToPage(new { step = "cloud" });
    }

    private void Load(string? step)
    {
        Step = (step ?? string.Empty).Trim().ToLowerInvariant();
        if (!StepOrder.Contains(Step))
        {
            Step = "welcome";
        }

        if (Step == "cloud")
        {
            var cloud = _workspaceStore.GetCloudConnectionSettings();
            CloudConnected = cloud.Enabled && cloud.HasToken;
            CloudLinkUrl = cloud.Url ?? string.Empty;
            SuggestedInstanceName = PrimaryNode()?.Name ?? Environment.MachineName;
            License = _licenseService.Current;
            CloudManagesLicense = CloudConnected;
        }

        if (Step == "notifications")
        {
            var cloud = _workspaceStore.GetCloudConnectionSettings();
            CloudConnected = cloud.Enabled && cloud.HasToken;
            CloudRelayEnabled = cloud.RelayAlerts;
            EmailConfigured = _workspaceStore.HasEmailNotifications();
        }

        if (Step == "structure")
        {
            var root = PrimaryNode();
            var folders = _workspaceStore.GetAllElements().OfType<FolderElement>().ToList();
            var statuses = new List<StarterFolderStatus>();
            var missing = 0;
            foreach (var (name, children) in StarterTree)
            {
                var top = root is null
                    ? null
                    : folders.FirstOrDefault(folder => folder.ParentId == root.Id &&
                        string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));
                var topExists = top is not null;
                if (!topExists) { missing++; }
                statuses.Add(new StarterFolderStatus(name, 0, topExists));

                foreach (var child in children)
                {
                    var childExists = topExists && folders.Any(folder => folder.ParentId == top!.Id &&
                        string.Equals(folder.Name, child, StringComparison.OrdinalIgnoreCase));
                    if (!childExists) { missing++; }
                    statuses.Add(new StarterFolderStatus(child, 1, childExists));
                }
            }
            StarterFolders = statuses;
            MissingFolderCount = missing;

            ConfiguredSubnets = _workspaceStore.GetPrimaryProbeSubnets();
            IReadOnlyList<string> detected;
            try { detected = ProbeSystemInfoProvider.Collect().Networks; }
            catch { detected = []; }
            SuggestedNetworks = detected
                .Where(net => !ConfiguredSubnets.Any(existing => string.Equals(existing, net, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }

        if (Step == "discovery")
        {
            ConfiguredSubnets = _workspaceStore.GetPrimaryProbeSubnets();
            RemoteProbes = _workspaceStore.GetAllElements()
                .OfType<ProbeElement>()
                .Where(probe => probe.ParentId is not null)
                .Select(probe => new WizardProbe(probe.Name, probe.ProbeId ?? string.Empty, probe.EnrollmentToken ?? string.Empty))
                .ToArray();
        }

        if (Step == "twofactor")
        {
            TwoFactorEnabled = _workspaceStore.FindUser(UserId)?.TwoFactorEnabled ?? false;
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
