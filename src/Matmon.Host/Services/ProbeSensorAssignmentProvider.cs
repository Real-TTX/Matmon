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
        var probe = _workspaceStore.FindProbeByProbeId(probeId)
            ?? throw new InvalidOperationException($"Probe '{probeId}' was not found.");
        var snapshot = _workspaceStore.Workspace;
        var elementsById = _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
        var templateMap = snapshot.Templates.ToDictionary(template => template.Id);
        var definitionMap = snapshot.SensorDefinitions.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);
        var latestBySensorId = _workspaceStore.GetLatestSensorObservations();

        var sensors = EnumerateDescendants(probe)
            .OfType<SensorElement>()
            .Where(sensor => !string.Equals(sensor.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
            .Select(sensor =>
            {
                var lineage = BuildLineage(sensor, elementsById);
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
                    latestObservation?.TimestampUtc);
            })
            .ToArray();

        return new ProbeSensorAssignmentsResponse(
            probe.ProbeId,
            probe.Name,
            DateTimeOffset.UtcNow,
            sensors);
    }

    public bool TryBuildRecordingContext(
        string probeId,
        Guid sensorId,
        out ProbeElement probe,
        out SensorElement sensor,
        out MonitoringSettings settings)
    {
        probe = null!;
        sensor = null!;
        settings = null!;

        var foundProbe = _workspaceStore.FindProbeByProbeId(probeId);
        if (foundProbe is null)
        {
            return false;
        }

        var foundSensor = EnumerateDescendants(foundProbe)
            .OfType<SensorElement>()
            .FirstOrDefault(candidate => candidate.Id == sensorId);
        if (foundSensor is null ||
            string.Equals(foundSensor.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var snapshot = _workspaceStore.Workspace;
        var elementsById = _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
        var templateMap = snapshot.Templates.ToDictionary(template => template.Id);
        var lineage = BuildLineage(foundSensor, elementsById);

        probe = foundProbe;
        sensor = foundSensor;
        settings = _resolver.Resolve(lineage, templateMap);
        return true;
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

    private static IReadOnlyList<MonitoringElement> BuildLineage(
        MonitoringElement element,
        IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        var lineage = new List<MonitoringElement>();
        var current = element;

        while (true)
        {
            lineage.Add(current);

            if (current.ParentId is not Guid parentId || !elementsById.TryGetValue(parentId, out var parent))
            {
                break;
            }

            current = parent;
        }

        lineage.Reverse();
        return lineage;
    }

    private static IEnumerable<MonitoringElement> EnumerateDescendants(MonitoringContainerElement parent)
    {
        foreach (var child in parent.Children)
        {
            yield return child;

            if (child is MonitoringContainerElement container)
            {
                foreach (var descendant in EnumerateDescendants(container))
                {
                    yield return descendant;
                }
            }
        }
    }
}
