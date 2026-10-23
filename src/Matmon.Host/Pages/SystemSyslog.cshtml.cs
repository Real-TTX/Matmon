using Matmon.Core.Domain;
using Matmon.Core.Syslog;
using Matmon.Host.Services;
using Matmon.Host.Services.Plugins;
using Matmon.Probe.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>One line of the Senders list: the sender, the host in the tree it was matched to, what to tell a new sensor to
/// look for (<see cref="WatchSender"/> - the NAME that made the match, null when the address did and the host's own address
/// will do) and the syslog sensors already on that host.</summary>
public sealed record SyslogSourceRow(SyslogSender Sender, HostElement? Host, string? WatchSender, IReadOnlyList<SensorElement> Sensors);

/// <summary>
/// System > Syslog (admins) - a page that comes with the syslog collector plugin: where the log is collected (the collector
/// on the main probe and on probes near the devices, each with its state), how long and how much it keeps, a test message,
/// and who has been sending, with a button that puts a sensor on a device that turned up. The collectors' ports and
/// allow-lists are their plugin settings (System > Plugins). Reading the log itself is the Syslog tab of the Events page.
///
/// Every change is an admin's (the page is admin-only and the page write guard agrees).
/// </summary>
public sealed class SystemSyslogModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspace;
    private readonly ILicenseService _license;
    private readonly PluginUi _plugins;
    private readonly ISyslogStore? _store;

    // The store exists on a primary only; on a probe the page is simply not there.
    public SystemSyslogModel(IMonitoringWorkspaceStore workspace, ILicenseService license, PluginUi plugins, ISyslogStore? store = null)
    {
        _workspace = workspace;
        _license = license;
        _plugins = plugins;
        _store = store;
    }

    /// <summary>How far back the Senders list looks (days, 0 = everything kept).</summary>
    [BindProperty(SupportsGet = true)]
    public int Days { get; set; } = 7;

    [BindProperty]
    public SyslogStorageInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    /// <summary>Why the storage form was refused - shown with the form, which keeps what was typed.</summary>
    public string? FormError { get; private set; }

    public SyslogSettings Settings { get; private set; } = new();

    /// <summary>Where the syslog collector runs (the main probe, probes) with its last state.</summary>
    public IReadOnlyList<PluginPlacementView> Collectors { get; private set; } = [];

    /// <summary>The collector on this machine, when it runs - the one a test message and the device examples go to.</summary>
    public PluginPlacementView? LocalCollector { get; private set; }

    public bool Active => Collectors.Any(collector => collector.Assignment.Enabled);

    public SyslogReceiverHead Head { get; private set; } = new("paused", "No collector", string.Empty);

    public SyslogStorageInfo Storage { get; private set; } = new(false, null, 0, 0, null, null, 0);

    public IReadOnlyList<SyslogSourceRow> Sources { get; private set; } = [];

    /// <summary>The name devices should be pointed at: the one this page was opened on.</summary>
    public string ReceiverHost => Request.Host.Host;

    public IReadOnlyList<(int Days, string Label)> SourceWindows => SyslogPageSupport.SourceWindows;

    public string Ago(DateTimeOffset when) => SyslogPageSupport.Ago(when, DateTimeOffset.UtcNow);

    /// <summary>The ports the collector on this machine listens on (null = that protocol is off).</summary>
    public (int? Udp, int? Tcp) LocalPorts => LocalCollector is { } local ? Ports(local.Assignment) : (null, null);

    public IActionResult OnGet()
    {
        if (_store is null)
        {
            return NotFound();
        }

        Load(resetInput: true);
        return Page();
    }

    public IActionResult OnPostSaveStorage()
    {
        if (_store is null)
        {
            return NotFound();
        }

        if (!SyslogPageSupport.TryBuildStorage(Input, _workspace.GetSyslogSettings(), out var settings, out var error))
        {
            FormError = error;
            ModelState.Clear(); // the inputs show what was typed, not what an earlier post left in the model state
            Load(resetInput: false);
            return Page();
        }

        _workspace.UpdateSyslogSettings(settings);
        StatusMessage = $"Saved. The log keeps {settings.RetentionDays} days and at most {settings.MaxMessages:N0} messages.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return NotFound();
        }

        Load(resetInput: true);
        if (LocalCollector is not { IsRunning: true })
        {
            ErrorMessage = "No syslog collector runs on this machine - the test sends to it. Switch one on for the main probe under System > Plugins.";
            return RedirectToPage();
        }

        var (udp, tcp) = LocalPorts;
        var (ok, message) = await SyslogSelfTest.RunAsync(udp, tcp, _store, cancellationToken);
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

    public IActionResult OnPostClear()
    {
        if (_store is null)
        {
            return NotFound();
        }

        _store.Clear();
        StatusMessage = "All stored syslog messages were deleted.";
        return RedirectToPage();
    }

    /// <summary>Puts a "Syslog Messages" sensor on a host that has been sending - from the Senders list, so a device that
    /// turned up in the log is one click from being watched.</summary>
    public IActionResult OnPostMonitor(Guid hostId, string? source)
    {
        if (_store is null)
        {
            return NotFound();
        }

        if (_workspace.FindElement(hostId) is not HostElement host)
        {
            ErrorMessage = "That host no longer exists.";
            return RedirectToPage(new { days = Days });
        }

        var existing = _workspace.GetAllElements().OfType<SensorElement>()
            .Any(sensor => sensor.ParentId == host.Id && string.Equals(sensor.SensorTypeKey, SyslogSensorTypes.Messages, StringComparison.OrdinalIgnoreCase));
        if (existing)
        {
            ErrorMessage = $"{host.Name} already has a Syslog Messages sensor.";
            return RedirectToPage(new { days = Days });
        }

        if (!_license.CanAddSensor(out var reason))
        {
            ErrorMessage = reason;
            return RedirectToPage(new { days = Days });
        }

        // The sensor looks for the host's own address unless told otherwise. When the host was matched by NAME - the log
        // records another address - the sensor has to be told which name to look for, or it would find nothing.
        MonitoringSettings? settings = null;
        var sender = source?.Trim();
        if (!string.IsNullOrWhiteSpace(sender) && sender.Length <= 200 &&
            !string.Equals(sender, host.Address, StringComparison.OrdinalIgnoreCase))
        {
            settings = new MonitoringSettings();
            settings.Parameters[SyslogSensorSupport.SourceKey] = sender;
        }

        var sensor = _workspace.CreateSensor(host.Id, "Syslog Messages", SyslogSensorTypes.Messages, string.Empty, null, settings);
        StatusMessage = $"Added \"{sensor.Name}\" to {host.Name}: it counts what the device logs in the last hour and warns on the first error.";
        return RedirectToPage(new { days = Days });
    }

    /// <summary>The ports a syslog collector assignment listens on, with the plugin's defaults for what was left empty.</summary>
    public static (int? Udp, int? Tcp) Ports(PluginAssignment assignment)
    {
        bool On(string key) => !assignment.Settings.TryGetValue(key, out var value) || !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        int Port(string key) => assignment.Settings.TryGetValue(key, out var value) && int.TryParse(value, out var port) ? port : SyslogSettings.DefaultPort;
        return (On("udpEnabled") ? Port("udpPort") : null, On("tcpEnabled") ? Port("tcpPort") : null);
    }

    private void Load(bool resetInput)
    {
        Settings = _workspace.GetSyslogSettings();
        if (resetInput)
        {
            Input = SyslogStorageInput.From(Settings);
        }

        Storage = _store!.GetStorageInfo();
        Collectors = _plugins.Placements(SyslogCollectorPlugin.Id);
        LocalCollector = Collectors.FirstOrDefault(collector => collector.Assignment.Enabled && collector.Location?.IsPrimary == true);
        Head = SyslogPageSupport.DescribeCollectors(Collectors, Storage.Available, Storage.Error);

        var now = DateTimeOffset.UtcNow;
        var since = Days > 0 ? now - TimeSpan.FromDays(Math.Min(Days, 3650)) : DateTimeOffset.UnixEpoch;
        var senders = SyslogPageSupport.BuildSenders(_store.GetSenders(since));

        var elements = _workspace.GetAllElements();
        var hosts = elements.OfType<HostElement>().ToArray();
        var syslogSensors = elements.OfType<SensorElement>().Where(sensor => SyslogSensorTypes.IsSyslog(sensor.SensorTypeKey)).ToArray();

        Sources = senders
            .Select(sender =>
            {
                var match = SyslogPageSupport.FindHost(sender, hosts);
                var sensors = match is null ? [] : syslogSensors.Where(sensor => sensor.ParentId == match.Host.Id).ToArray();
                return new SyslogSourceRow(sender, match?.Host, match?.MatchedName, sensors);
            })
            .ToArray();
    }
}
