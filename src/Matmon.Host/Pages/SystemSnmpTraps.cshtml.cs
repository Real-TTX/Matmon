using Matmon.Core.Domain;
using Matmon.Core.SnmpTraps;
using Matmon.Core.Syslog;
using Matmon.Host.Services;
using Matmon.Host.Services.Plugins;
using Matmon.Probe.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>How the primary processes traps, as the form posts it (the collectors' ports and allow-lists are plugin settings).</summary>
public sealed class SnmpTrapSettingsInput
{
    public string? Communities { get; set; }

    public bool PollOnTrap { get; set; } = true;
}

/// <summary>One row of the SNMPv3 user list. A password left empty keeps the stored one; an empty user name removes the row.</summary>
public sealed class SnmpTrapV3UserInput
{
    /// <summary>The name the row was rendered with - finds the stored passwords when the name is changed.</summary>
    public string? OriginalUsername { get; set; }

    public string? Username { get; set; }

    public string AuthProtocol { get; set; } = "sha1";

    public string? AuthPassword { get; set; }

    public string PrivProtocol { get; set; } = "aes128";

    public string? PrivPassword { get; set; }

    /// <summary>Display only: a password is stored for this row.</summary>
    public bool HasAuthPassword { get; set; }

    public bool HasPrivPassword { get; set; }
}

/// <summary>
/// System > SNMP traps (admins) - a page that comes with the SNMP trap collector plugin: where traps are collected (the
/// collector on the main probe and on probes near the devices, each with its state), how the primary processes them -
/// which communities count, the SNMPv3 users, whether a trap makes the device's sensors poll at once - and a test trap.
/// The collectors' ports and allow-lists are their plugin settings. The traps themselves are in the central log (Events >
/// Syslog, filtered to "SNMP trap").
/// </summary>
public sealed class SystemSnmpTrapsModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspace;
    private readonly PluginUi _plugins;
    private readonly ISyslogStore? _store;
    private readonly SnmpTrapReceiverState? _state;

    // The log and the trap processing exist on a primary only; on a probe the page is simply not there.
    public SystemSnmpTrapsModel(IMonitoringWorkspaceStore workspace, PluginUi plugins, ISyslogStore? store = null, SnmpTrapReceiverState? state = null)
    {
        _workspace = workspace;
        _plugins = plugins;
        _store = store;
        _state = state;
    }

    [BindProperty]
    public SnmpTrapSettingsInput Input { get; set; } = new();

    [BindProperty]
    public List<SnmpTrapV3UserInput> V3Users { get; set; } = [];

    public string? UsersFormError { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string? FormError { get; private set; }

    public SnmpTrapSettings Settings { get; private set; } = new();

    public SnmpTrapReceiverSnapshot Receiver { get; private set; } = new(null, 0, 0, 0, 0, null, null);

    /// <summary>Where the SNMP trap collector runs (the main probe, probes) with its last state.</summary>
    public IReadOnlyList<PluginPlacementView> Collectors { get; private set; } = [];

    /// <summary>The collector on this machine, when it runs - the one a test trap and the device hint go to.</summary>
    public PluginPlacementView? LocalCollector { get; private set; }

    public bool Active => Collectors.Any(collector => collector.Assignment.Enabled);

    public int LocalPort => LocalCollector is { } local && local.Assignment.Settings.TryGetValue("port", out var value) && int.TryParse(value, out var port)
        ? port
        : SnmpTrapSettings.DefaultPort;

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

        // The v3 users have their own form; this one leaves them as they are.
        settings.V3Users = _workspace.GetSnmpTrapSettings().V3Users;
        _workspace.UpdateSnmpTrapSettings(settings);
        StatusMessage = "Saved. It applies to the next trap.";
        return RedirectToPage();
    }

    public IActionResult OnPostSaveUsers()
    {
        if (_store is null)
        {
            return NotFound();
        }

        var current = _workspace.GetSnmpTrapSettings();
        if (!TryBuildV3Users(V3Users, current.V3Users, out var users, out var error))
        {
            UsersFormError = error;
            ModelState.Clear();
            Load(resetInput: true, resetUsers: false);
            return Page();
        }

        current.V3Users = users;
        _workspace.UpdateSnmpTrapSettings(current);
        StatusMessage = users.Count == 0
            ? "Saved. No SNMPv3 user - v3 traps are refused."
            : $"Saved. SNMPv3 traps are accepted from {string.Join(", ", users.Select(user => user.Username))}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        if (_store is null || _state is null)
        {
            return NotFound();
        }

        Load(resetInput: true);
        if (LocalCollector is not { IsRunning: true })
        {
            ErrorMessage = "No SNMP trap collector runs on this machine - the test sends to it. Switch one on for the main probe under System > Plugins.";
            return RedirectToPage();
        }

        var (ok, message) = await SnmpTrapSelfTest.RunAsync(LocalPort, _workspace.GetSnmpTrapSettings(), _store, _state, cancellationToken);
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

    /// <summary>The processing settings from the form. The collectors' ports and allow-lists are not here: they are each
    /// collector's own settings (System > Plugins).</summary>
    public static bool TryBuildSettings(SnmpTrapSettingsInput input, out SnmpTrapSettings settings, out string? error)
    {
        error = null;
        settings = new SnmpTrapSettings
        {
            Communities = input.Communities?.Trim(),
            PollOnTrap = input.PollOnTrap
        }.Normalized();
        return true;
    }

    /// <summary>
    /// Builds the v3 users from the form. Refuses what a device could never match (an unknown protocol, privacy without
    /// authentication, a password under the 8 characters USM requires) instead of storing a user no trap can pass.
    /// </summary>
    public static bool TryBuildV3Users(IReadOnlyList<SnmpTrapV3UserInput> rows, IReadOnlyList<SnmpTrapV3User> stored, out List<SnmpTrapV3User> users, out string? error)
    {
        users = [];
        error = null;
        foreach (var row in rows)
        {
            var name = row.Username?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue; // an emptied row removes the user
            }

            if (name.Length > 32)
            {
                error = $"'{name}': an SNMPv3 user name has at most 32 characters.";
                return false;
            }

            if (users.Any(user => string.Equals(user.Username, name, StringComparison.Ordinal)))
            {
                error = $"'{name}' is in the list twice.";
                return false;
            }

            var auth = SnmpV3Crypto.ParseAuth(row.AuthProtocol);
            var priv = SnmpV3Crypto.ParsePriv(row.PrivProtocol);
            if (auth is null || priv is null)
            {
                error = $"'{name}': unknown authentication or privacy protocol.";
                return false;
            }

            if (auth == SnmpV3AuthProtocol.None && priv != SnmpV3PrivProtocol.None)
            {
                error = $"'{name}': privacy needs authentication - SNMPv3 has no encryption without it.";
                return false;
            }

            var previous = stored.FirstOrDefault(user => string.Equals(user.Username, row.OriginalUsername?.Trim() ?? name, StringComparison.Ordinal));
            var authPassword = string.IsNullOrEmpty(row.AuthPassword) ? previous?.AuthPassword : row.AuthPassword;
            var privPassword = string.IsNullOrEmpty(row.PrivPassword) ? previous?.PrivPassword : row.PrivPassword;
            if (auth != SnmpV3AuthProtocol.None && string.IsNullOrEmpty(authPassword))
            {
                error = $"'{name}': enter the authentication password.";
                return false;
            }

            if ((auth != SnmpV3AuthProtocol.None && authPassword!.Length < 8) || (priv != SnmpV3PrivProtocol.None && !string.IsNullOrEmpty(privPassword) && privPassword.Length < 8))
            {
                error = $"'{name}': SNMPv3 passwords have at least 8 characters.";
                return false;
            }

            users.Add(new SnmpTrapV3User
            {
                Username = name,
                AuthProtocol = row.AuthProtocol.Trim().ToLowerInvariant(),
                AuthPassword = auth == SnmpV3AuthProtocol.None ? null : authPassword,
                PrivProtocol = priv == SnmpV3PrivProtocol.None ? "none" : row.PrivProtocol.Trim().ToLowerInvariant(),
                PrivPassword = priv == SnmpV3PrivProtocol.None ? null : privPassword
            });
        }

        if (users.Count > SnmpTrapSettings.MaxV3Users)
        {
            error = $"At most {SnmpTrapSettings.MaxV3Users} SNMPv3 users.";
            return false;
        }

        return true;
    }

    private void Load(bool resetInput, bool resetUsers = true)
    {
        Settings = _workspace.GetSnmpTrapSettings();
        Receiver = _state?.Snapshot() ?? Receiver;
        Collectors = _plugins.Placements(SnmpTrapCollectorPlugin.Id);
        LocalCollector = Collectors.FirstOrDefault(collector => collector.Assignment.Enabled && collector.Location?.IsPrimary == true);
        if (resetUsers)
        {
            V3Users = Settings.V3Users.Select(user => new SnmpTrapV3UserInput
            {
                OriginalUsername = user.Username,
                Username = user.Username,
                AuthProtocol = user.AuthProtocol,
                PrivProtocol = user.PrivProtocol,
                HasAuthPassword = !string.IsNullOrEmpty(user.AuthPassword) || user.AuthPasswordHydrationFailed,
                HasPrivPassword = !string.IsNullOrEmpty(user.PrivPassword) || user.PrivPasswordHydrationFailed
            }).ToList();
        }
        else
        {
            // A refused form comes back with what was typed - but never with the passwords.
            foreach (var row in V3Users)
            {
                var previous = Settings.V3Users.FirstOrDefault(user => string.Equals(user.Username, row.OriginalUsername, StringComparison.Ordinal));
                row.HasAuthPassword = previous is not null && !string.IsNullOrEmpty(previous.AuthPassword);
                row.HasPrivPassword = previous is not null && !string.IsNullOrEmpty(previous.PrivPassword);
                row.AuthPassword = null;
                row.PrivPassword = null;
            }
        }

        // One empty row is the "add" (up to the limit).
        if (V3Users.Count(row => string.IsNullOrWhiteSpace(row.Username)) == 0 && V3Users.Count < SnmpTrapSettings.MaxV3Users)
        {
            V3Users.Add(new SnmpTrapV3UserInput());
        }

        if (resetInput)
        {
            Input = new SnmpTrapSettingsInput
            {
                Communities = Settings.Communities,
                PollOnTrap = Settings.PollOnTrap
            };
        }
    }
}
