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

    public IReadOnlyList<SystemProbeOverview> GetProbes() => BuildProbeOverview();

    private IReadOnlyList<SystemProbeOverview> BuildProbeOverview()
    {
        var liveProbeMap = _probeRegistry.GetAll()
            .ToDictionary(probe => probe.ProbeId, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var heartbeatGrace = TimeSpan.FromSeconds(Math.Max(_runtimeOptions.HeartbeatIntervalSeconds * 2, 45));

        var elements = _workspaceStore.GetAllElements();
        var elementsById = elements.ToDictionary(element => element.Id);
        var probes = elements
            .OfType<ProbeElement>()
            .OrderBy(probe => probe.ParentId.HasValue ? 1 : 0)
            .ThenBy(probe => probe.Name, StringComparer.OrdinalIgnoreCase)
            .Select(probe => BuildProbeRow(probe, liveProbeMap, now, heartbeatGrace))
            .ToList();

        // An agent is a host with an identity of its own - listed beside the probes (IsAgent), never as one of them.
        var probeNames = elements.OfType<ProbeElement>()
            .Where(probe => !string.IsNullOrWhiteSpace(probe.ProbeId))
            .GroupBy(probe => probe.ProbeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
        var agents = elements
            .OfType<HostElement>()
            .Where(host => host.Agent is not null)
            .OrderBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
            .Select(host => BuildAgentRow(host, MonitoringTopology.FindProbe(MonitoringTopology.BuildLineage(host, elementsById)),
                liveProbeMap, probeNames, now, heartbeatGrace));

        return [.. probes, .. agents];
    }

    private static SystemProbeOverview BuildAgentRow(
        HostElement host,
        ProbeElement? via,
        IReadOnlyDictionary<string, ProbeStatusSnapshot> liveProbeMap,
        IReadOnlyDictionary<string, string> probeNames,
        DateTimeOffset now,
        TimeSpan heartbeatGrace)
    {
        var agent = host.Agent!;
        liveProbeMap.TryGetValue(agent.ProbeId, out var live);
        var system = ResolveSystemInfo(false, live);
        var relayedBy = live?.RelayProbeId is { Length: > 0 } relay
            ? probeNames.TryGetValue(relay, out var relayName) ? relayName : relay
            : null;
        return new SystemProbeOverview(
            host.Id,
            agent.ProbeId,
            host.Name,
            "Agent",
            ResolveProbeState(false, live, now, heartbeatGrace),
            live is null ? "waiting for the agent's first heartbeat" : live.Message ?? (live.RelayProbeId is null ? "connected" : "connected through its probe"),
            live?.LastSeenUtc,
            agent.Token,
            CountSensors(host),
            system.OperatingSystem,
            system.Host,
            system.Networks,
            live?.Version,
            live?.DuplicateWarning,
            true,
            live?.UpdateStatus,
            host.Id,
            host.Name,
            null,
            via?.Id,
            via?.Name,
            relayedBy);
    }

    private static SystemProbeOverview BuildProbeRow(
        ProbeElement probe,
        IReadOnlyDictionary<string, ProbeStatusSnapshot> liveProbeMap,
        DateTimeOffset now,
        TimeSpan heartbeatGrace)
    {
        var isRoot = !probe.ParentId.HasValue;
        liveProbeMap.TryGetValue(probe.ProbeId, out var liveProbe);
        var state = ResolveProbeState(isRoot, liveProbe, now, heartbeatGrace);
        var role = isRoot ? "Primary" : "Secondary";
        var message = isRoot
            ? "local primary probe"
            : liveProbe?.Message ?? "waiting for probe heartbeat";

        var system = ResolveSystemInfo(isRoot, liveProbe);

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
            MonitoringTopology.EnumerateOwnedDescendants(probe).OfType<SensorElement>().Count(),
            system.OperatingSystem,
            system.Host,
            networks,
            // Build version: the primary reports its own; a secondary's rides on the heartbeat. Makes an
            // out-of-date (or duplicate old) probe container visible at a glance on the Probes page.
            isRoot ? MatmonVersion.Current : liveProbe?.Version,
            isRoot ? null : liveProbe?.DuplicateWarning,
            false,
            isRoot ? null : liveProbe?.UpdateStatus,
            null,
            null,
            isRoot ? null : liveProbe?.SelfUpdate);
    }

    /// <summary>System "full sync": the local primary reports itself; a secondary reports OS / host /
    /// subnets through its heartbeat snapshot (placeholders until the first beat arrives).</summary>
    private static ProbeSystemInfo ResolveSystemInfo(bool isRoot, ProbeStatusSnapshot? liveProbe)
    {
        if (isRoot)
        {
            return ProbeSystemInfoProvider.Collect();
        }

        if (liveProbe is null)
        {
            return new ProbeSystemInfo("-", "-", []);
        }

        return new ProbeSystemInfo(
            string.IsNullOrWhiteSpace(liveProbe.OperatingSystem) ? "-" : liveProbe.OperatingSystem!,
            string.IsNullOrWhiteSpace(liveProbe.Host) ? "-" : liveProbe.Host!,
            liveProbe.Networks ?? []);
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
