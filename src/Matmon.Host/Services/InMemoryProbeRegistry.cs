using System.Collections.Concurrent;

namespace Matmon.Host.Services;

public sealed class InMemoryProbeRegistry : IProbeRegistry, IProbeHeartbeatLookup
{
    private readonly ConcurrentDictionary<string, ProbeStatusSnapshot> _probes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DuplicateWatch> _duplicateWatch = new(StringComparer.OrdinalIgnoreCase);
    private readonly MatmonRuntimeOptions _runtimeOptions;

    public InMemoryProbeRegistry(MatmonRuntimeOptions runtimeOptions) => _runtimeOptions = runtimeOptions;

    public ProbeStatusSnapshot Record(ProbeHeartbeatRequest request, DateTimeOffset receivedAtUtc)
    {
        var duplicateWarning = DetectDuplicate(request, receivedAtUtc);

        var snapshot = new ProbeStatusSnapshot(
            request.ProbeId,
            request.ProbeName,
            receivedAtUtc,
            "Online",
            request.Message,
            request.OperatingSystem,
            request.Host,
            request.Networks,
            request.AgentVersion,
            duplicateWarning,
            request.UpdateStatus);

        _probes[request.ProbeId] = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Two probe processes sharing one ProbeId (the classic "old container was never removed on update") is
    /// invisible in normal operation but poisonous: both pull the same assignments, so every other observation
    /// comes from the other build - sensors flip between healthy and error for no apparent reason. Catch it here,
    /// where every heartbeat converges, and surface it on the Probes page instead of letting it look like a
    /// flapping sensor. Two independent signals, either is enough:
    /// (a) the reported identity (host or build version) ALTERNATES - A, B, A within a couple of intervals.
    ///     A single change is an upgrade (a new container, an agent auto-update), not a duplicate: the new
    ///     process beats right after starting, so "changed AND fast" fired on a large share of ordinary
    ///     updates. It takes the third beat to tell the two apart, which costs one interval of detection;
    /// (b) the SAME identity beats far faster than the configured interval - more senders than one.
    /// The warning is sticky for a few intervals so it survives the beat that looks normal in between.
    /// </summary>
    private string? DetectDuplicate(ProbeHeartbeatRequest request, DateTimeOffset receivedAtUtc)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _runtimeOptions.HeartbeatIntervalSeconds));
        var identity = $"{request.Host}|{request.AgentVersion}";

        var watch = _duplicateWatch.AddOrUpdate(
            request.ProbeId,
            _ => new DuplicateWatch(identity, null, receivedAtUtc, DateTimeOffset.MinValue, null, DateTimeOffset.MinValue),
            (_, previous) =>
            {
                var gap = receivedAtUtc - previous.LastSeenUtc;
                string? reason = null;

                var changed = !string.Equals(previous.Identity, identity, StringComparison.OrdinalIgnoreCase);

                if (changed &&
                    string.Equals(previous.PreviousIdentity, identity, StringComparison.OrdinalIgnoreCase) &&
                    receivedAtUtc - previous.ChangedUtc < interval * 2)
                {
                    reason = $"two processes report as this probe ({Describe(previous.Identity)} and {Describe(identity)}) - remove the stale container";
                }
                else if (!changed && gap > TimeSpan.Zero && gap < interval * 0.4)
                {
                    reason = "heartbeats arrive faster than the configured interval - more than one process uses this probe id";
                }

                var next = changed
                    ? previous with { Identity = identity, PreviousIdentity = previous.Identity, ChangedUtc = receivedAtUtc, LastSeenUtc = receivedAtUtc }
                    : previous with { LastSeenUtc = receivedAtUtc };
                return reason is not null ? next with { Warning = reason, WarningUtc = receivedAtUtc } : next;
            });

        // Keep an existing warning visible for a few intervals - the duplicate only shows on the beats it "wins".
        return receivedAtUtc - watch.WarningUtc < interval * 6 ? watch.Warning : null;
    }

    private static string Describe(string identity)
    {
        var parts = identity.Split('|', 2);
        var host = string.IsNullOrWhiteSpace(parts[0]) ? "unknown host" : parts[0];
        var version = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "unknown version";
        return $"{host} / {version}";
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

    private sealed record DuplicateWatch(
        string Identity,
        string? PreviousIdentity,
        DateTimeOffset LastSeenUtc,
        DateTimeOffset ChangedUtc,
        string? Warning,
        DateTimeOffset WarningUtc);
}
