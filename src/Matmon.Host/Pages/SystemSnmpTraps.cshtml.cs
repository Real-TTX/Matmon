using Matmon.Core.SnmpTraps;
using Matmon.Core.Syslog;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public sealed class SnmpTrapSettingsInput
{
    public bool Enabled { get; set; }

    public int Port { get; set; } = SnmpTrapSettings.DefaultPort;

    public string? AllowedSources { get; set; }

    public string? Communities { get; set; }

    public bool PollOnTrap { get; set; } = true;
}

/// <summary>
/// System > SNMP traps (admins): the trap receiver - on/off, port, who may send, which communities count, whether a trap
/// makes the device's sensors poll at once - its status and a test trap. The traps themselves are in the central log
/// (Events > Syslog, filtered to "SNMP trap"). Self-contained like System > Syslog; both are to become plugins.
/// </summary>
public sealed class SystemSnmpTrapsModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspace;
    private readonly ISyslogStore? _store;
    private readonly SnmpTrapReceiverState? _state;

    // The log and the receiver exist on a primary only; on a probe the page is simply not there.
    public SystemSnmpTrapsModel(IMonitoringWorkspaceStore workspace, ISyslogStore? store = null, SnmpTrapReceiverState? state = null)
    {
        _workspace = workspace;
        _store = store;
        _state = state;
    }

    [BindProperty]
    public SnmpTrapSettingsInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string? FormError { get; private set; }

    public SnmpTrapSettings Settings { get; private set; } = new();

    public SnmpTrapReceiverSnapshot Receiver { get; private set; } = new("off", null, null, 0, 0, 0, 0, null, null);

    /// <summary>The name devices should be pointed at: the one this page was opened on.</summary>
    public string ReceiverHost => Request.Host.Host;

    public string Ago(DateTimeOffset when) => SyslogPageSupport.Ago(when, DateTimeOffset.UtcNow);

    public IActionResult OnGet()
    {
        if (_store is null)
        {
            return NotFound();
        }

        Load(resetInput: true);
        return Page();
    }

    public IActionResult OnPostSaveSettings()
    {
        if (_store is null)
        {
            return NotFound();
        }

        if (!TryBuildSettings(Input, out var settings, out var error))
        {
            FormError = error;
            ModelState.Clear();
            Load(resetInput: false);
            return Page();
        }

        _workspace.UpdateSnmpTrapSettings(settings);
        StatusMessage = settings.Enabled
            ? "Saved. The receiver applies it within a few seconds." +
              (string.IsNullOrWhiteSpace(settings.AllowedSources) ? " Anyone who can reach the port can send traps - restrict it with Allowed sources." : string.Empty)
            : "Saved. The receiver is off.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        if (_store is null || _state is null)
        {
            return NotFound();
        }

        var (ok, message) = await SnmpTrapSelfTest.RunAsync(_workspace.GetSnmpTrapSettings(), _store, _state, cancellationToken);
        if (ok)
        {
            StatusMessage = message;
        }
        else
        {
            ErrorMessage = message;
        }

        return RedirectToPage();
    }

    /// <summary>Refuses rather than corrects: a port out of range or an allow-list entry that is no address would, ignored,
    /// either break the receiver or widen who may send.</summary>
    public static bool TryBuildSettings(SnmpTrapSettingsInput input, out SnmpTrapSettings settings, out string? error)
    {
        settings = new SnmpTrapSettings();
        error = null;
        if (input.Port is < 1 or > 65535)
        {
            error = "The port must be a number between 1 and 65535.";
            return false;
        }

        var allowed = input.AllowedSources?.Trim();
        if (!string.IsNullOrEmpty(allowed))
        {
            IpAllowList.Parse(allowed, out var invalid);
            if (invalid.Count > 0)
            {
                error = $"Not an IP address or range: {string.Join(", ", invalid)}. Use addresses like 192.168.1.2 or ranges like 192.168.1.0/24, separated by commas or new lines.";
                return false;
            }
        }

        settings = new SnmpTrapSettings
        {
            Enabled = input.Enabled,
            Port = input.Port,
            AllowedSources = allowed,
            Communities = input.Communities?.Trim(),
            PollOnTrap = input.PollOnTrap
        }.Normalized();
        return true;
    }

    private void Load(bool resetInput)
    {
        Settings = _workspace.GetSnmpTrapSettings();
        Receiver = _state?.Snapshot() ?? Receiver;
        if (resetInput)
        {
            Input = new SnmpTrapSettingsInput
            {
                Enabled = Settings.Enabled,
                Port = Settings.Port,
                AllowedSources = Settings.AllowedSources,
                Communities = Settings.Communities,
                PollOnTrap = Settings.PollOnTrap
            };
        }
    }
}
