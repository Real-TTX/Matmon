using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

[Authorize]
public sealed class EventsModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public EventsModel(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

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

        return Page();
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
