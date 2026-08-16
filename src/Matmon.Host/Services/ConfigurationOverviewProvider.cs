using Matmon.Core;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class ConfigurationOverviewProvider : IConfigurationOverviewProvider
{
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly MatmonAuthOptions _authOptions;
    private readonly StorageOverviewProvider _storageOverviewProvider;
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly IProbeRegistry _probeRegistry;
    private readonly SlaveProbeRuntimeState _slaveRuntimeState;

    public ConfigurationOverviewProvider(
        MatmonRuntimeOptions runtimeOptions,
        MatmonAuthOptions authOptions,
        StorageOverviewProvider storageOverviewProvider,
        IMonitoringWorkspaceStore workspaceStore,
        IProbeRegistry probeRegistry,
        SlaveProbeRuntimeState slaveRuntimeState)
    {
        _runtimeOptions = runtimeOptions;
        _authOptions = authOptions;
        _storageOverviewProvider = storageOverviewProvider;
        _workspaceStore = workspaceStore;
        _probeRegistry = probeRegistry;
        _slaveRuntimeState = slaveRuntimeState;
    }

    public ConfigurationOverview GetOverview()
    {
        var storage = _storageOverviewProvider.GetOverview();
        var slaveRuntime = _slaveRuntimeState.Snapshot();

        return new ConfigurationOverview(
            _runtimeOptions.Mode.ToString(),
            _runtimeOptions.ProbeId,
            _runtimeOptions.ProbeName,
            _runtimeOptions.PrimaryUrl,
            _runtimeOptions.HeartbeatIntervalSeconds,
            _runtimeOptions.PollingWorkers,
            _runtimeOptions.WorkspacePath,
            _authOptions.Username,
            _authOptions.Password,
            BuildMasterSnippet(),
            BuildSlaveSnippet(),
            BuildAppSettingsSnippet(),
            storage,
            BuildProbeOverview(),
            slaveRuntime);
    }

    private IReadOnlyList<SystemProbeOverview> BuildProbeOverview()
    {
        var liveProbeMap = _probeRegistry.GetAll()
            .ToDictionary(probe => probe.ProbeId, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var heartbeatGrace = TimeSpan.FromSeconds(Math.Max(_runtimeOptions.HeartbeatIntervalSeconds * 2, 45));

        return _workspaceStore.GetAllElements()
            .OfType<ProbeElement>()
            .OrderBy(probe => probe.ParentId.HasValue ? 1 : 0)
            .ThenBy(probe => probe.Name, StringComparer.OrdinalIgnoreCase)
            .Select(probe =>
            {
                var isRoot = !probe.ParentId.HasValue;
                liveProbeMap.TryGetValue(probe.ProbeId, out var liveProbe);
                var state = ResolveProbeState(isRoot, liveProbe, now, heartbeatGrace);
                var role = isRoot ? "Primary" : "Secondary";
                var message = isRoot
                    ? "local primary probe"
                    : liveProbe?.Message ?? "waiting for probe heartbeat";

                // System "full sync": the local primary reports itself; a secondary reports OS /
                // host / subnets through its heartbeat snapshot.
                var system = isRoot
                    ? ProbeSystemInfoProvider.Collect()
                    : liveProbe is not null
                        ? new ProbeSystemInfo(
                            string.IsNullOrWhiteSpace(liveProbe.OperatingSystem) ? "-" : liveProbe.OperatingSystem!,
                            string.IsNullOrWhiteSpace(liveProbe.Host) ? "-" : liveProbe.Host!,
                            liveProbe.Networks ?? [])
                        : new ProbeSystemInfo("-", "-", []);

                // Admin-configured subnets first (what you want this probe to scan), then the
                // auto-detected interfaces it reported.
                var networks = probe.Subnets
                    .Concat(system.Networks)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new SystemProbeOverview(
                    probe.Id,
                    string.IsNullOrWhiteSpace(probe.ProbeId) ? "-" : probe.ProbeId,
                    probe.Name,
                    role,
                    state,
                    message,
                    isRoot ? null : liveProbe?.LastSeenUtc,
                    isRoot ? null : probe.EnrollmentToken,
                    CountSensors(probe),
                    system.OperatingSystem,
                    system.Host,
                    networks);
            })
            .ToArray();
    }

    private static string ResolveProbeState(
        bool isRoot,
        ProbeStatusSnapshot? liveProbe,
        DateTimeOffset now,
        TimeSpan heartbeatGrace)
    {
        if (isRoot)
        {
            return "Local";
        }

        if (liveProbe is null)
        {
            return "Waiting";
        }

        return now - liveProbe.LastSeenUtc <= heartbeatGrace
            ? "Online"
            : "Offline";
    }

    private static int CountSensors(MonitoringContainerElement element)
    {
        var count = 0;
        foreach (var child in element.Children)
        {
            if (child is SensorElement)
            {
                count++;
            }

            if (child is MonitoringContainerElement container)
            {
                count += CountSensors(container);
            }
        }

        return count;
    }

    private static string BuildMasterSnippet()
    {
        return """
Matmon__Mode=Primary
Matmon__HeartbeatIntervalSeconds=30
Matmon__WorkspacePath=data/workspace.json
Matmon__Auth__Username=admin
Matmon__Auth__Password=admin
""";
    }

    private static string BuildSlaveSnippet()
    {
        return """
Matmon__Mode=Secondary
Matmon__ProbeId=probe-01
Matmon__ProbeName=Secondary Probe 01
Matmon__PrimaryUrl=http://primary:8099
Matmon__ProbeToken=probe-01-token
Matmon__HeartbeatIntervalSeconds=30
Matmon__WorkspacePath=data/workspace.json
""";
    }

    private static string BuildAppSettingsSnippet()
    {
        return """
"Matmon": {
  "Mode": "Primary",
  "HeartbeatIntervalSeconds": 30,
  "WorkspacePath": "data/workspace.json",
  "Auth": {
    "Username": "admin",
    "Password": "admin"
  }
}
""";
    }
}
