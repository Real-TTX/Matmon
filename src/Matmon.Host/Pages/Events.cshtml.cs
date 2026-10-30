using Matmon.Core.Domain;
using Matmon.Core.Syslog;
using Matmon.Host.Services;
using Matmon.Host.Services.Plugins;
using Matmon.Probe.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Events: what Matmon saw happen (state changes, alerts, edits) - and, as a second tab while a syslog or SNMP trap collector
/// runs (a tab the collector plugins bring along), what the network's devices said. The syslog half is read here by
/// operators and admins; the collectors are set up under System > Plugins, the log under System > Syslog.
/// </summary>
[Authorize]
public sealed class EventsModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly PluginUi _plugins;
    private readonly ISyslogStore? _syslog;

    // The syslog store exists on a primary only; on a probe the tab is simply not there.
    public EventsModel(IMonitoringWorkspaceStore workspaceStore, PluginUi plugins, ISyslogStore? syslog = null)
    {
        _workspaceStore = workspaceStore;
        _plugins = plugins;
        _syslog = syslog;
    }

    // ---- the Syslog tab's filter, so a link can open the tab already filtered (a sensor's "Log" button, a sender) -------

    [BindProperty(SupportsGet = true, Name = "source")]
    public string? SyslogSource { get; set; }

    [BindProperty(SupportsGet = true, Name = "window")]
    public int SyslogWindow { get; set; } = 60;

    [BindProperty(SupportsGet = true, Name = "severity")]
    public string? SyslogMinSeverity { get; set; }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? SyslogText { get; set; }

    [BindProperty(SupportsGet = true, Name = "regex")]
    public bool SyslogUseRegex { get; set; }

    /// <summary>The Syslog tab - null when it is not offered (receiver off and nothing logged, a viewer, a probe).</summary>
    public SyslogMessagesViewModel? SyslogView { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Kind { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Take { get; set; } = 250;

    public EventsViewModel View { get; private set; } = default!;

    public IActionResult OnGet()
    {
        Take = Math.Clamp(Take, 50, 1000);

        var events = _workspaceStore.GetEvents(Take).AsEnumerable();
        events = ApplyFilters(events);

        View = new EventsViewModel
        {
            KindFilter = NormalizeFilter(Kind),
            StateFilter = NormalizeFilter(State),
            Search = Search?.Trim() ?? string.Empty,
            Take = Take,
            Events = events.Select(BuildRow).ToArray()
        };
        SyslogView = BuildSyslogView();

        return Page();
    }

    // ---- the Syslog tab ----------------------------------------------------------------------------------------------

    private bool CanReadSyslog => _syslog is not null && MatmonSecurity.CanOperateAlerts(User);

    private SyslogMessagesViewModel? BuildSyslogView()
    {
        if (!CanReadSyslog)
        {
            return null;
        }

        var latest = _syslog!.Latest(new SyslogQuery());
        var collectors = _plugins.Placements(SyslogCollectorPlugin.Id).Concat(_plugins.Placements(SnmpTrapCollectorPlugin.Id)).ToList();
        var active = collectors.Any(collector => collector.Assignment.Enabled);
        if (!SyslogPageSupport.ShowSyslogTab(canRead: true, active))
        {
            return null;
        }

        return new SyslogMessagesViewModel(
            SyslogWindow,
            SyslogSource,
            SyslogMinSeverity,
            SyslogText,
            SyslogUseRegex,
            active,
            latest is not null,
            CanConfigure: MatmonSecurity.IsAdmin(User),
            SyslogPageSupport.DescribeCollectors(collectors),
            latest is not null ? "last message " + SyslogPageSupport.Ago(latest.ReceivedUtc, DateTimeOffset.UtcNow) : null);
    }

    /// <summary>The log as JSON, newest first, one page at a time (<paramref name="before"/> is the id of the oldest row the
    /// client already has). The totals are only asked for when the filter changes - a live refresh every few seconds must
    /// not count a big window each time.</summary>
    public IActionResult OnGetSyslogData(int window = 60, string? source = null, string? severity = null, string? q = null, bool regex = false, long? before = null, bool counts = true)
    {
        if (!CanReadSyslog)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!SyslogPageSupport.TryBuildQuery(window, source, severity, q, regex, before, DateTimeOffset.UtcNow, out var query, out var error))
        {
            return new JsonResult(new { items = Array.Empty<object>(), hasMore = false, counts = (object?)null, error });
        }

        var items = _syslog!.Search(query, SyslogPageSupport.PageSize);
        object? summary = null;
        if (counts)
        {
            var totals = _syslog.Count(query with { BeforeId = null });
            summary = new { total = totals.Total, errors = totals.Errors, warnings = totals.Warnings };
        }

        return new JsonResult(new
        {
            items = items.Select(SyslogPageSupport.ToRow),
            hasMore = items.Count >= SyslogPageSupport.PageSize,
            counts = summary,
            error = (string?)null
        });
    }

    /// <summary>Just the totals for a filter - what the live view asks for after new rows arrived, instead of paying for
    /// them on every tick.</summary>
    public IActionResult OnGetSyslogCounts(int window = 60, string? source = null, string? severity = null, string? q = null, bool regex = false)
    {
        if (!CanReadSyslog)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!SyslogPageSupport.TryBuildQuery(window, source, severity, q, regex, beforeId: null, DateTimeOffset.UtcNow, out var query, out var error))
        {
            return new JsonResult(new { counts = (object?)null, error });
        }

        var totals = _syslog!.Count(query);
        return new JsonResult(new { counts = new { total = totals.Total, errors = totals.Errors, warnings = totals.Warnings }, error = (string?)null });
    }

    /// <summary>The names senders are known by (addresses and host names of the last 30 days), for the sender filter's
    /// suggestions. An aggregate over the log, so the tab asks for it only when someone starts to type a sender.</summary>
    public IActionResult OnGetSyslogSenders()
    {
        if (!CanReadSyslog)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var senders = SyslogPageSupport.BuildSenders(_syslog!.GetSenders(DateTimeOffset.UtcNow.AddDays(-30)));
        return new JsonResult(new { names = SyslogPageSupport.KnownSenderNames(senders) });
    }

    private IEnumerable<MonitoringEvent> ApplyFilters(IEnumerable<MonitoringEvent> events)
    {
        var kindFilter = NormalizeFilter(Kind);
        var stateFilter = NormalizeFilter(State);
        var search = Search?.Trim();

        if (!string.Equals(kindFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            events = events.Where(entry => string.Equals(entry.Kind.ToString(), kindFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(stateFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            events = events.Where(entry => string.Equals(GetStateKey(entry.State), stateFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            events = events.Where(entry =>
                entry.ElementName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.ElementPath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (entry.Details?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return events;
    }

    private static WorkspaceEventRow BuildRow(MonitoringEvent entry)
    {
        return new WorkspaceEventRow(
            entry.Id,
            entry.TimestampUtc.ToDisplay().ToString("dd.MM HH:mm:ss"),
            entry.Kind.ToString(),
            FormatKindLabel(entry.Kind),
            entry.ElementId,
            entry.ElementKind?.ToString() ?? string.Empty,
            entry.ElementName,
            entry.ElementPath,
            GetStateKey(entry.State),
            entry.State is null ? null : MonitoringStatePresentation.Label(entry.State.Value),
            entry.Message,
            entry.Details);
    }

    private static string NormalizeFilter(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "all" : value.Trim();
    }

    private static string GetStateKey(SensorState? state)
    {
        return state is null ? string.Empty : MonitoringStatePresentation.Key(state.Value);
    }

    private static string FormatKindLabel(MonitoringEventKind kind)
    {
        return kind switch
        {
            MonitoringEventKind.StateChanged => "State changed",
            MonitoringEventKind.AlertRaised => "Alert raised",
            MonitoringEventKind.AlertAcknowledged => "Alert acknowledged",
            MonitoringEventKind.AlertResolved => "Alert resolved",
            MonitoringEventKind.AlertMuted => "Alert muted",
            MonitoringEventKind.AlertUnmuted => "Alert unmuted",
            MonitoringEventKind.AlertRecovered => "Alert recovered",
            MonitoringEventKind.AlertReopened => "Alert re-opened",
            MonitoringEventKind.Created => "Created",
            MonitoringEventKind.Updated => "Updated",
            MonitoringEventKind.Moved => "Moved",
            MonitoringEventKind.Deleted => "Deleted",
            MonitoringEventKind.Paused => "Paused",
            MonitoringEventKind.Resumed => "Resumed",
            _ => "Info"
        };
    }
}

/// <summary>What the Syslog tab of the Events page is rendered from: the filter it opens with, whether the receiver is on,
/// whether the log holds anything, and the receiver's one-line state.</summary>
public sealed record SyslogMessagesViewModel(
    int Window,
    string? Source,
    string? Severity,
    string? Q,
    bool Regex,
    bool ReceiverEnabled,
    bool HasMessages,
    bool CanConfigure,
    SyslogReceiverHead Head,
    string? LastReceivedText)
{
    public int PageSize => SyslogPageSupport.PageSize;
}

public sealed record EventsViewModel
{
    public string KindFilter { get; init; } = "all";

    public string StateFilter { get; init; } = "all";

    public string Search { get; init; } = string.Empty;

    public int Take { get; init; }

    public IReadOnlyList<WorkspaceEventRow> Events { get; init; } = [];
}

public sealed record WorkspaceEventRow(
    Guid Id,
    string Timestamp,
    string KindKey,
    string KindLabel,
    Guid? ElementId,
    string ElementKind,
    string ElementName,
    string ElementPath,
    string StateKey,
    string? StateLabel,
    string Message,
    string? Details);
