using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Core.Syslog;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>One line of the Senders tab: the sender, the host in the tree it was matched to, what to tell a new sensor to look
/// for (<see cref="WatchSender"/> - the NAME that made the match, null when the address did and the host's own address will
/// do) and the syslog sensors already on that host.</summary>
public sealed record SyslogSourceRow(SyslogSender Sender, HostElement? Host, string? WatchSender, IReadOnlyList<SensorElement> Sensors);

/// <summary>
/// The central log. Three tabs: Messages (the log itself, filterable, live), Sources (who has been sending, and which of
/// them are in the tree and watched by a sensor) and Settings (the receiver: on/off, ports, who may send, how long to keep
/// it, plus its status). Operators and admins read it; every change is admin-only (the page write guard).
///
/// The log is rendered client-side from <see cref="OnGetData"/> like the application log, and every message is put on the
/// page as TEXT: anything on the network can send a syslog line, so its content is never markup.
/// </summary>
public sealed class SyslogModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspace;
    private readonly ILicenseService _license;
    private readonly ISyslogStore? _store;
    private readonly SyslogReceiverState? _state;

    // The store and the receiver state exist on a primary only; on a probe the page is simply not there.
    public SyslogModel(
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

    // ---- the filter bar (so a link can open the page already filtered) -----------------------------------------------

    [BindProperty(SupportsGet = true)]
    public int Window { get; set; } = 60;

    [BindProperty(SupportsGet = true)]
    public string? Source { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Severity { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Regex { get; set; }

    /// <summary>How far back the Sources tab looks (days, 0 = everything kept).</summary>
    [BindProperty(SupportsGet = true)]
    public int Days { get; set; } = 7;

    // ---- the settings form -------------------------------------------------------------------------------------------

    [BindProperty]
    public SyslogSettingsInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    /// <summary>Why the settings form was refused - shown with the form, which keeps what was typed.</summary>
    public string? FormError { get; private set; }

    public bool IsAdmin => MatmonSecurity.IsAdmin(User);

    public SyslogSettings Settings { get; private set; } = new();

    public SyslogReceiverSnapshot Receiver { get; private set; } = EmptyReceiver;

    public SyslogStorageInfo Storage { get; private set; } = new(false, null, 0, 0, null, null, 0);

    public IReadOnlyList<SyslogSourceRow> Sources { get; private set; } = [];

    /// <summary>The names senders are known by, for the sender filter's suggestions.</summary>
    public IReadOnlyList<string> KnownSenders { get; private set; } = [];

    /// <summary>The name devices should be pointed at: the one this page was opened on.</summary>
    public string ReceiverHost => Request.Host.Host;

    public IReadOnlyList<(int Minutes, string Label)> Windows => SyslogPageSupport.Windows;

    public IReadOnlyList<(string Value, string Label)> SeverityChoices => SyslogPageSupport.SeverityChoices;

    public IReadOnlyList<(int Days, string Label)> SourceWindows => SyslogPageSupport.SourceWindows;

    public int PageSize => SyslogPageSupport.PageSize;

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

    /// <summary>The log as JSON, newest first, one page at a time (<paramref name="before"/> is the id of the oldest row the
    /// client already has). The totals are only asked for when the filter changes - a live refresh every few seconds must
    /// not count a big window each time.</summary>
    public IActionResult OnGetData(int window = 60, string? source = null, string? severity = null, string? q = null, bool regex = false, long? before = null, bool counts = true)
    {
        if (_store is null)
        {
            return NotFound();
        }

        if (!SyslogPageSupport.TryBuildQuery(window, source, severity, q, regex, before, DateTimeOffset.UtcNow, out var query, out var error))
        {
            return new JsonResult(new { items = Array.Empty<object>(), hasMore = false, counts = (object?)null, error });
        }

        var items = _store.Search(query, SyslogPageSupport.PageSize);
        object? summary = null;
        if (counts)
        {
            var totals = _store.Count(query with { BeforeId = null });
            summary = new { total = totals.Total, errors = totals.Errors, warnings = totals.Warnings };
        }

        return new JsonResult(new
        {
            items = items.Select(Row),
            hasMore = items.Count >= SyslogPageSupport.PageSize,
            counts = summary,
            error = (string?)null
        });
    }

    /// <summary>Just the totals for a filter - what the live view asks for after new rows arrived, instead of paying for
    /// them on every tick.</summary>
    public IActionResult OnGetCounts(int window = 60, string? source = null, string? severity = null, string? q = null, bool regex = false)
    {
        if (_store is null)
        {
            return NotFound();
        }

        if (!SyslogPageSupport.TryBuildQuery(window, source, severity, q, regex, beforeId: null, DateTimeOffset.UtcNow, out var query, out var error))
        {
            return new JsonResult(new { counts = (object?)null, error });
        }

        var totals = _store.Count(query);
        return new JsonResult(new { counts = new { total = totals.Total, errors = totals.Errors, warnings = totals.Warnings }, error = (string?)null });
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
                  : string.Empty)
            : "Saved. The receiver is off - nothing is collected, and what is already stored stays until it ages out.";
        return ToTab("settings");
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

        return ToTab("settings");
    }

    public IActionResult OnPostClear()
    {
        if (_store is null)
        {
            return NotFound();
        }

        _store.Clear();
        StatusMessage = "All stored syslog messages were deleted.";
        return ToTab("settings");
    }

    /// <summary>Puts a "Syslog Messages" sensor on a host that has been sending - from the Sources tab, so a device that
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
            return ToTab("senders");
        }

        var existing = _workspace.GetAllElements().OfType<SensorElement>()
            .Any(sensor => sensor.ParentId == host.Id && string.Equals(sensor.SensorTypeKey, SyslogSensorTypes.Messages, StringComparison.OrdinalIgnoreCase));
        if (existing)
        {
            ErrorMessage = $"{host.Name} already has a Syslog Messages sensor.";
            return ToTab("senders");
        }

        if (!_license.CanAddSensor(out var reason))
        {
            ErrorMessage = reason;
            return ToTab("senders");
        }

        // The sensor looks for the host's own address unless told otherwise. When the host was matched by NAME - the log
        // records another address - the sensor has to be told which address to look for, or it would find nothing.
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
        return ToTab("senders");
    }

    private IActionResult ToTab(string tab) => RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: tab);

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

        KnownSenders = senders
            .SelectMany(sender => new[] { sender.SourceAddress, sender.Hostname })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToArray();
    }

    private static object Row(SyslogMessage message) => new
    {
        id = message.Id,
        ts = message.ReceivedUtc.ToString("o", CultureInfo.InvariantCulture),
        time = message.ReceivedUtc.ToDisplay().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        sev = message.Severity,
        sevName = SyslogSeverity.ShortName(message.Severity),
        sevClass = SyslogPageSupport.SeverityClass(message.Severity),
        source = message.SourceAddress,
        host = message.Hostname,
        app = message.AppName,
        procId = message.ProcId,
        msgId = message.MsgId,
        facility = SyslogFacility.Name(message.Facility),
        deviceTime = message.DeviceTime,
        text = message.Message
    };

    private static readonly SyslogReceiverSnapshot EmptyReceiver =
        new(SyslogListenerStatus.Off, SyslogListenerStatus.Off, 0, 0, 0, null, null);
}
