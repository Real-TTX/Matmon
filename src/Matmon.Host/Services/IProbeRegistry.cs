namespace Matmon.Host.Services;

public interface IProbeRegistry
{
    ProbeStatusSnapshot Record(ProbeHeartbeatRequest request, DateTimeOffset receivedAtUtc);

    IReadOnlyList<ProbeStatusSnapshot> GetAll();
}
