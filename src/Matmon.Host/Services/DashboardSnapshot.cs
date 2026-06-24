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
    int ErrorAlertCount,
    int WarningAlertCount,
    int PausedSensorCount,
    int HealthySensorCount,
    int WarningSensorCount,
    int AcknowledgedSensorCount,
    int ErrorSensorCount,
    int OtherSensorCount,
    IReadOnlyList<DashboardNodeViewModel> Nodes,
    IReadOnlyList<ProbeStatusSnapshot> ConnectedProbes,
    IReadOnlyList<DashboardProbeViewModel> Probes,
    IReadOnlyList<SensorDefinition> SensorDefinitions,
    IReadOnlyList<TelemetrySeriesSnapshot> TelemetrySeries,
    IReadOnlyList<TelemetrySeriesSnapshot> HighlightedTelemetrySeries);
