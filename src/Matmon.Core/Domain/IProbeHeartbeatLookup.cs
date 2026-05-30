namespace Matmon.Core.Domain;

public interface IProbeHeartbeatLookup
{
    bool TryGetLastHeartbeat(string probeId, out DateTimeOffset lastSeenUtc, out string probeName);
}
