using Matmon.Core.Domain;
using Matmon.Core.Sample;

namespace Matmon.Host.Services;

public sealed class DashboardSnapshotProvider : IDashboardSnapshotProvider
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly IProbeRegistry _probeRegistry;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly MonitoringInheritanceResolver _resolver = new();

    public DashboardSnapshotProvider(
        IMonitoringWorkspaceStore workspaceStore,
        IProbeRegistry probeRegistry,
        MatmonRuntimeOptions runtimeOptions)
    {
        _workspaceStore = workspaceStore;
        _probeRegistry = probeRegistry;
        _runtimeOptions = runtimeOptions;
    }

    public WorkspaceSummary GetWorkspaceSummary()
    {
        var elements = _workspaceStore.GetAllElements();
        var workspaceName = elements
            .OfType<ProbeElement>()
            .FirstOrDefault(probe => probe.ParentId is null)?.Name
            ?? "Workspace";

        var probeCount = 0;
        var sensorCount = 0;
        var pausedSensorCount = 0;
        var pausedSensorIds = new HashSet<Guid>();
        foreach (var element in elements)
        {
            switch (element.Kind)
            {
                case MonitoringElementKind.Probe:
                    probeCount++;
                    break;
                case MonitoringElementKind.Sensor:
                    sensorCount++;
                    if (element is SensorElement sensor && sensor.IsPaused)
                    {
                        pausedSensorCount++;
                        pausedSensorIds.Add(sensor.Id);
                    }
                    break;
            }
        }

        // Error/warning sensor counts from the cached latest-observation states (cheap -
        // no telemetry scan), so the sidebar alert badge renders real numbers on first
        // paint instead of flashing 0 → N once the client refresh lands.
        var errorSensorCount = 0;
        var warningSensorCount = 0;
        foreach (var (sensorId, observation) in _workspaceStore.GetLatestSensorObservations())
        {
            if (pausedSensorIds.Contains(sensorId))
            {
                continue;
            }

            switch (observation.State)
            {
                case SensorState.Critical:
                    errorSensorCount++;
                    break;
                case SensorState.Warning:
                    warningSensorCount++;
                    break;
            }
        }

        var (openAlerts, acknowledgedAlerts, errorAlerts, warningAlerts) = _workspaceStore.GetActiveAlertCounts();

        return new WorkspaceSummary(
            workspaceName,
            probeCount,
            sensorCount,
            openAlerts,
            acknowledgedAlerts,
            errorAlerts,
            warningAlerts,
            errorSensorCount,
            warningSensorCount,
            pausedSensorCount);
    }

    public DashboardSnapshot CreateSnapshot()
    {
        // Deep-cloned snapshot: this method walks the entire element tree (health, severity, flatten)
        // and must not race concurrent edits or the polling service.
        var workspace = _workspaceStore.GetWorkspaceClone();
        var now = DateTimeOffset.UtcNow;
        var templateMap = workspace.Templates.ToDictionary(template => template.Id);
        var sensorDefinitionMap = workspace.SensorDefinitions.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);
        var sensorHistoryMap = _workspaceStore.GetRecentSensorHistoryBySensor(TimeSpan.FromDays(1), maxPerSensor: 80);
        var connectedProbes = _probeRegistry.GetAll();
        var liveProbeMap = connectedProbes.ToDictionary(probe => probe.ProbeId, StringComparer.OrdinalIgnoreCase);
        var probeHealthMap = BuildProbeHealthMap(workspace.RootProbe, liveProbeMap, now);
        var activeAlerts = BuildActiveAlertCandidates(workspace, templateMap, probeHealthMap, sensorHistoryMap, now).ToArray();

        _workspaceStore.SyncAlerts(activeAlerts, now);
        workspace = _workspaceStore.GetWorkspaceClone();

        var nodeSeverities = new Dictionary<Guid, MonitoringSeverity>();

        ComputeNodeSeverities(
            workspace.RootProbe,
            new List<MonitoringElement>(),
            templateMap,
            probeHealthMap,
            sensorHistoryMap,
            nodeSeverities,
            inheritedSeverity: MonitoringSeverity.Ok,
            now);

        var nodes = new List<DashboardNodeViewModel>();
        var probes = new List<DashboardProbeViewModel>();
        var counters = new NodeCounters();
        var telemetrySeries = new List<TelemetrySeriesSnapshot>();

        Flatten(
            workspace.RootProbe,
            new List<MonitoringElement>(),
            0,
            templateMap,
            sensorDefinitionMap,
            probeHealthMap,
            sensorHistoryMap,
            nodeSeverities,
            nodes,
            probes,
            counters,
            telemetrySeries,
            now,
            inheritedSeverity: MonitoringSeverity.Ok);

        var activeAlertCount = workspace.Alerts.Count(alert => alert.IsActive && !alert.IsAcknowledged);
        var acknowledgedAlertCount = workspace.Alerts.Count(alert => alert.IsActive && alert.IsAcknowledged);
        // Open (unacknowledged) alerts split by severity - an acknowledged alert is "handled", so it
        // counts as Ack, not as an Error/Warning in the status (mirrors the Alerts page buckets).
        var errorAlertCount = workspace.Alerts.Count(alert => alert.IsActive && !alert.IsAcknowledged && alert.State != SensorState.Warning && alert.State != SensorState.Paused);
        var warningAlertCount = workspace.Alerts.Count(alert => alert.IsActive && !alert.IsAcknowledged && alert.State == SensorState.Warning);
        var pausedSensorCount = EnumerateElements(workspace.RootProbe).OfType<SensorElement>().Count(sensor => sensor.IsPaused);
        var acknowledgedSensorIds = workspace.Alerts
            .Where(alert => alert.IsActive && alert.IsAcknowledged && alert.ElementKind == MonitoringElementKind.Sensor)
            .Select(alert => alert.ElementId)
            .ToHashSet();
        var healthySensorCount = telemetrySeries.Count(series => IsDashboardState(series.StateKey, "ok") && !acknowledgedSensorIds.Contains(series.SensorId));
        var warningSensorCount = telemetrySeries.Count(series => IsDashboardState(series.StateKey, "warning") && !acknowledgedSensorIds.Contains(series.SensorId));
        var acknowledgedSensorCount = telemetrySeries.Count(series => acknowledgedSensorIds.Contains(series.SensorId));
        var errorSensorCount = telemetrySeries.Count(series => IsDashboardState(series.StateKey, "error") && !acknowledgedSensorIds.Contains(series.SensorId));
        var otherSensorCount = Math.Max(0, counters.Sensors - healthySensorCount - warningSensorCount - acknowledgedSensorCount - errorSensorCount);
        var highlightedTelemetrySeries = telemetrySeries
            .Where(series => series.IsHighlighted)
            .OrderByDescending(series => GetDashboardSeriesSortRank(series.StateKey))
            .ThenBy(series => series.Path)
            .ToArray();

        return new DashboardSnapshot(
            _runtimeOptions.Mode,
            workspace.RootProbe.Name,
            now,
            counters.Probes,
            counters.Hosts,
            counters.Sensors,
            workspace.Templates.Count,
            workspace.NotificationRules.Count,
            activeAlertCount,
            acknowledgedAlertCount,
            errorAlertCount,
            warningAlertCount,
            pausedSensorCount,
            healthySensorCount,
            warningSensorCount,
            acknowledgedSensorCount,
            errorSensorCount,
            otherSensorCount,
            nodes,
            connectedProbes,
            probes,
            workspace.SensorDefinitions,
            telemetrySeries
                .OrderByDescending(series => GetDashboardSeriesSortRank(series.StateKey))
                .ThenBy(series => series.Path)
                .ToArray(),
            highlightedTelemetrySeries);
    }

    private Dictionary<Guid, ProbeHealthSnapshot> BuildProbeHealthMap(
        MonitoringElement root,
        IReadOnlyDictionary<string, ProbeStatusSnapshot> liveProbes,
        DateTimeOffset now)
    {
        var probeHealthMap = new Dictionary<Guid, ProbeHealthSnapshot>();
        var heartbeatWindowSeconds = Math.Clamp(_runtimeOptions.HeartbeatIntervalSeconds, 5, 300);

        foreach (var probe in EnumerateElements(root).OfType<ProbeElement>())
        {
            if (probe.ParentId is null)
            {
                probeHealthMap[probe.Id] = new ProbeHealthSnapshot(
                    MonitoringSeverity.Ok,
                    "local",
                    "local primary");
                continue;
            }

            if (!liveProbes.TryGetValue(probe.ProbeId, out var liveSnapshot))
            {
                probeHealthMap[probe.Id] = new ProbeHealthSnapshot(
                    MonitoringSeverity.Error,
                    "-",
                    "no heartbeat");
                continue;
            }

            var ageSeconds = Math.Max((now - liveSnapshot.LastSeenUtc).TotalSeconds, 0);
            var severity = MonitoringStatePresentation.FromHeartbeatAge(ageSeconds, heartbeatWindowSeconds);
            probeHealthMap[probe.Id] = new ProbeHealthSnapshot(
                severity,
                liveSnapshot.LastSeenUtc.ToDisplay().ToString("HH:mm:ss"),
                BuildHeartbeatMessage(severity, ageSeconds));
        }

        return probeHealthMap;
    }

    private IReadOnlyList<MonitoringAlertCandidate> BuildActiveAlertCandidates(
        MonitoringWorkspaceSnapshot workspace,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templates,
        IReadOnlyDictionary<Guid, ProbeHealthSnapshot> probeHealthMap,
        IReadOnlyDictionary<Guid, SensorObservation[]> sensorHistoryMap,
        DateTimeOffset now)
    {
        var alerts = new List<MonitoringAlertCandidate>();
        BuildActiveAlertCandidates(
            workspace.RootProbe,
            new List<MonitoringElement>(),
            templates,
            probeHealthMap,
            sensorHistoryMap,
            alerts,
            now,
            string.Empty);
        return alerts;
    }

    private void BuildActiveAlertCandidates(
        MonitoringElement element,
        List<MonitoringElement> lineage,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templates,
        IReadOnlyDictionary<Guid, ProbeHealthSnapshot> probeHealthMap,
        IReadOnlyDictionary<Guid, SensorObservation[]> sensorHistoryMap,
        List<MonitoringAlertCandidate> alerts,
        DateTimeOffset now,
        string parentPath)
    {
        lineage.Add(element);

        var path = string.IsNullOrWhiteSpace(parentPath) ? element.Name : $"{parentPath} / {element.Name}";

        switch (element)
        {
            case ProbeElement probe when probe.ParentId is not null && probeHealthMap.TryGetValue(probe.Id, out var probeHealth) && probeHealth.Severity != MonitoringSeverity.Ok:
                alerts.Add(new MonitoringAlertCandidate(
                    probe.Id,
                    probe.Kind,
                    probe.Name,
                    path,
                    probeHealth.Severity == MonitoringSeverity.Warning ? SensorState.Warning : SensorState.Critical,
                    probeHealth.Message));
                break;
            case SensorElement sensorElement when !sensorElement.IsPaused:
            {
                if (TryGetLatestSensorObservation(sensorHistoryMap, sensorElement.Id) is { } latestObservation &&
                    latestObservation.State is SensorState.Warning or SensorState.Critical)
                {
                    var defaultValue = GetObservationDefaultValue(latestObservation);
                    alerts.Add(new MonitoringAlertCandidate(
                        sensorElement.Id,
                        sensorElement.Kind,
                        sensorElement.Name,
                        path,
                        latestObservation.State,
                        latestObservation.Message ?? BuildSensorStateMessage(sensorElement.SensorTypeKey, defaultValue, latestObservation.State)));
                }
                break;
            }
        }

        if (element is MonitoringContainerElement container)
        {
            foreach (var child in container.Children)
            {
                BuildActiveAlertCandidates(child, lineage, templates, probeHealthMap, sensorHistoryMap, alerts, now, path);
            }
        }

        lineage.RemoveAt(lineage.Count - 1);
    }

    private MonitoringSeverity ComputeNodeSeverities(
        MonitoringElement element,
        List<MonitoringElement> lineage,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templates,
        IReadOnlyDictionary<Guid, ProbeHealthSnapshot> probeHealthMap,
        IReadOnlyDictionary<Guid, SensorObservation[]> sensorHistoryMap,
        Dictionary<Guid, MonitoringSeverity> nodeSeverities,
        MonitoringSeverity inheritedSeverity,
        DateTimeOffset now)
    {
        lineage.Add(element);

        var ownSeverity = element switch
        {
            ProbeElement probe when probeHealthMap.TryGetValue(probe.Id, out var health) => MonitoringStatePresentation.Max(
                inheritedSeverity,
                health.Severity),
            SensorElement sensorElement => GetSensorNodeSeverity(
                inheritedSeverity,
                sensorElement,
                TryGetLatestSensorObservation(sensorHistoryMap, sensorElement.Id),
                EffectiveStaleAfter(_resolver.Resolve(lineage, templates), sensorElement.SensorTypeKey)),
            _ => inheritedSeverity
        };

        var subtreeSeverity = ownSeverity;
        var childInheritedSeverity = element is ProbeElement ? ownSeverity : inheritedSeverity;

        if (element is MonitoringContainerElement container)
        {
            foreach (var child in container.Children)
            {
                var childSeverity = ComputeNodeSeverities(
                    child,
                    lineage,
                    templates,
                    probeHealthMap,
                    sensorHistoryMap,
                    nodeSeverities,
                    childInheritedSeverity,
                    now);

                subtreeSeverity = MonitoringStatePresentation.Max(subtreeSeverity, childSeverity);
            }
        }

        nodeSeverities[element.Id] = subtreeSeverity;
        lineage.RemoveAt(lineage.Count - 1);
        return subtreeSeverity;
    }

    private void Flatten(
        MonitoringElement element,
        List<MonitoringElement> lineage,
        int depth,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templates,
        IReadOnlyDictionary<string, SensorDefinition> sensorDefinitions,
        IReadOnlyDictionary<Guid, ProbeHealthSnapshot> probeHealthMap,
        IReadOnlyDictionary<Guid, SensorObservation[]> sensorHistoryMap,
        IReadOnlyDictionary<Guid, MonitoringSeverity> nodeSeverities,
        List<DashboardNodeViewModel> nodes,
        List<DashboardProbeViewModel> probes,
        NodeCounters counters,
        List<TelemetrySeriesSnapshot> telemetrySeries,
        DateTimeOffset now,
        MonitoringSeverity inheritedSeverity)
    {
        lineage.Add(element);

        var effectiveSettings = _resolver.Resolve(lineage, templates);
        var state = nodeSeverities.TryGetValue(element.Id, out var severity)
            ? severity
            : inheritedSeverity;
        var sensorObservation = element is SensorElement observedSensor
            ? TryGetLatestSensorObservation(sensorHistoryMap, observedSensor.Id)
            : null;
        var sensorPresentation = element is SensorElement presentationSensor
            ? BuildSensorPresentation(presentationSensor, sensorObservation, EffectiveStaleAfter(effectiveSettings, presentationSensor.SensorTypeKey))
            : null;
        var stateKey = sensorPresentation?.StateKey ?? MonitoringStatePresentation.Key(state);
        var stateLabel = sensorPresentation?.StateLabel ?? MonitoringStatePresentation.Label(state);
        var stateColor = sensorPresentation?.StateColor ?? MonitoringStatePresentation.Color(state);
        var stateMessage = sensorPresentation?.StateMessage
            ?? (element is ProbeElement probeNode && probeHealthMap.TryGetValue(probeNode.Id, out var probeNodeHealth)
                ? probeNodeHealth.Message
                : null);
        var isHighlighted = element is SensorElement && effectiveSettings.Highlight == true;
        var templateNames = element.AppliedTemplateIds
            .Select(templateId => templates.TryGetValue(templateId, out var template)
                ? template.Name
                : templateId.ToString("N")[..8])
            .ToArray();

        nodes.Add(new DashboardNodeViewModel(
            element.Id,
            element.Name,
            element.Kind,
            depth,
            BuildDetails(element, lineage),
            effectiveSettings.Summary(),
            templateNames.Length == 0 ? "no template" : string.Join(" / ", templateNames),
            isHighlighted,
            stateKey,
            stateLabel,
            stateColor,
            stateMessage));

        switch (element.Kind)
        {
            case MonitoringElementKind.Probe:
                counters.Probes++;

                if (element is ProbeElement probeItem && probeHealthMap.TryGetValue(probeItem.Id, out var probeItemHealth))
                {
                    probes.Add(new DashboardProbeViewModel(
                        probeItem.ProbeId,
                        probeItem.Name,
                        stateKey,
                        stateLabel,
                        stateColor,
                        probeItemHealth.LastSeen,
                        probeItemHealth.Message));
                }
                break;
            case MonitoringElementKind.Host:
                counters.Hosts++;
                break;
            case MonitoringElementKind.Sensor:
                counters.Sensors++;

                if (element is SensorElement sensorForSeries)
                {
                    telemetrySeries.Add(BuildSensorSeries(lineage, sensorForSeries, effectiveSettings, sensorDefinitions, sensorHistoryMap));
                }
                break;
        }

        if (element is MonitoringContainerElement container)
        {
            var childInheritedSeverity = element is ProbeElement
                ? state
                : inheritedSeverity;

            foreach (var child in container.Children)
            {
                Flatten(child, lineage, depth + 1, templates, sensorDefinitions, probeHealthMap, sensorHistoryMap, nodeSeverities, nodes, probes, counters, telemetrySeries, now, childInheritedSeverity);
            }
        }

        lineage.RemoveAt(lineage.Count - 1);
    }

    private IReadOnlyList<TelemetrySeriesSnapshot> BuildProbeSeries(
        IReadOnlyList<ProbeStatusSnapshot> probes,
        IReadOnlyDictionary<string, MonitoringSeverity> effectiveSeverities,
        DateTimeOffset now)
    {
        if (probes.Count == 0)
        {
            return [];
        }

        var series = new List<TelemetrySeriesSnapshot>(probes.Count);
        var heartbeatWindowSeconds = Math.Clamp(_runtimeOptions.HeartbeatIntervalSeconds, 5, 300);

        foreach (var probe in probes)
        {
            var points = new List<TelemetrySamplePoint>();
            const int samples = 40;
            var sampleStepSeconds = Math.Max(heartbeatWindowSeconds / 6.0, 1.0);
            var interval = TimeSpan.FromSeconds(sampleStepSeconds);
            var ownSeverity = MonitoringStatePresentation.FromHeartbeatAge(
                Math.Max((now - probe.LastSeenUtc).TotalSeconds, 0),
                heartbeatWindowSeconds);
            var displaySeverity = effectiveSeverities.TryGetValue(probe.ProbeId, out var severity)
                ? severity
                : ownSeverity;

            for (var index = 0; index < samples; index++)
            {
                var timestamp = now - interval * (samples - index - 1);
                var ageSeconds = GetHeartbeatAgeSeconds(timestamp, probe.LastSeenUtc, heartbeatWindowSeconds);
                var state = ToSensorState(displaySeverity);
                points.Add(new TelemetrySamplePoint(timestamp, ageSeconds, state));
            }

            series.Add(new TelemetrySeriesSnapshot(
                $"probe:{probe.ProbeId}:heartbeat",
                Guid.Empty,
                probe.ProbeName,
                "Probe Heartbeat",
                probe.ProbeId,
                $"{probe.ProbeName} heartbeat",
                "Seconds since last heartbeat",
                "s",
                ToSensorState(displaySeverity),
                points[^1].Value,
                MonitoringStatePresentation.Color(displaySeverity),
                false,
                points,
                MonitoringStatePresentation.Key(displaySeverity),
                MonitoringStatePresentation.Label(displaySeverity),
                MonitoringStatePresentation.Color(displaySeverity)));
        }

        return series;
    }

    private TelemetrySeriesSnapshot BuildSensorSeries(
        IReadOnlyList<MonitoringElement> lineage,
        SensorElement sensor,
        MonitoringSettings effectiveSettings,
        IReadOnlyDictionary<string, SensorDefinition> sensorDefinitions,
        IReadOnlyDictionary<Guid, SensorObservation[]> sensorHistoryMap)
    {
        var defaultChannelKey = effectiveSettings.DefaultChannelKey;
        var key = $"sensor:{sensor.Id:N}";
        var title = sensor.Name;
        var path = string.Join(" / ", lineage.Select(item => item.Name));
        var description = effectiveSettings.Summary();
        var unit = GetSensorUnit(sensor.SensorTypeKey);
        var observations = TryGetSensorObservations(sensorHistoryMap, sensor.Id);
        var latestObservation = observations.Count == 0 ? null : observations[^1];
        var display = BuildSensorPresentation(sensor, latestObservation, EffectiveStaleAfter(effectiveSettings, sensor.SensorTypeKey), defaultChannelKey);
        // Humanize the current value exactly like the sensor-detail view + notifications: take the latest
        // observation's DEFAULT channel unit (the per-type fallback only when the channel has none) and auto-scale
        // (e.g. SNMP uptime seconds -> days). The tree, the dashboard cards and the live /api/dashboard refresh all
        // read the series' CurrentValue/Unit, so before this they showed the raw number (raw seconds) in the tree.
        var defaultChannel = latestObservation?.Channels.FirstOrDefault(channel =>
                channel.IsDefault ||
                (!string.IsNullOrWhiteSpace(defaultChannelKey) &&
                 string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase)))
            ?? latestObservation?.Channels.FirstOrDefault();
        var rawUnit = string.IsNullOrWhiteSpace(defaultChannel?.Unit) ? unit : defaultChannel!.Unit;
        var measurementKind = defaultChannel?.MeasurementKind ?? SensorUnitConverter.GuessMeasurementKind(rawUnit);
        var currentValueDisplay = SensorUnitConverter.Format(display.CurrentValue, rawUnit, measurementKind);
        var isHighlighted = effectiveSettings.Highlight == true;
        var sensorTypeLabel = sensorDefinitions.TryGetValue(sensor.SensorTypeKey, out var sensorDefinition)
            ? sensorDefinition.DisplayName
            : sensor.SensorTypeKey;
        var points = observations
            .Select(observation => SensorHistoryAnalytics.BuildGraphPoint(observation, defaultChannelKey))
            .Where(point => point is not null)
            .Select(point => point!)
            .ToArray();

        return new TelemetrySeriesSnapshot(
            key,
            sensor.Id,
            path,
            sensorTypeLabel,
            SensorTargetResolver.Resolve(sensor, lineage),
            title,
            description,
            currentValueDisplay.Unit,
            display.State,
            currentValueDisplay.Value,
            display.StateColor,
            isHighlighted,
            points,
            display.StateKey,
            display.StateLabel,
            display.StateColor);
    }

    private static IReadOnlyList<SensorObservation> TryGetSensorObservations(
        IReadOnlyDictionary<Guid, SensorObservation[]> sensorHistoryMap,
        Guid sensorId)
    {
        return sensorHistoryMap.TryGetValue(sensorId, out var observations)
            ? observations
            : Array.Empty<SensorObservation>();
    }

    private static SensorObservation? TryGetLatestSensorObservation(
        IReadOnlyDictionary<Guid, SensorObservation[]> sensorHistoryMap,
        Guid sensorId)
    {
        return sensorHistoryMap.TryGetValue(sensorId, out var observations) && observations.Length > 0
            ? observations[^1]
            : null;
    }

    // A reading is "overdue" only past ~2x the sensor's OWN effective cadence (its configured interval, else the
    // per-type default) - NOT the type default alone. Using the type default made a slow-polling sensor (e.g. a
    // ping set to every 2 min) flip grey after the 30 s ping type-default window and flap green<->grey between
    // its real polls. Floored so the fastest sensors don't flap on normal poll jitter.
    private static TimeSpan EffectiveStaleAfter(MonitoringSettings settings, string sensorTypeKey)
    {
        var interval = settings.PollingInterval is { } configured && configured > TimeSpan.Zero
            ? configured
            : settings.PollingSchedule is { Mode: MonitoringScheduleMode.Every, EverySeconds: { } every } && every > 0
                ? TimeSpan.FromSeconds(every)
                : SensorScheduleDefaults.Resolve(sensorTypeKey);
        var staleAfter = interval * 2;
        return staleAfter < TimeSpan.FromSeconds(90) ? TimeSpan.FromSeconds(90) : staleAfter;
    }

    private static SensorDisplaySnapshot BuildSensorPresentation(
        SensorElement sensor,
        SensorObservation? latestObservation,
        TimeSpan staleAfter,
        string? defaultChannelKey = null)
    {
        if (sensor.IsPaused)
        {
            return new SensorDisplaySnapshot(
                SensorState.Paused,
                MonitoringStatePresentation.PausedKey,
                MonitoringStatePresentation.PausedLabel,
                MonitoringStatePresentation.PausedColor,
                "sensor paused",
                null);
        }

        if (latestObservation is null)
        {
            return new SensorDisplaySnapshot(
                SensorState.Unknown,
                MonitoringStatePresentation.UnknownKey,
                MonitoringStatePresentation.UnknownLabel,
                MonitoringStatePresentation.UnknownColor,
                "no measurements yet",
                null);
        }

        // Overdue run → Unknown: if the last reading is well past this sensor's OWN cadence (a paused folder
        // was resumed, or the node was down), the old value no longer reflects reality. It flips back to its
        // real state as soon as the catch-up poll writes a fresh observation.
        if (DateTimeOffset.UtcNow - latestObservation.TimestampUtc > staleAfter)
        {
            return new SensorDisplaySnapshot(
                SensorState.Unknown,
                MonitoringStatePresentation.UnknownKey,
                MonitoringStatePresentation.UnknownLabel,
                MonitoringStatePresentation.UnknownColor,
                "awaiting refresh (overdue)",
                GetObservationDefaultValue(latestObservation, defaultChannelKey));
        }

        var defaultValue = GetObservationDefaultValue(latestObservation, defaultChannelKey);

        return latestObservation.State switch
        {
            SensorState.Healthy => new SensorDisplaySnapshot(
                SensorState.Healthy,
                MonitoringStatePresentation.Key(SensorState.Healthy),
                MonitoringStatePresentation.Label(SensorState.Healthy),
                MonitoringStatePresentation.Color(SensorState.Healthy),
                latestObservation.Message ?? BuildSensorStateMessage(sensor.SensorTypeKey, defaultValue, SensorState.Healthy),
                defaultValue),
            SensorState.Warning => new SensorDisplaySnapshot(
                SensorState.Warning,
                MonitoringStatePresentation.Key(SensorState.Warning),
                MonitoringStatePresentation.Label(SensorState.Warning),
                MonitoringStatePresentation.Color(SensorState.Warning),
                latestObservation.Message ?? BuildSensorStateMessage(sensor.SensorTypeKey, defaultValue, SensorState.Warning),
                defaultValue),
            SensorState.Critical => new SensorDisplaySnapshot(
                SensorState.Critical,
                MonitoringStatePresentation.Key(SensorState.Critical),
                MonitoringStatePresentation.Label(SensorState.Critical),
                MonitoringStatePresentation.Color(SensorState.Critical),
                latestObservation.Message ?? BuildSensorStateMessage(sensor.SensorTypeKey, defaultValue, SensorState.Critical),
                defaultValue),
            SensorState.Disabled => new SensorDisplaySnapshot(
                SensorState.Disabled,
                MonitoringStatePresentation.Key(SensorState.Disabled),
                MonitoringStatePresentation.Label(SensorState.Disabled),
                MonitoringStatePresentation.Color(SensorState.Disabled),
                latestObservation.Message ?? "sensor disabled",
                defaultValue),
            SensorState.Paused => new SensorDisplaySnapshot(
                SensorState.Paused,
                MonitoringStatePresentation.PausedKey,
                MonitoringStatePresentation.PausedLabel,
                MonitoringStatePresentation.PausedColor,
                latestObservation.Message ?? "sensor paused",
                defaultValue),
            _ => new SensorDisplaySnapshot(
                SensorState.Unknown,
                MonitoringStatePresentation.UnknownKey,
                MonitoringStatePresentation.UnknownLabel,
                MonitoringStatePresentation.UnknownColor,
                latestObservation.Message ?? "no measurements yet",
                defaultValue)
        };
    }

    private static double? GetObservationDefaultValue(SensorObservation? observation, string? defaultChannelKey = null)
    {
        return SensorHistoryAnalytics.GetDefaultValue(observation, defaultChannelKey);
    }

    private static MonitoringSeverity GetSensorNodeSeverity(
        MonitoringSeverity inheritedSeverity,
        SensorElement sensor,
        SensorObservation? latestObservation,
        TimeSpan staleAfter)
    {
        if (sensor.IsPaused || latestObservation is null)
        {
            return inheritedSeverity;
        }

        // Overdue run → treat as Unknown (contributes no severity), matching the sensor's own Unknown display,
        // so a stale reading (e.g. after a paused folder resumes) doesn't keep the tree coloured on old data.
        if (DateTimeOffset.UtcNow - latestObservation.TimestampUtc > staleAfter)
        {
            return inheritedSeverity;
        }

        return latestObservation.State switch
        {
            SensorState.Healthy => MonitoringStatePresentation.Max(inheritedSeverity, MonitoringSeverity.Ok),
            SensorState.Warning => MonitoringStatePresentation.Max(inheritedSeverity, MonitoringSeverity.Warning),
            SensorState.Critical => MonitoringStatePresentation.Max(inheritedSeverity, MonitoringSeverity.Error),
            SensorState.Disabled => inheritedSeverity,
            SensorState.Paused => inheritedSeverity,
            SensorState.Unknown => inheritedSeverity,
            _ => inheritedSeverity
        };
    }

    private static string BuildSensorStateMessage(string sensorTypeKey, double? value, SensorState state)
    {
        var unit = GetSensorUnit(sensorTypeKey);

        if (state == SensorState.Disabled)
        {
            return "sensor disabled";
        }

        if (!value.HasValue)
        {
            if (string.Equals(sensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
            {
                return state == SensorState.Critical
                    ? "no heartbeat received"
                    : "heartbeat pending";
            }

            return state == SensorState.Critical
                ? "measurement failed"
                : "no measurements yet";
        }

        return $"value {value.Value:0.#}{unit}";
    }

    private static string BuildHeartbeatMessage(MonitoringSeverity severity, double ageSeconds)
    {
        return severity switch
        {
            MonitoringSeverity.Ok => $"heartbeat {ageSeconds:0.#}s ago",
            MonitoringSeverity.Warning => $"heartbeat delayed {ageSeconds:0.#}s",
            MonitoringSeverity.Error => "no heartbeat",
            _ => "no heartbeat"
        };
    }

    private static int GetDashboardSeriesSortRank(string stateKey)
    {
        return stateKey.ToLowerInvariant() switch
        {
            "error" => 3,
            "warning" => 2,
            "paused" => 1,
            _ => 0
        };
    }

    private static bool IsDashboardState(string? actualState, string expectedState)
    {
        if (string.Equals(expectedState, "ok", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(actualState, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(actualState, expectedState, StringComparison.OrdinalIgnoreCase);
    }

    private static SensorState ToSensorState(MonitoringSeverity severity)
    {
        return severity switch
        {
            MonitoringSeverity.Ok => SensorState.Healthy,
            MonitoringSeverity.Warning => SensorState.Warning,
            MonitoringSeverity.Error => SensorState.Critical,
            _ => SensorState.Critical
        };
    }

    private static string BuildSensorDescription(SensorElement sensor, MonitoringSettings settings, IReadOnlyList<MonitoringElement> lineage)
    {
        return $"{sensor.SensorTypeKey} -> {SensorTargetResolver.Resolve(sensor, lineage)} | {settings.Summary()}";
    }

    private static string GetSensorUnit(string sensorTypeKey)
    {
        return sensorTypeKey.ToLowerInvariant() switch
        {
            "ping" => "ms",
            "http" => "ms",
            "snmp" => string.Empty,
            "probe-heartbeat" => "s",
            "powershell" => string.Empty,
            "ssl-certificate" => "d",
            "tcp-port" => "ms",
            "mssql" => string.Empty,
            _ => "value"
        };
    }

    private static double GetHeartbeatAgeSeconds(
        DateTimeOffset pointTimestamp,
        DateTimeOffset lastSeenUtc,
        int heartbeatWindowSeconds)
    {
        var interval = Math.Max(heartbeatWindowSeconds, 1);
        var elapsed = (pointTimestamp - lastSeenUtc).TotalSeconds;
        var normalized = elapsed % interval;

        if (normalized < 0)
        {
            normalized += interval;
        }

        return normalized;
    }

    private static string BuildDetails(MonitoringElement element, IReadOnlyList<MonitoringElement> lineage)
    {
        return element switch
        {
            ProbeElement probe => probe.Description ?? "probe root",
            FolderElement folder => folder.Description ?? "folder",
            HostElement host => string.IsNullOrWhiteSpace(host.Address) ? "host" : host.Address,
            SensorElement sensor => $"{sensor.SensorTypeKey} -> {FormatSensorTarget(sensor, lineage)}",
            _ => string.Empty
        };
    }

    private static string FormatSensorTarget(SensorElement sensor, IReadOnlyList<MonitoringElement> lineage)
    {
        var target = SensorTargetResolver.Resolve(sensor, lineage);
        if (string.IsNullOrWhiteSpace(target))
        {
            return "(no target)";
        }

        return string.IsNullOrWhiteSpace(sensor.Target) ? $"{target} (inherited)" : target;
    }

    private static IEnumerable<MonitoringElement> EnumerateElements(MonitoringElement element)
    {
        yield return element;

        if (element is not MonitoringContainerElement container)
        {
            yield break;
        }

        foreach (var child in container.Children)
        {
            foreach (var nested in EnumerateElements(child))
            {
                yield return nested;
            }
        }
    }

    private sealed record SensorDisplaySnapshot(
        SensorState State,
        string StateKey,
        string StateLabel,
        string StateColor,
        string? StateMessage,
        double? CurrentValue);

    private sealed record ProbeHealthSnapshot(
        MonitoringSeverity Severity,
        string LastSeen,
        string Message);

    private sealed class NodeCounters
    {
        public int Probes { get; set; }

        public int Hosts { get; set; }

        public int Sensors { get; set; }
    }
}
