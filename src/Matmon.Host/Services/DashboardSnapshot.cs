using Matmon.Core;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed record DashboardSnapshot(
    AppMode Mode,
    string WorkspaceName,
    DateTimeOffset GeneratedAtUtc,
    int ConfiguredProbeCount,
    int HostCount,
    int SensorCount,
    int TemplateCount,
    int NotificationRuleCount,
    int ActiveAlertCount,
    int AcknowledgedAlertCount,
    int PausedSensorCount,
    int WarningSensorCount,
    int ErrorSensorCount,
    IReadOnlyList<DashboardNodeViewModel> Nodes,
    IReadOnlyList<ProbeStatusSnapshot> ConnectedProbes,
    IReadOnlyList<DashboardProbeViewModel> Probes,
    IReadOnlyList<SensorDefinition> SensorDefinitions,
    IReadOnlyList<TelemetrySeriesSnapshot> TelemetrySeries,
    IReadOnlyList<TelemetrySeriesSnapshot> HighlightedTelemetrySeries);
