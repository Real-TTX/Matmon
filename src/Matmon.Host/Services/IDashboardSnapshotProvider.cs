namespace Matmon.Host.Services;

public interface IDashboardSnapshotProvider
{
    DashboardSnapshot CreateSnapshot();

    /// <summary>
    /// Cheap topology-only counts for the layout's workspace summary strip.
    /// Unlike <see cref="CreateSnapshot"/> it does no telemetry queries, builds
    /// no series and does not sync alerts, so it is safe to call on every page.
    /// </summary>
    WorkspaceSummary GetWorkspaceSummary();
}

public sealed record WorkspaceSummary(string WorkspaceName, int ConfiguredProbeCount, int SensorCount);
