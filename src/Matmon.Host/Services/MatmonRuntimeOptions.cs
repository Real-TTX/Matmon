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
    /// How often the Primary heartbeats to Matmon.Cloud (dead-man-switch + aggregate metadata). Independent
    /// of the probe <see cref="HeartbeatIntervalSeconds"/> - once a minute is plenty for a check-in, and it
    /// keeps tunnel/cloud load low. Set via <c>Matmon__CloudHeartbeatIntervalSeconds</c>; floored at 15s.
    /// </summary>
    public int CloudHeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How many due sensors the primary polls concurrently per cycle. After downtime or resuming a
    /// paused sensor/folder the overdue sensors are caught up in parallel (most-overdue first),
    /// instead of one slow sensor blocking the rest. Set via <c>Matmon__PollingWorkers</c>; clamped
    /// to 1..256. Default 8 (polling is I/O-bound, so a handful of workers helps even on small hosts).
    /// </summary>
    public int PollingWorkers { get; set; } = 8;

    public string? ProbeToken { get; set; }

    /// <summary>Shared secret that authenticates callers of the Executor run-mode's <c>/api/execute</c> and
    /// <c>/api/sensor-catalog</c> (<c>Matmon__ExecutorToken</c>). Only relevant when <see cref="Mode"/> is
    /// <see cref="AppMode.Executor"/>; empty = the endpoints reject everything (locked).</summary>
    public string? ExecutorToken { get; set; }

    public string WorkspacePath { get; set; } = "data/workspace.json";

    public string? TelemetryPath { get; set; }

    public string? BackupPath { get; set; }

    public string? DataProtectionPath { get; set; }

    public bool SeedSampleData { get; set; }

    public bool ProvisionLocalDockerProbe { get; set; }

    public bool ProvisionDemoSensors { get; set; }

    public bool AutoCreateProbeSystemSensors { get; set; }

    public bool CreateStarterMap { get; set; }

    /// <summary>Dev/preview only (<c>Matmon__DemoServicePartner</c>): seed a dummy managing service partner
    /// (logo + accent + contact) so co-branding is visible on this instance - login/sidebar/app-accent/reports -
    /// without a real Matmon.Cloud link. Mutually exclusive with a real cloud link (short-circuits the cloud loop).</summary>
    public bool DemoServicePartner { get; set; }

    // Optional overrides for the demo partner (so a real third-party brand can be previewed LOCALLY without
    // committing its logo/name into the repo/images). All fall back to a generic built-in placeholder.
    public string? DemoServicePartnerName { get; set; }
    public string? DemoServicePartnerProductName { get; set; }
    public string? DemoServicePartnerColor { get; set; }
    public string? DemoServicePartnerContactUrl { get; set; }
    /// <summary>Path (absolute or relative to the content root) to a PNG/JPEG logo file for the demo partner.</summary>
    public string? DemoServicePartnerLogoPath { get; set; }
    /// <summary>Demo: treat the logo as a complete OEM lockup (show it alone, no product-name text beneath).</summary>
    public bool DemoServicePartnerOemLogo { get; set; }
    public string? DemoServicePartnerSlogan { get; set; }
    public string? DemoServicePartnerSecondaryColor { get; set; }
    /// <summary>Path to a small square PNG/JPEG for the demo favicon + mobile header.</summary>
    public string? DemoServicePartnerSmallLogoPath { get; set; }
    /// <summary>Demo sidebar style: 0 = logo top / name below, 1 = logo left / name right.</summary>
    public int DemoServicePartnerSidebarStyle { get; set; }

    /// <summary>
    /// When set (e.g. via <c>Matmon__UnifiCloudApiKey</c>), the primary auto-provisions a
    /// "UniFi Cloud" health sensor on startup using this Site Manager API key. The key is
    /// only read from configuration/env - never committed.
    /// </summary>
    public string? UnifiCloudApiKey { get; set; }

    /// <summary>
    /// Base URL of the Matmon.Cloud control plane (e.g. <c>Matmon__CloudUrl=http://localhost:8055</c>).
    /// When set together with <see cref="CloudInstanceId"/> + <see cref="CloudInstanceToken"/>, a Primary
    /// sends heartbeats + metadata to the cloud (dead-man-switch + public dashboard). Create the instance
    /// in the Matmon.Cloud UI to obtain the id + token. Empty = fully offline, no cloud connection.
    /// </summary>
    public string? CloudUrl { get; set; }

    /// <summary>The instance id issued by Matmon.Cloud (<c>Matmon__CloudInstanceId</c>).</summary>
    public string? CloudInstanceId { get; set; }

    /// <summary>The secret instance token issued by Matmon.Cloud (<c>Matmon__CloudInstanceToken</c>).</summary>
    public string? CloudInstanceToken { get; set; }
}
