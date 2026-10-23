using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// The two ways a real problem raises no alert because it is explained: a DEPENDENCY is down (the router a site hangs on -
/// everything behind it fails with it, and one alert for the router says it all), or a MAINTENANCE WINDOW is on (the
/// server reboots for its patches). Both only touch alerting: sensors keep running. A held dependent records Unknown with
/// the reason; a sensor in maintenance records what it sees and simply raises no new alert.
/// </summary>
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    // Sensors held because their dependency is down, and whether their OWN result was Critical - which is what makes them
    // down for the sensors that depend on THEM (a chain: router -> switch -> server). In memory: the next result of each
    // sensor rebuilds it after a restart (and until then the wait below covers the gap).
    private readonly Dictionary<Guid, (Guid Dependency, bool RealCritical)> _heldByDependency = [];

    // Failing sensors whose new alert waits for their dependency to be checked (MonitoringDependencies.WaitForCheck): the
    // router ping runs every 30 s, the HTTP check behind it every 5 minutes, and the HTTP check may simply notice first.
    private readonly Dictionary<Guid, Guid> _awaitingDependency = [];

    // Recording one sensor's result can hold its dependents, whose results can hold theirs - bounded, whatever the data.
    private int _dependencyDepth;

    private Dictionary<Guid, MonitoringElement> BuildElementIndexLocked() =>
        EnumerateElements(_document.RootProbe).ToDictionary(element => element.Id);

    /// <summary>Under <c>_gate</c>: the sensor's dependency (inherited, never itself, never through a loop back to it), or null.</summary>
    private SensorElement? ResolveDependencyLocked(Guid sensorId, IReadOnlyDictionary<Guid, MonitoringElement> index)
    {
        Guid? ConfiguredFor(Guid id) => index.TryGetValue(id, out var element) && element is SensorElement
            ? MonitoringDependencies.Configured(MonitoringTopology.BuildLineage(element, index), id)
            : null;

        return MonitoringDependencies.ResolveWithoutLoop(sensorId, ConfiguredFor) is { } dependencyId
            && index.TryGetValue(dependencyId, out var dependency)
            && dependency is SensorElement sensor
                ? sensor
                : null;
    }

    /// <summary>Under <c>_gate</c>: is this sensor DOWN for the sensors that depend on it? Its own last result was Critical -
    /// also when it is held itself, which is what carries an outage down a chain.</summary>
    private bool IsDownForDependentsLocked(Guid sensorId) =>
        _heldByDependency.TryGetValue(sensorId, out var held)
            ? held.RealCritical
            : _telemetry.GetLatestObservation(sensorId)?.State == SensorState.Critical;

    /// <summary>Under <c>_gate</c>: the dependency that holds this result (a problem while the dependency is down), or null.</summary>
    private SensorElement? FindHoldingDependencyLocked(Guid sensorId, SensorExecutionResult result)
    {
        if (result.State is not (SensorState.Warning or SensorState.Critical))
        {
            return null;
        }

        var dependency = ResolveDependencyLocked(sensorId, BuildElementIndexLocked());
        return dependency is { IsPaused: false } && IsDownForDependentsLocked(dependency.Id) ? dependency : null;
    }

    /// <summary>Under <c>_gate</c>, after a result was recorded: what that means for the sensors around it. Sensors that waited
    /// for this one to be checked are released (and alert now, if it is up); when it has just gone down its failing
    /// dependents are held at once (no waiting for their next poll); when it has just come back up its held dependents are
    /// polled again (a disk sensor polled every 6 h would otherwise show Unknown for hours). True when an alert changed.</summary>
    private bool AfterDependencyResultLocked(Guid sensorId, bool wasDown, DateTimeOffset timestampUtc)
    {
        var isDown = IsDownForDependentsLocked(sensorId);
        var waiting = _awaitingDependency.Where(pair => pair.Value == sensorId).Select(pair => pair.Key).ToList();
        foreach (var id in waiting)
        {
            _awaitingDependency.Remove(id);
        }

        if (isDown && !wasDown)
        {
            HoldDependentsLocked(sensorId, timestampUtc);
            return false;
        }

        if (!isDown && wasDown && _wakeQueue is { } wake)
        {
            foreach (var held in _heldByDependency.Where(pair => pair.Value.Dependency == sensorId).Select(pair => pair.Key).ToList())
            {
                wake.Request(held);
            }
        }

        var changed = false;
        foreach (var id in waiting)
        {
            if (!_heldByDependency.ContainsKey(id) && _telemetry.GetLatestObservation(id) is { State: SensorState.Warning or SensorState.Critical } latest)
            {
                changed |= SyncSensorAlertFromObservation(id, ToResult(latest), timestampUtc);
            }
        }

        return changed;
    }

    /// <summary>Under <c>_gate</c>: the dependency has just gone down - every dependent that is failing right now gets a
    /// result "held" at once, through the normal recording path (so the event log, the held map and chains all follow).</summary>
    private void HoldDependentsLocked(Guid dependencyId, DateTimeOffset timestampUtc)
    {
        if (_dependencyDepth >= MonitoringDependencies.MaxDepth)
        {
            return;
        }

        var index = BuildElementIndexLocked();
        var dependents = index.Values.OfType<SensorElement>()
            .Where(sensor => sensor.Id != dependencyId && !sensor.IsPaused && !_heldByDependency.ContainsKey(sensor.Id))
            .Where(sensor => ResolveDependencyLocked(sensor.Id, index)?.Id == dependencyId)
            .ToList();

        _dependencyDepth++;
        try
        {
            foreach (var sensor in dependents)
            {
                if (_telemetry.GetLatestObservation(sensor.Id) is not { State: SensorState.Warning or SensorState.Critical } latest)
                {
                    continue; // fine (or not polled yet) - judged at its next poll like any other
                }

                var at = latest.TimestampUtc >= timestampUtc ? latest.TimestampUtc.AddMilliseconds(1) : timestampUtc;
                RecordSensorObservation(sensor.Id, new SensorExecutionResult(latest.State, TimeSpan.Zero, null, latest.Message), at);
            }
        }
        finally
        {
            _dependencyDepth--;
        }
    }

    /// <summary>Under <c>_gate</c>: should this failing sensor's NEW alert wait for its dependency to be checked? Asks for that
    /// check once (the dependency is polled at once) and gives up after <see cref="MonitoringDependencies.CheckWait"/>.</summary>
    private bool IsDependencyCheckPendingLocked(SensorElement sensor, DateTimeOffset now)
    {
        var dependency = ResolveDependencyLocked(sensor.Id, BuildElementIndexLocked());
        if (dependency is null || dependency.IsPaused)
        {
            _awaitingDependency.Remove(sensor.Id);
            return false;
        }

        var since = _problemSince.TryGetValue(sensor.Id, out var known) ? known : _telemetry.GetLatestObservation(sensor.Id)?.TimestampUtc ?? now;
        if (!MonitoringDependencies.WaitForCheck(since, _telemetry.GetLatestObservation(dependency.Id)?.TimestampUtc, now))
        {
            _awaitingDependency.Remove(sensor.Id);
            return false;
        }

        if (_awaitingDependency.TryAdd(sensor.Id, dependency.Id))
        {
            _wakeQueue?.Request(dependency.Id);
        }

        return true;
    }

    private static SensorExecutionResult ToResult(SensorObservation observation) =>
        new(observation.State, observation.Duration, observation.Value, observation.Message)
        {
            DefaultChannelKey = observation.DefaultChannelKey,
            Channels = observation.Channels
        };

    public MonitoringDependencyInfo? GetDependency(Guid sensorId)
    {
        lock (_gate)
        {
            var index = BuildElementIndexLocked();
            if (!index.TryGetValue(sensorId, out var element) || element is not SensorElement)
            {
                return null;
            }

            var dependency = ResolveDependencyLocked(sensorId, index);
            return dependency is null
                ? null
                : new MonitoringDependencyInfo(dependency.Id, dependency.Name, GetElementPath(dependency), IsDownForDependentsLocked(dependency.Id), _heldByDependency.ContainsKey(sensorId));
        }
    }

    public MonitoringDependencyInfo? GetInheritedDependency(Guid elementId)
    {
        lock (_gate)
        {
            var index = BuildElementIndexLocked();
            if (!index.TryGetValue(elementId, out var element) || element.ParentId is not Guid parentId || !index.TryGetValue(parentId, out var parent))
            {
                return null;
            }

            // What the parents say - for a sensor skipping itself, exactly like the resolution does.
            var inherited = MonitoringDependencies.Configured(MonitoringTopology.BuildLineage(parent, index), elementId);
            return inherited is { } id && index.TryGetValue(id, out var dependency) && dependency is SensorElement
                ? new MonitoringDependencyInfo(dependency.Id, dependency.Name, GetElementPath(dependency), IsDownForDependentsLocked(dependency.Id), false)
                : null;
        }
    }

    public string? DescribeDependencyProblem(Guid elementId, Guid? dependencySetting)
    {
        if (dependencySetting is not { } dependencyId || dependencyId == MonitoringDependencies.None)
        {
            return null;
        }

        lock (_gate)
        {
            var index = BuildElementIndexLocked();
            if (!index.TryGetValue(dependencyId, out var dependency) || dependency is not SensorElement)
            {
                return "The dependency must be a sensor that exists.";
            }

            if (dependencyId == elementId)
            {
                return "A sensor cannot depend on itself.";
            }

            if (!index.TryGetValue(elementId, out var element))
            {
                return null;
            }

            // Try it: with the new setting in place, does any sensor here end up depending on itself through the chain?
            var previous = element.Settings.DependencySensorId;
            element.Settings.DependencySensorId = dependencyId;
            try
            {
                Guid? ConfiguredFor(Guid id) => index.TryGetValue(id, out var candidate) && candidate is SensorElement
                    ? MonitoringDependencies.Configured(MonitoringTopology.BuildLineage(candidate, index), id)
                    : null;

                var affected = element is MonitoringContainerElement container
                    ? MonitoringTopology.EnumerateDescendants(container).OfType<SensorElement>()
                    : element is SensorElement sensor ? [sensor] : [];
                var looping = affected.FirstOrDefault(sensor =>
                    ConfiguredFor(sensor.Id) is not null && MonitoringDependencies.ResolveWithoutLoop(sensor.Id, ConfiguredFor) is null);
                return looping is null
                    ? null
                    : $"That would make a loop: '{dependency.Name}' itself depends - directly or through others - on '{looping.Name}'. A loop would hold both and alert nobody.";
            }
            finally
            {
                element.Settings.DependencySensorId = previous;
            }
        }
    }

    // ---- maintenance windows -----------------------------------------------------------------------------------------

    /// <summary>One-time windows that ended longer ago than this are tidied away.</summary>
    private static readonly TimeSpan EndedMaintenanceKept = TimeSpan.FromDays(30);

    public IReadOnlyList<MaintenanceWindow> GetMaintenanceWindows()
    {
        lock (_gate)
        {
            _document.MaintenanceWindows ??= [];
            var now = DateTimeOffset.UtcNow;
            if (_document.MaintenanceWindows.RemoveAll(window => window.Recurrence == MaintenanceRecurrence.Once && window.EndUtc is { } end && now - end > EndedMaintenanceKept) > 0)
            {
                QueueSave(SavePriority.Configuration);
            }

            return _document.MaintenanceWindows.Select(window => window.Clone()).ToList();
        }
    }

    public MaintenanceWindow? FindMaintenanceWindow(Guid id)
    {
        lock (_gate)
        {
            return _document.MaintenanceWindows?.FirstOrDefault(window => window.Id == id)?.Clone();
        }
    }

    public MaintenanceWindow SaveMaintenanceWindow(MaintenanceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var copy = window.Clone();
        copy.Name = copy.Name.Trim();
        copy.TargetTag = string.IsNullOrWhiteSpace(copy.TargetTag) ? null : copy.TargetTag.Trim();
        if (copy.TargetTag is not null)
        {
            copy.TargetElementId = null; // a tag wins, like on a notification rule
        }

        copy.DaysOfWeek = MaintenanceSchedule.OrderedDays(copy.DaysOfWeek).ToList();
        copy.Note = string.IsNullOrWhiteSpace(copy.Note) ? null : copy.Note.Trim();
        if (MaintenanceSchedule.Validate(copy) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        lock (_gate)
        {
            _document.MaintenanceWindows ??= [];
            if (copy.TargetElementId is { } targetId && FindElementInternal(targetId) is null)
            {
                throw new InvalidOperationException("The element the window covers no longer exists.");
            }

            var index = _document.MaintenanceWindows.FindIndex(existing => existing.Id == copy.Id);
            if (index >= 0)
            {
                copy.CreatedUtc = _document.MaintenanceWindows[index].CreatedUtc;
                copy.CreatedBy = _document.MaintenanceWindows[index].CreatedBy;
                _document.MaintenanceWindows[index] = copy;
            }
            else
            {
                if (copy.CreatedUtc == default)
                {
                    copy.CreatedUtc = DateTimeOffset.UtcNow;
                }

                _document.MaintenanceWindows.Add(copy);
            }

            QueueSave(SavePriority.Configuration);
            return copy.Clone();
        }
    }

    public bool DeleteMaintenanceWindow(Guid id)
    {
        lock (_gate)
        {
            if (_document.MaintenanceWindows?.RemoveAll(window => window.Id == id) is not > 0)
            {
                return false;
            }

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public MaintenanceStatus? GetActiveMaintenance(Guid elementId, DateTimeOffset now)
    {
        lock (_gate)
        {
            return ActiveMaintenanceLocked(elementId, now);
        }
    }

    public bool IsInMaintenance(Guid elementId, DateTimeOffset now) => GetActiveMaintenance(elementId, now) is not null;

    /// <summary>The time zone a window's wall-clock times are in: its own, else the workspace's display zone.</summary>
    public TimeZoneInfo MaintenanceZone(MaintenanceWindow window) =>
        DisplayTimeZone.Resolve(window.TimeZoneId) ?? DisplayTimeZone.Resolve(_document.DisplayTimeZoneId) ?? TimeZoneInfo.Local;

    /// <summary>Under <c>_gate</c>: the window that covers this element now (the one that ends last, when several do), or null.</summary>
    private MaintenanceStatus? ActiveMaintenanceLocked(Guid elementId, DateTimeOffset now)
    {
        if (_document.MaintenanceWindows is not { Count: > 0 } windows)
        {
            return null;
        }

        Dictionary<Guid, MonitoringElement>? index = null;
        MaintenanceStatus? found = null;
        foreach (var window in windows)
        {
            if (MaintenanceSchedule.ActiveAt(window, now, MaintenanceZone(window)) is not { } occurrence)
            {
                continue;
            }

            index ??= BuildElementIndexLocked();
            if (!NotificationRuleTargeting.Covers(window.TargetElementId, window.TargetTag, includeDescendants: true, elementId, index))
            {
                continue;
            }

            if (found is null || occurrence.EndUtc > found.EndsUtc)
            {
                found = new MaintenanceStatus(window.Id, window.Name, occurrence.EndUtc);
            }
        }

        return found;
    }
}
