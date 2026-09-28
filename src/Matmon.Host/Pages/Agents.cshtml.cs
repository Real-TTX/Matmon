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

    public AgentsModel(
        IConfigurationOverviewProvider configurationOverviewProvider,
        IMonitoringWorkspaceStore workspaceStore,
        ILicenseService licenseService)
    {
        _configurationOverviewProvider = configurationOverviewProvider;
        _workspaceStore = workspaceStore;
        _licenseService = licenseService;
    }

    public IReadOnlyList<SystemProbeOverview> Agents { get; private set; } = [];

    public IReadOnlyList<AgentEnrollment> PendingEnrollments { get; private set; } = [];

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

    public string BuildWindowsCommands(string code) =>
        $"""
        # PowerShell as Administrator, in the folder holding matmon-agent.exe
        New-Item -ItemType Directory -Force "$env:ProgramFiles\Matmon Agent" | Out-Null
        Copy-Item .\matmon-agent.exe "$env:ProgramFiles\Matmon Agent\"
        & "$env:ProgramFiles\Matmon Agent\matmon-agent.exe" enroll --url {InstanceUrl} --code {code}
        New-Service -Name "Matmon Agent" -BinaryPathName "`"$env:ProgramFiles\Matmon Agent\matmon-agent.exe`"" -StartupType Automatic
        Start-Service "Matmon Agent"
        """;

    public string BuildLinuxCommands(string code) =>
        $"""
        # as root, in the folder holding matmon-agent
        install -m 0755 matmon-agent /usr/local/bin/matmon-agent
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

    private void Load()
    {
        Agents = _configurationOverviewProvider.GetOverview().Probes
            .Where(probe => probe.IsAgent)
            .OrderBy(probe => probe.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PendingEnrollments = _workspaceStore.GetPendingAgentEnrollments();

        // Said up front rather than discovered at install time: the code would be issued fine and then
        // refused at the endpoint, on a customer's machine, in a terminal.
        if (!_licenseService.CanAddProbe(out var reason))
        {
            ProbeLimitReason = reason;
        }
    }
}
