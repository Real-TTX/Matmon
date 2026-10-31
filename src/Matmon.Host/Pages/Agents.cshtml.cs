using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Agents: a Matmon agent installed ON a machine, enrolled with a one-time code. An agent IS a host - the machine it measures
/// (<see cref="HostElement.Agent"/>) - not a probe: its sensors live under the host and run on the agent, and the probe above
/// the host is the one it talks through (a remote probe relays it to the primary). What this page adds is the enrolment
/// itself: create a code (for a new host in a chosen place, or for an existing host), then either download the agent with the
/// code inside (one click / one command on the machine) or copy a one-line install command.
/// </summary>
public sealed class AgentsModel : PageModel
{
    private static readonly int[] ValidityHours = [1, 24, 168];

    private readonly IConfigurationOverviewProvider _configurationOverviewProvider;
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly AgentPackageStore _packageStore;
    private readonly AgentInstallerCodes _installerCodes;

    public AgentsModel(
        IConfigurationOverviewProvider configurationOverviewProvider,
        IMonitoringWorkspaceStore workspaceStore,
        AgentPackageStore packageStore,
        AgentInstallerCodes installerCodes)
    {
        _configurationOverviewProvider = configurationOverviewProvider;
        _workspaceStore = workspaceStore;
        _packageStore = packageStore;
        _installerCodes = installerCodes;
    }

    public IReadOnlyList<SystemProbeOverview> Agents { get; private set; } = [];

    public IReadOnlyList<AgentEnrollment> PendingEnrollments { get; private set; } = [];

    /// <summary>What this instance can hand out: hand-placed packages and what the agent feed offers.</summary>
    public AgentCatalog Catalog { get; private set; } = new([], null, null, null);

    public IReadOnlyList<AgentPackage> Packages => Catalog.Packages;

    public string PackagesPath => _packageStore.RootPath;

    [BindProperty]
    public string? NewName { get; set; }

    [BindProperty]
    public int NewValidityHours { get; set; } = 24;

    /// <summary>An existing host the new agent becomes (it stays where it is); empty = a new host, named after the machine,
    /// under <see cref="NewParentId"/>.</summary>
    [BindProperty]
    public Guid? NewHostId { get; set; }

    /// <summary>Where a NEW agent host goes: the primary, a remote probe (which then relays the agent) or a folder.</summary>
    [BindProperty]
    public Guid? NewParentId { get; set; }

    /// <summary>What the host picker offers: every host that has no agent yet.</summary>
    public IReadOnlyList<ElementPickerOption> HostOptions { get; private set; } = [];

    /// <summary>What the "place under" picker offers: probes and folders.</summary>
    public IReadOnlyList<ElementPickerOption> PlaceOptions { get; private set; } = [];

    /// <summary>The code just issued - shown on exactly one render, then gone for good (only its hash is kept).</summary>
    [TempData]
    public string? IssuedCode { get; set; }

    /// <summary>The issued code's enrolment id - what the download buttons name (the server keeps the plain code in memory
    /// for its lifetime, <see cref="AgentInstallerCodes"/>). A Guid, not a string: like a date, the TempData serializer
    /// turns a Guid-shaped string back into a Guid on read, and a string property then fails to take it.</summary>
    [TempData]
    public Guid? IssuedEnrollmentId { get; set; }

    [TempData]
    public string? IssuedName { get; set; }

    /// <summary>The issued code re-installs an existing agent - the page then says what that replaces.</summary>
    [TempData]
    public bool IssuedReplaces { get; set; }

    /// <summary>The existing host the issued code turns into an agent.</summary>
    [TempData]
    public string? IssuedHostName { get; set; }

