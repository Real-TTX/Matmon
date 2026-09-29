using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Agents: the probes that were installed ON a machine and enrolled with a one-time code. A view over the
/// probe elements (<see cref="ProbeElement.AgentEnrolledUtc"/>), not a second model - an agent is managed,
/// assigned and alarmed exactly like any other probe. What this page adds is the enrolment itself.
/// </summary>
public sealed class AgentsModel : PageModel
{
    private static readonly int[] ValidityHours = [1, 24, 168];

    private readonly IConfigurationOverviewProvider _configurationOverviewProvider;
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly ILicenseService _licenseService;
    private readonly AgentPackageStore _packageStore;

    public AgentsModel(
        IConfigurationOverviewProvider configurationOverviewProvider,
        IMonitoringWorkspaceStore workspaceStore,
        ILicenseService licenseService,
        AgentPackageStore packageStore)
    {
        _configurationOverviewProvider = configurationOverviewProvider;
        _workspaceStore = workspaceStore;
        _licenseService = licenseService;
        _packageStore = packageStore;
    }

    public IReadOnlyList<SystemProbeOverview> Agents { get; private set; } = [];

    public IReadOnlyList<AgentEnrollment> PendingEnrollments { get; private set; } = [];

    /// <summary>The agent binaries this instance carries (none on a plain local dev run).</summary>
    public IReadOnlyList<AgentPackage> Packages { get; private set; } = [];

    public string PackagesPath => _packageStore.RootPath;

    [BindProperty]
    public string? NewName { get; set; }

    [BindProperty]
    public int NewValidityHours { get; set; } = 24;

    /// <summary>The code just issued - shown on exactly one render, then gone for good (only its hash is kept).</summary>
    [TempData]
    public string? IssuedCode { get; set; }

    [TempData]
    public string? IssuedName { get; set; }

    // A DateTime, not an ISO string: the TempData serializer turns any date-shaped string back into a
    // DateTime on read, and the string property then fails to take it.
    [TempData]
    public DateTime? IssuedExpiresUtc { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string? ProbeLimitReason { get; private set; }

    public IReadOnlyList<int> ValidityChoices => ValidityHours;

    /// <summary>The URL an agent should enrol against: the one this page was opened on.</summary>
    public string InstanceUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    public string InstanceVersion => Matmon.Probe.MatmonVersion.Current;

    /// <summary>
    /// The agent will update itself: it runs a different build than the instance offers. Mirrors the agent's
    /// own rule (AgentUpdatePolicy) closely enough for a hint - an unversioned local instance build offers
    /// nothing, and without a package for the agent's platform there is nothing to update to.
    /// </summary>
    public bool IsUpdatePending(SystemProbeOverview agent) =>
        Packages.Count > 0 &&
        !string.IsNullOrWhiteSpace(agent.Version) &&
        !InstanceVersion.StartsWith("local-", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(agent.Version, InstanceVersion, StringComparison.OrdinalIgnoreCase);

    public void OnGet() => Load();

    public IActionResult OnPostCreate()
    {
        var hours = ValidityHours.Contains(NewValidityHours) ? NewValidityHours : 24;
        var issue = _workspaceStore.CreateAgentEnrollment(NewName, TimeSpan.FromHours(hours), User.Identity?.Name);

        IssuedCode = issue.Code;
        IssuedName = issue.Enrollment.Name;
        IssuedExpiresUtc = issue.Enrollment.ExpiresUtc.UtcDateTime;
        return RedirectToPage();
    }

    public IActionResult OnPostRevoke(Guid enrollmentId)
    {
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

    // Each recipe downloads the binary FROM this instance with the same code it then enrols with - the code
    // is checked but not consumed by the download. Without bundled packages (a local dev run) the recipe says
    // where the binary has to come from instead of pointing at a URL that 404s.
    public string BuildWindowsCommands(string code)
    {
        var fetch = HasPackage("win-x64")
            ? $$"""
              $ProgressPreference = 'SilentlyContinue'
              Invoke-WebRequest -UseBasicParsing -Uri "{{InstanceUrl}}/api/agent/packages/win-x64" -Headers @{ "X-Matmon-Enrollment-Code" = "{{code}}" } -OutFile "$dir\matmon-agent.exe"
              """
            : """
              # This instance ships no agent packages - copy matmon-agent.exe into $dir first.
              """;

        return $$"""
            # PowerShell as Administrator
            $dir = "$env:ProgramFiles\Matmon Agent"
            New-Item -ItemType Directory -Force $dir | Out-Null
            {{fetch}}
            & "$dir\matmon-agent.exe" enroll --url {{InstanceUrl}} --code {{code}}
            New-Service -Name "Matmon Agent" -BinaryPathName "`"$dir\matmon-agent.exe`"" -StartupType Automatic
            Start-Service "Matmon Agent"
            """;
    }

    public string BuildLinuxCommands(string code)
    {
        var fetch = HasPackage("linux-x64") || HasPackage("linux-arm64")
            ? $"""
              case "$(uname -m)" in aarch64|arm64) rid=linux-arm64 ;; *) rid=linux-x64 ;; esac
              curl -fsSL -H "X-Matmon-Enrollment-Code: {code}" "{InstanceUrl}/api/agent/packages/$rid" -o /usr/local/bin/matmon-agent
              chmod 0755 /usr/local/bin/matmon-agent
              """
            : """
              # This instance ships no agent packages - copy matmon-agent to /usr/local/bin first.
              install -m 0755 matmon-agent /usr/local/bin/matmon-agent
              """;

        return $"""
            # as root
            {fetch}
            matmon-agent enroll --url {InstanceUrl} --code {code}
            cat > /etc/systemd/system/matmon-agent.service <<'UNIT'
            [Unit]
            Description=Matmon Agent
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=notify
            ExecStart=/usr/local/bin/matmon-agent
            Restart=always
            RestartSec=10

            [Install]
            WantedBy=multi-user.target
            UNIT
            systemctl daemon-reload && systemctl enable --now matmon-agent
            """;
    }

    private bool HasPackage(string runtimeId) =>
        Packages.Any(package => string.Equals(package.Platform.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));

    private void Load()
    {
        Agents = _configurationOverviewProvider.GetOverview().Probes
            .Where(probe => probe.IsAgent)
            .OrderBy(probe => probe.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PendingEnrollments = _workspaceStore.GetPendingAgentEnrollments();
        Packages = _packageStore.GetPackages();

        // Said up front rather than discovered at install time: the code would be issued fine and then
        // refused at the endpoint, on a customer's machine, in a terminal.
        if (!_licenseService.CanAddProbe(out var reason))
        {
            ProbeLimitReason = reason;
        }
    }
}
