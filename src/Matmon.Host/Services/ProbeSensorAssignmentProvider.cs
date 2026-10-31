using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class ProbeSensorAssignmentProvider
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly MonitoringInheritanceResolver _resolver = new();

    public ProbeSensorAssignmentProvider(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    public ProbeSensorAssignmentsResponse BuildAssignments(string probeId)
    {
        var probe = _workspaceStore.FindRunnerByProbeId(probeId)
            ?? throw new InvalidOperationException($"Probe '{probeId}' was not found.");
        var snapshot = _workspaceStore.Workspace;
        var elementsById = _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
        var templateMap = snapshot.Templates.ToDictionary(template => template.Id);
        var definitionMap = snapshot.SensorDefinitions.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);
        var latestBySensorId = _workspaceStore.GetLatestSensorObservations();

        var sensors = MonitoringTopology.EnumerateOwnedDescendants(probe)
            .OfType<SensorElement>()
            .Where(sensor => !SensorPlacement.RunsOnPrimaryOnly(sensor.SensorTypeKey))
            .Select(sensor =>
            {
                var lineage = MonitoringTopology.BuildLineage(sensor, elementsById);
                var settings = _resolver.Resolve(lineage, templateMap);
                ApplySensorCredentialDefaults(settings, sensor.SensorTypeKey, definitionMap);
                settings.Credentials.Clear();
                settings.SelectedCredentialId = null;

                latestBySensorId.TryGetValue(sensor.Id, out var latestObservation);
                return new ProbeSensorAssignment(
                    sensor.Id,
                    sensor.Name,
                    string.Join(" / ", lineage.Select(element => element.Name)),
                    sensor.SensorTypeKey,
                    SensorTargetResolver.Resolve(sensor, lineage),
                    sensor.IsPaused,
                    settings,
                    latestObservation?.TimestampUtc,
                    latestObservation);
            })
            .ToArray();

        return new ProbeSensorAssignmentsResponse(
            MonitoringTopology.RunnerId(probe),
            probe.Name,
            DateTimeOffset.UtcNow,
            sensors,
            _workspaceStore.GetAllowedProgramPaths());
    }

    public bool TryBuildRecordingContext(
        string probeId,
        Guid sensorId,
        out ProbeRunnerRef probe,
        out SensorElement sensor,
        out MonitoringSettings settings)
    {
        probe = null!;
        sensor = null!;
        settings = null!;

        var foundProbe = _workspaceStore.FindRunnerByProbeId(probeId);
        if (foundProbe is null)
        {
            return false;
        }

        var foundSensor = MonitoringTopology.EnumerateOwnedDescendants(foundProbe)
            .OfType<SensorElement>()
            .FirstOrDefault(candidate => candidate.Id == sensorId);
        if (foundSensor is null || SensorPlacement.RunsOnPrimaryOnly(foundSensor.SensorTypeKey))
        {
            return false;
        }

        var snapshot = _workspaceStore.Workspace;
        var elementsById = _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
        var templateMap = snapshot.Templates.ToDictionary(template => template.Id);
        var lineage = MonitoringTopology.BuildLineage(foundSensor, elementsById);

        probe = new ProbeRunnerRef(foundProbe.Id, MonitoringTopology.RunnerId(foundProbe), foundProbe.Name);
        sensor = foundSensor;
        settings = _resolver.Resolve(lineage, templateMap);
        return true;
    }

    /// <summary>
    /// Resolves a saved sensor into a probe-ready run: its effective (inherited) settings and target,
    /// with credential VALUES inlined and the bundles stripped, so the owning probe can run it standalone
    /// - exactly the shape <see cref="BuildAssignments"/> produces. <paramref name="owningProbeId"/> is the
    /// remote probe's id when the sensor lives under one, or null for the local primary root. Returns false
    /// when the id is not a sensor.
    /// </summary>
    public bool TryBuildProbeReadyRun(
        Guid sensorId,
        out string? owningProbeId,
        out string sensorTypeKey,
        out string target,
        out MonitoringSettings settings)
    {
        owningProbeId = null;
        sensorTypeKey = string.Empty;
        target = string.Empty;
        settings = null!;

        var elementsById = _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
        if (!elementsById.TryGetValue(sensorId, out var element) || element is not SensorElement sensor)
        {
            return false;
        }

        var snapshot = _workspaceStore.Workspace;
        var templateMap = snapshot.Templates.ToDictionary(template => template.Id);
        var definitionMap = snapshot.SensorDefinitions.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);
        var lineage = MonitoringTopology.BuildLineage(sensor, elementsById);

        sensorTypeKey = sensor.SensorTypeKey;
        settings = _resolver.Resolve(lineage, templateMap);
        ApplySensorCredentialDefaults(settings, sensorTypeKey, definitionMap);
        settings.Credentials.Clear();
        settings.SelectedCredentialId = null;
        target = SensorTargetResolver.Resolve(sensor, lineage);

        // The nearest agent host or probe in the lineage runs the sensor; the probe with no parent is the local primary
        // root (runs in-process), an agent or any other probe is remote (must run there) - except a sensor that only the
        // primary can run (SensorPlacement), which stays here whatever it sits under.
        var runner = MonitoringTopology.FindRunner(lineage);
        owningProbeId = MonitoringTopology.IsRemoteRunner(runner) && !SensorPlacement.RunsOnPrimaryOnly(sensorTypeKey)
            ? MonitoringTopology.RunnerId(runner!)
            : null;
        return true;
    }

    /// <summary>
    /// Turns already-resolved settings into the standalone shape a probe needs: inlines the credential
    /// values for the sensor type's kinds and clears the bundle references. Used by the "Test" / SNMP
    /// discover paths, which start from transient form settings rather than a saved sensor.
    /// </summary>
    public void MakeSettingsProbeReady(MonitoringSettings settings, string sensorTypeKey)
    {
        var definitionMap = _workspaceStore.Workspace.SensorDefinitions
            .ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);
        ApplySensorCredentialDefaults(settings, sensorTypeKey, definitionMap);
        settings.Credentials.Clear();
        settings.SelectedCredentialId = null;
    }

    private static void ApplySensorCredentialDefaults(
        MonitoringSettings settings,
        string sensorTypeKey,
        IReadOnlyDictionary<string, SensorDefinition> definitionMap)
    {
        if (definitionMap.TryGetValue(sensorTypeKey, out var definition))
        {
            MonitoringSettings.ApplyCredentialValuesForKinds(settings, definition.CredentialKinds);
        }
    }
}

/// <summary>Who a probe endpoint is talking to: a remote probe or an agent (its host).</summary>
public sealed record ProbeRunnerRef(Guid ElementId, string ProbeId, string Name);
