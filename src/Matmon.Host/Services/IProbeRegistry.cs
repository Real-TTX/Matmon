namespace Matmon.Host.Services;

public interface IProbeRegistry
{
    ProbeStatusSnapshot Record(ProbeHeartbeatRequest request, DateTimeOffset receivedAtUtc, string? relayProbeId = null);

    /// <summary>The probe / agent said it is going away (shutdown, sleep, its service stopping); its next heartbeat clears it.</summary>
    void MarkGoingOffline(string probeId, DateTimeOffset atUtc);

    IReadOnlyList<ProbeStatusSnapshot> GetAll();
}
