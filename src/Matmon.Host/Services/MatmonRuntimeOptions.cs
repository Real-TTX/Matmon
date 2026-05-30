using Matmon.Core;

namespace Matmon.Host.Services;

public sealed class MatmonRuntimeOptions
{
    public AppMode Mode { get; set; } = AppMode.Master;

    public string? ProbeId { get; set; }

    public string? ProbeName { get; set; }

    public string? MasterUrl { get; set; }

    public int HeartbeatIntervalSeconds { get; set; } = 30;

    public string? ProbeToken { get; set; }

    public string WorkspacePath { get; set; } = "data/workspace.json";
}
