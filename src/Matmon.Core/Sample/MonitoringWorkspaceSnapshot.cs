using Matmon.Core.Domain;

namespace Matmon.Core.Sample;

public sealed record MonitoringWorkspaceSnapshot(
    ProbeElement RootProbe,
    IReadOnlyList<MonitoringTemplate> Templates,
    IReadOnlyList<SensorDefinition> SensorDefinitions,
    NotificationWorkspaceConfiguration NotificationConfiguration,
    IReadOnlyList<NotificationSender> NotificationSenders,
    IReadOnlyList<NotificationReceiver> NotificationReceivers,
    IReadOnlyList<NotificationRule> NotificationRules,
    IReadOnlyList<MonitoringAlert> Alerts);
