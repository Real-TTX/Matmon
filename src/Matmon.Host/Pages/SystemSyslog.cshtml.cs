using Matmon.Core.Domain;
using Matmon.Core.Syslog;
using Matmon.Host.Services;
using Matmon.Probe.Syslog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>One line of the Senders list: the sender, the host in the tree it was matched to, what to tell a new sensor to
/// look for (<see cref="WatchSender"/> - the NAME that made the match, null when the address did and the host's own address
/// will do) and the syslog sensors already on that host.</summary>
public sealed record SyslogSourceRow(SyslogSender Sender, HostElement? Host, string? WatchSender, IReadOnlyList<SensorElement> Sensors);

/// <summary>
/// System > Syslog (admins): the receiver - on/off, ports, who may send, how long to keep the log - its status, and who has
/// been sending, with a button that puts a sensor on a device that turned up. Reading the log itself is the Syslog tab of
/// the Events page. A system setting lives here rather than next to the log because the log is for everybody who works
/// with alerts, and the receiver is for whoever runs the machine.
///
/// Every change is an admin's (the page is admin-only and the page write guard agrees).
/// </summary>
public sealed class SystemSyslogModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspace;
    private readonly ILicenseService _license;
    private readonly ISyslogStore? _store;
    private readonly SyslogReceiverState? _state;

    // The store and the receiver state exist on a primary only; on a probe the page is simply not there.
    public SystemSyslogModel(
        IMonitoringWorkspaceStore workspace,
        ILicenseService license,
        ISyslogStore? store = null,
        SyslogReceiverState? state = null)
    {
        _workspace = workspace;
        _license = license;
        _store = store;
        _state = state;
    }

    /// <summary>How far back the Senders list looks (days, 0 = everything kept).</summary>
    [BindProperty(SupportsGet = true)]
    public int Days { get; set; } = 7;

    [BindProperty]
    public SyslogSettingsInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    /// <summary>Why the settings form was refused - shown with the form, which keeps what was typed.</summary>
    public string? FormError { get; private set; }

    public SyslogSettings Settings { get; private set; } = new();

    public SyslogReceiverSnapshot Receiver { get; private set; } = EmptyReceiver;

    public SyslogStorageInfo Storage { get; private set; } = new(false, null, 0, 0, null, null, 0);

    public IReadOnlyList<SyslogSourceRow> Sources { get; private set; } = [];

    /// <summary>The name devices should be pointed at: the one this page was opened on.</summary>
    public string ReceiverHost => Request.Host.Host;

    public IReadOnlyList<(int Days, string Label)> SourceWindows => SyslogPageSupport.SourceWindows;

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

        if (!SyslogPageSupport.TryBuildSettings(Input, out var settings, out var error))
        {
            FormError = error;
            ModelState.Clear(); // the inputs show what was typed, not what an earlier post left in the model state
            Load(resetInput: false);
            return Page();
        }

        _workspace.UpdateSyslogSettings(settings);

        StatusMessage = settings.Enabled
            ? "Saved. The receiver applies it within a few seconds." +
              (string.IsNullOrWhiteSpace(settings.AllowedSources)
                  ? " Anyone who can reach the port can send messages - restrict it with Allowed sources."
                  : string.Empty) +
              " The log is on the Syslog tab of Events."
            : "Saved. The receiver is off - nothing is collected, and what is already stored stays until it ages out.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        if (_store is null || _state is null)
        {
            return NotFound();
        }

        var (ok, message) = await SyslogSelfTest.RunAsync(_workspace.GetSyslogSettings(), _store, _state, cancellationToken);
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

    private void Load(bool resetInput)
    {
        var settings = _workspace.GetSyslogSettings();
        Settings = settings;
        if (resetInput)
        {
            Input = SyslogSettingsInput.From(settings);
        }

        Receiver = _state?.Snapshot() ?? EmptyReceiver;
        Storage = _store!.GetStorageInfo();

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

    private static readonly SyslogReceiverSnapshot EmptyReceiver =
        new(SyslogListenerStatus.Off, SyslogListenerStatus.Off, 0, 0, 0, null, null);
}
