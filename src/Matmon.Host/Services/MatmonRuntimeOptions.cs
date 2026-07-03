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

    /// <summary>
    /// How many due sensors the primary polls concurrently per cycle. After downtime or resuming a
    /// paused sensor/folder the overdue sensors are caught up in parallel (most-overdue first),
    /// instead of one slow sensor blocking the rest. Set via <c>Matmon__PollingWorkers</c>; clamped
    /// to 1..256. Default 8 (polling is I/O-bound, so a handful of workers helps even on small hosts).
    /// </summary>
    public int PollingWorkers { get; set; } = 8;

    public string? ProbeToken { get; set; }

    public string WorkspacePath { get; set; } = "data/workspace.json";

    public string? TelemetryPath { get; set; }

    public string? BackupPath { get; set; }

    public string? DataProtectionPath { get; set; }

    public bool SeedSampleData { get; set; }

    public bool ProvisionLocalDockerProbe { get; set; }

    public bool ProvisionDemoSensors { get; set; }

    public bool AutoCreateProbeSystemSensors { get; set; }

    public bool CreateStarterMap { get; set; }

    /// <summary>
    /// When set (e.g. via <c>Matmon__UnifiCloudApiKey</c>), the primary auto-provisions a
    /// "UniFi Cloud" health sensor on startup using this Site Manager API key. The key is
    /// only read from configuration/env — never committed.
    /// </summary>
    public string? UnifiCloudApiKey { get; set; }
}
