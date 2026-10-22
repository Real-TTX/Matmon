using Matmon.Core;

namespace Matmon.Probe;

/// <summary>
/// The settings a probe needs to work for a primary - and ONLY those. They used to be eight of the thirty-odd
/// properties on the Host's <c>MatmonRuntimeOptions</c>, next to workspace paths, demo-partner seeds and the
/// cloud bootstrap; a probe library cannot depend on that bag without shipping it to every agent.
///
/// <c>MatmonRuntimeOptions</c> now DERIVES from this class rather than duplicating the fields, so the Host
/// still has one options object bound from one <c>Matmon</c> config section (every <c>runtimeOptions.Mode</c>
/// call site is untouched) and there is exactly one declaration of each setting.
/// </summary>
public class ProbeRuntimeOptions
{
    public AppMode Mode { get; set; } = AppMode.Primary;

    public string? ProbeId { get; set; }

    public string? ProbeName { get; set; }

    public string? PrimaryUrl { get; set; }

    public int HeartbeatIntervalSeconds { get; set; } = 30;

    public string? ProbeToken { get; set; }

    /// <summary>
    /// Secondary store-and-forward: when the primary is unreachable, the probe keeps executing its
    /// <b>cached</b> assignments on schedule and buffers the observations, then flushes them (oldest first,
    /// with their original timestamps) once the link returns. This caps how far back the buffer is kept -
    /// observations older than this are dropped. Default 7 days; <b>0 disables buffering</b> (unsent results
    /// are dropped immediately, the pre-buffer behaviour). Set via <c>Matmon__OfflineBufferRetentionDays</c>.
    /// The buffer is kept on disk too (<see cref="OfflineBufferPath"/>), so it also survives a restart of the probe.
    /// </summary>
    public int OfflineBufferRetentionDays { get; set; } = 7;

    /// <summary>The file the offline buffer is kept in (JSON lines). The Docker probe defaults to its data directory, the
    /// agent to its state directory; set via <c>Matmon__OfflineBufferPath</c>. Empty = memory only (lost on a restart).</summary>
    public string? OfflineBufferPath { get; set; }

    /// <summary>Hard cap on buffered observations (RAM safety for a long outage); the oldest are dropped past it.
    /// Set via <c>Matmon__OfflineBufferMaxObservations</c>. Default 100000; ≤0 = no count cap (retention only).</summary>
    public int OfflineBufferMaxObservations { get; set; } = 100_000;
}
