using System.Collections.Concurrent;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class InMemoryProbeRegistry : IProbeRegistry, IProbeHeartbeatLookup
{
    private readonly ConcurrentDictionary<string, ProbeStatusSnapshot> _probes = new(StringComparer.OrdinalIgnoreCase);

    public ProbeStatusSnapshot Record(ProbeHeartbeatRequest request, DateTimeOffset receivedAtUtc)
    {
        var snapshot = new ProbeStatusSnapshot(
            request.ProbeId,
            request.ProbeName,
            receivedAtUtc,
            "Online",
            request.Message,
            request.OperatingSystem,
            request.Host,
            request.Networks);

        _probes[request.ProbeId] = snapshot;
        return snapshot;
    }

    public IReadOnlyList<ProbeStatusSnapshot> GetAll()
    {
        return _probes.Values
            .OrderByDescending(probe => probe.LastSeenUtc)
            .ThenBy(probe => probe.ProbeName)
            .ToArray();
    }

    public bool TryGetLastHeartbeat(string probeId, out DateTimeOffset lastSeenUtc, out string probeName)
    {
        if (_probes.TryGetValue(probeId, out var snapshot))
        {
            lastSeenUtc = snapshot.LastSeenUtc;
            probeName = snapshot.ProbeName;
            return true;
        }

        lastSeenUtc = default;
        probeName = string.Empty;
        return false;
    }
}
