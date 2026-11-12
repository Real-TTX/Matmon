namespace Matmon.Core.Domain;

public interface IProbeHeartbeatLookup
{
    bool TryGetLastHeartbeat(string probeId, out DateTimeOffset lastSeenUtc, out string probeName);

    /// <summary>When the probe / agent last said it is going away (shutdown, sleep, the service stopping) - cleared by its
    /// next heartbeat. False when it said nothing.</summary>
    bool TryGetGoingOffline(string probeId, out DateTimeOffset atUtc)
    {
        atUtc = default;
        return false;
    }
}
