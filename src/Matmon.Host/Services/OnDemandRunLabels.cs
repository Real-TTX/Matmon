using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>How a run's place reads in a message: "agent 'PC-01'" for a machine with a Matmon agent, else "probe 'Branch'" -
/// to the user an agent is a host, never a probe.</summary>
public static class OnDemandRunLabels
{
    public static string For(IMonitoringWorkspaceStore store, string runnerId) =>
        store.FindRunnerByProbeId(runnerId) switch
        {
            HostElement { Agent: not null } host => $"agent '{host.Name}'",
            { } runner => $"probe '{runner.Name}'",
            _ => $"probe '{runnerId}'"
        };
}
