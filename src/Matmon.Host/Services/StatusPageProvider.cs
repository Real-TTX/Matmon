using System.Collections.Concurrent;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed record StatusComponentView(
    StatusPageComponent Component,
    StatusComponentState State,
    string Label,
    string Tone,
    double? Uptime,
    IReadOnlyList<StatusDayBar> Bars);

public sealed record StatusGroupView(string? Name, IReadOnlyList<StatusComponentView> Components);

public sealed record StatusIncidentView(StatusIncident Incident, IReadOnlyList<string> ComponentNames);

public sealed record StatusMaintenanceView(string Name, string? Note, DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool Active, IReadOnlyList<string> ComponentNames);

public sealed record StatusPageView(
    StatusPage Page,
    StatusComponentState Overall,
    string OverallText,
    IReadOnlyList<StatusGroupView> Groups,
    IReadOnlyList<StatusIncidentView> OpenIncidents,
    IReadOnlyList<StatusIncidentView> PastIncidents,
    IReadOnlyList<StatusMaintenanceView> Maintenance,
    TimeZoneInfo Zone,
    DateTimeOffset GeneratedUtc);

/// <summary>
/// Builds what a status page shows: every component's state now (its sensors, maintenance, open incidents), its bars
/// from the daily state counts, the maintenance that covers it and the incident messages. The anonymous page is cached
/// for 30 s per page - it may be opened by many people at once during exactly the outage it reports.
/// </summary>
public sealed class StatusPageProvider(IMonitoringWorkspaceStore store)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    /// <summary>Maintenance this far ahead is announced.</summary>
    private static readonly TimeSpan MaintenanceAhead = TimeSpan.FromDays(7);

    /// <summary>Resolved incidents shown under "Past incidents".</summary>
    private static readonly TimeSpan PastIncidentsShown = TimeSpan.FromDays(14);

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, DateTimeOffset Updated, StatusPageView View)> _cache = new();

    public StatusPageView BuildCached(StatusPage page)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(page.Id, out var entry) && now - entry.At < CacheFor && entry.Updated == page.UpdatedUtc)
        {
            return entry.View;
        }

        var view = Build(page, now);
        _cache[page.Id] = (now, page.UpdatedUtc, view);
        return view;
    }

    public StatusPageView Build(StatusPage page, DateTimeOffset nowUtc)
    {
        var zone = DisplayTimeZone.Resolve(store.GetDisplayTimeZoneId()) ?? TimeZoneInfo.Utc;
        var latest = store.GetLatestSensorObservations();
        var elementsById = store.GetAllElements().ToDictionary(element => element.Id);
        var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);

        var sensorsByComponent = page.Components.ToDictionary(
            component => component.Id,
            component => string.IsNullOrWhiteSpace(component.TargetToken)
                ? (IReadOnlyList<SensorElement>)[]
                : store.ResolveTargetSensors(component.TargetToken).Where(sensor => !sensor.IsPaused).ToList());
        var allSensorIds = sensorsByComponent.Values.SelectMany(sensors => sensors).Select(sensor => sensor.Id).ToHashSet();
        var daily = store.GetDailyStates(allSensorIds, today.AddDays(-(page.UptimeDays - 1)))
            .ToLookup(state => state.SensorId);

        var openImpact = new Dictionary<Guid, StatusIncidentImpact>();
        foreach (var incident in page.Incidents.Where(incident => incident.IsOpen))
        {
            foreach (var componentId in incident.ComponentIds)
            {
                openImpact[componentId] = openImpact.TryGetValue(componentId, out var known) && known > incident.Impact ? known : incident.Impact;
            }
        }

        var components = new List<StatusComponentView>();
        foreach (var component in page.Components)
        {
            var sensors = sensorsByComponent[component.Id];
            var states = sensors
                .Select(sensor => latest.TryGetValue(sensor.Id, out var observation) ? observation.State : SensorState.Unknown)
                .ToList();
            var inMaintenance = sensors.Any(sensor => store.IsInMaintenance(sensor.Id, nowUtc));
            var state = StatusPageRules.ComponentState(states, inMaintenance, openImpact.TryGetValue(component.Id, out var impact) ? impact : null);
            var history = sensors.SelectMany(sensor => daily[sensor.Id]).ToList();
            components.Add(new StatusComponentView(
                component,
                state,
                StatusPageRules.Label(state),
                StatusPageRules.Tone(state),
                StatusPageRules.Uptime(history),
                StatusPageRules.DayBars(history, today, page.UptimeDays)));
        }

        // Groups in the order their first component appears - the admin orders the list, the page follows it.
        var groups = components
            .GroupBy(view => view.Component.Group ?? string.Empty)
            .Select(group => new StatusGroupView(group.Key.Length == 0 ? null : group.Key, group.ToList()))
            .ToList();

        var componentNames = page.Components.ToDictionary(component => component.Id, component => component.Name);
        IReadOnlyList<string> NamesOf(IEnumerable<Guid> ids) => ids.Where(componentNames.ContainsKey).Select(id => componentNames[id]).ToList();

        var open = page.Incidents
            .Where(incident => incident.IsOpen)
            .OrderByDescending(incident => incident.CreatedUtc)
            .Select(incident => new StatusIncidentView(incident, NamesOf(incident.ComponentIds)))
            .ToList();
        var past = page.Incidents
            .Where(incident => incident.ResolvedUtc is { } resolved && nowUtc - resolved <= PastIncidentsShown)
            .OrderByDescending(incident => incident.ResolvedUtc)
            .Select(incident => new StatusIncidentView(incident, NamesOf(incident.ComponentIds)))
            .ToList();

        var maintenance = page.ShowMaintenance
            ? BuildMaintenance(page, sensorsByComponent, elementsById, componentNames, nowUtc)
            : [];

        var (overall, overallText) = StatusPageRules.Overall(components.Select(view => view.State).ToList());
        return new StatusPageView(page, overall, overallText, groups, open, past, maintenance, zone, nowUtc);
    }

    private List<StatusMaintenanceView> BuildMaintenance(
        StatusPage page,
        IReadOnlyDictionary<Guid, IReadOnlyList<SensorElement>> sensorsByComponent,
        IReadOnlyDictionary<Guid, MonitoringElement> elementsById,
        IReadOnlyDictionary<Guid, string> componentNames,
        DateTimeOffset nowUtc)
    {
        var result = new List<StatusMaintenanceView>();
        foreach (var window in store.GetMaintenanceWindows().Where(window => window.Enabled))
        {
            var covered = page.Components
                .Where(component => sensorsByComponent[component.Id].Any(sensor =>
                    NotificationRuleTargeting.Covers(window.TargetElementId, window.TargetTag, true, sensor.Id, elementsById)))
                .Select(component => component.Id)
                .ToList();
            if (covered.Count == 0)
            {
                continue;
            }

            var zone = store.MaintenanceZone(window);
            var active = MaintenanceSchedule.ActiveAt(window, nowUtc, zone);
            var occurrence = active ?? MaintenanceSchedule.Next(window, nowUtc, zone);
            if (occurrence is not { } when || (active is null && when.StartUtc - nowUtc > MaintenanceAhead))
            {
                continue;
            }

            result.Add(new StatusMaintenanceView(
                window.Name,
                window.Note,
                when.StartUtc,
                when.EndUtc,
                active is not null,
                covered.Select(id => componentNames[id]).ToList()));
        }

        return result.OrderByDescending(view => view.Active).ThenBy(view => view.StartUtc).ToList();
    }
}
