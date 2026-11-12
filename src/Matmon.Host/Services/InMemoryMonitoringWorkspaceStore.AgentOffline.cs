namespace Matmon.Host.Services;

/// <summary>Agent hosts that may go offline (a notebook): the option lives on the host's agent identity
/// (<see cref="AgentIdentity.MayGoOffline"/>) and the agent's connection sensor asks it here (<see cref="AgentOffline"/>).</summary>
public sealed partial class InMemoryMonitoringWorkspaceStore : IAgentOfflinePolicy
{
    public bool TryGet(string probeId, out int warnDays)
    {
        warnDays = 0;
        if (string.IsNullOrWhiteSpace(probeId))
        {
            return false;
        }

        lock (_gate)
        {
            if (FindAgentHostLocked(probeId) is { Agent: { MayGoOffline: true } agent })
            {
                warnDays = Math.Max(0, agent.OfflineWarnDays);
                return true;
            }
        }

        return false;
    }
}
