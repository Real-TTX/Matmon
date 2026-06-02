using Matmon.Core;

namespace Matmon.Host.Services;

public sealed class MatmonRuntimeOptions
{
    public AppMode Mode { get; set; } = AppMode.Primary;

    public string? ProbeId { get; set; }

    public string? ProbeName { get; set; }

    public string? PrimaryUrl { get; set; }

    [Obsolete("Use PrimaryUrl instead.")]
    public string? MasterUrl
    {
        get => PrimaryUrl;
        set => PrimaryUrl = value;
    }

    public int HeartbeatIntervalSeconds { get; set; } = 30;

    public string? ProbeToken { get; set; }

    public string WorkspacePath { get; set; } = "data/workspace.json";

    public string? BackupPath { get; set; }

    public string? DataProtectionPath { get; set; }

    public bool SeedSampleData { get; set; }

    public bool ProvisionLocalDockerProbe { get; set; }

    public bool ProvisionDemoSensors { get; set; }

    public bool AutoCreateProbeSystemSensors { get; set; }

    public bool CreateStarterMap { get; set; }
}
