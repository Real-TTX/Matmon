namespace Matmon.Host.Services;

public interface IProbeRegistry
{
    ProbeStatusSnapshot Record(ProbeHeartbeatRequest request, DateTimeOffset receivedAtUtc, string? relayProbeId = null);

    IReadOnlyList<ProbeStatusSnapshot> GetAll();
}