    // A DateTime, not an ISO string: the TempData serializer turns any date-shaped string back into a
    // DateTime on read, and the string property then fails to take it.
    [TempData]
    public DateTime? IssuedExpiresUtc { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public IReadOnlyList<int> ValidityChoices => ValidityHours;

    /// <summary>The probe the agent of the issued code talks through (the primary, or a remote probe that relays it).</summary>
    public ProbeElement? IssuedVia { get; private set; }

    public bool IssuedViaRemote => IssuedVia is { ParentId: not null };

    /// <summary>
    /// The address the agent of the issued code is installed against: under a remote probe the address set for that probe
    /// (it relays), else the instance's own - the public address under System > General, else the one this page was opened
    /// on (inside the cloud console: the one remembered from a direct visit). Null when none is known.
    /// </summary>
    public string? IssuedAgentUrl { get; private set; }

    public string InstanceVersion => _packageStore.InstanceVersion;

    /// <summary>The agent build this instance hands out, when it is not its own (a rolling feed holds only the newest).</summary>
    public string? ServedVersion =>
        Packages.Select(package => package.Version).FirstOrDefault(version => version is not null && !string.Equals(version, InstanceVersion, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The agent will update itself: it runs a different build than this instance serves for it. Mirrors the agent's
    /// own rule (AgentUpdatePolicy) closely enough for a hint - an unversioned local build offers nothing.
    /// </summary>
    public bool IsUpdatePending(SystemProbeOverview agent, out string offered)
    {
        offered = Packages.FirstOrDefault()?.Version ?? InstanceVersion;
        return Packages.Count > 0 &&
            !string.IsNullOrWhiteSpace(agent.Version) &&
            !offered.StartsWith("local-", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(agent.Version, offered, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the ready-to-install downloads still work for this pending code (created in this process).</summary>
    public bool CanDownload(Guid enrollmentId) => _installerCodes.Find(enrollmentId) is not null;

    private Guid? _rootId;

    /// <summary>What an agent's heartbeats should name as their relay: nothing under the primary, else the probe it sits under.</summary>
    public string? RelayNameFor(SystemProbeOverview agent) => agent.ViaProbeElementId == _rootId ? null : agent.ViaProbeName;

    /// <summary>The probe a pending code's agent will talk through.</summary>
    public ProbeElement? ViaOf(AgentEnrollment enrollment) => _workspaceStore.FindAgentRelayProbe(enrollment);

    /// <summary>Whether a pending code re-installs an agent that exists.</summary>
    public bool IsReinstall(AgentEnrollment enrollment) =>
        enrollment.HostElementId is Guid hostId && _workspaceStore.FindElement(hostId) is HostElement { Agent: not null };

    public async Task OnGetAsync() => await LoadAsync();

    public IActionResult OnPostCreate()
    {
        var hours = ValidityHours.Contains(NewValidityHours) ? NewValidityHours : 24;
        try
        {
            var issue = _workspaceStore.CreateAgentEnrollment(NewName, TimeSpan.FromHours(hours), User.Identity?.Name, NewParentId, NewHostId);
            Issue(issue);
            IssuedHostName = issue.Enrollment.HostElementId is Guid hostId ? _workspaceStore.FindElement(hostId)?.Name : null;
        }
        catch (InvalidOperationException ex)
        {
            // An unknown host or place: said on the page, nothing issued.
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    /// <summary>
    /// A code that re-installs an EXISTING agent: a reinstalled machine keeps its host, sensors, history and alerts instead of
    /// becoming a new, empty one; whatever still runs under the old token is locked out once the new install enrols.
    /// </summary>
    public IActionResult OnPostReinstall(Guid agentHostId)
    {
        try
        {
            var issue = _workspaceStore.CreateAgentEnrollment(null, TimeSpan.FromHours(24), User.Identity?.Name, hostElementId: agentHostId);
            Issue(issue);
            IssuedReplaces = true;
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    /// <summary>Moves an agent to another host - the machine it sits on - without reinstalling it.</summary>
    public IActionResult OnPostRebindHost(Guid agentHostId, Guid? hostElementId)
    {
        if (hostElementId is not Guid hostId || hostId == Guid.Empty)
        {
            ErrorMessage = "Pick the host this agent runs on.";
            return RedirectToPage();
        }

        var problem = _workspaceStore.RebindAgentHost(agentHostId, hostId);
        if (problem is null)
        {
            var hostName = _workspaceStore.FindElement(hostId)?.Name ?? "the host";
            StatusMessage = $"The agent is now '{hostName}'. Its machine sensors moved along and run on the agent.";
        }
        else
        {
            ErrorMessage = problem;
        }

        return RedirectToPage();
    }

    public IActionResult OnPostRevoke(Guid enrollmentId)
    {
        _installerCodes.Forget(enrollmentId);
        if (_workspaceStore.RevokeAgentEnrollment(enrollmentId))
        {
            StatusMessage = "Enrolment code revoked.";
        }
        else
        {
            ErrorMessage = "That code no longer exists - it was used, revoked or has expired.";
        }

        return RedirectToPage();
    }

    private void Issue(AgentEnrollmentIssue issue)
    {
        IssuedCode = issue.Code;
        IssuedEnrollmentId = issue.Enrollment.Id;
        IssuedName = issue.Enrollment.Name;
        IssuedExpiresUtc = issue.Enrollment.ExpiresUtc.UtcDateTime;
        _installerCodes.Remember(issue.Enrollment.Id, issue.Code, issue.Enrollment.ExpiresUtc);
    }

    /// <summary>Windows: an administrator PowerShell fetches the install script with the code as a header and runs it.</summary>
    public string BuildWindowsCommand(string code) =>
        $"irm '{IssuedAgentUrl}/api/agent/install.ps1' -UseBasicParsing -Headers @{{'{AgentDownloadEndpoints.CodeHeader}'='{code}'}} | iex";

    /// <summary>Linux and macOS: the shell script, run as root.</summary>
    public string BuildUnixCommand(string code) =>
        $"curl -fsSL -H '{AgentDownloadEndpoints.CodeHeader}: {code}' '{IssuedAgentUrl}/api/agent/install.sh' | sudo sh";

    /// <summary>The ready-to-install download of one platform for a pending code.</summary>
    public string InstallerUrl(AgentPackage package, Guid enrollmentId) =>
        $"/api/agent/installer/{package.Platform.RuntimeId}?enrollment={enrollmentId}";

    public static string InstallerLabel(AgentPlatform platform) => platform.RuntimeId switch
    {
        "win-x64" => "Windows (setup with tray icon)",
        "linux-x64" => "Linux x64",
        "linux-arm64" => "Linux ARM64",
        "osx-arm64" => "macOS Apple Silicon",
        "osx-x64" => "macOS Intel",
        _ => platform.DisplayName
    };

    public static string SourceLabel(AgentPackage package) => package.Source switch
    {
        AgentPackageSource.Folder => "placed on this instance",
        AgentPackageSource.Cache => "from the agent feed, kept here",
        _ => "from the agent feed, fetched on first download"
    };

    private async Task LoadAsync()
    {
        Agents = _configurationOverviewProvider.GetProbes()
            .Where(probe => probe.IsAgent)
            .OrderBy(probe => probe.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var elements = _workspaceStore.GetAllElements();
        var root = elements.FirstOrDefault(element => element.ParentId is null);
        _rootId = root?.Id;
        var hostsWithAnAgent = elements.OfType<HostElement>().Where(host => host.Agent is not null).Select(host => host.Id).ToHashSet();
        HostOptions = ElementPickerOptions.Build(root, new HashSet<MonitoringElementKind> { MonitoringElementKind.Host })
            .Where(option => !hostsWithAnAgent.Contains(option.Id))
            .ToArray();
        PlaceOptions = ElementPickerOptions.Build(root, new HashSet<MonitoringElementKind> { MonitoringElementKind.Probe, MonitoringElementKind.Folder });

        PendingEnrollments = _workspaceStore.GetPendingAgentEnrollments();
        if (IssuedEnrollmentId is Guid issuedId && PendingEnrollments.FirstOrDefault(pending => pending.Id == issuedId) is { } issued)
        {
            IssuedVia = _workspaceStore.FindAgentRelayProbe(issued);
            IssuedAgentUrl = AgentDownloadEndpoints.AgentUrl(HttpContext, _workspaceStore, issued,
                PublicLinks.Absolute(HttpContext, _workspaceStore, string.Empty));
        }

        Catalog = await _packageStore.GetCatalogAsync(HttpContext.RequestAborted);
    }
}
