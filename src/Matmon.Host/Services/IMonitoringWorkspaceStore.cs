using Matmon.Core.Domain;
using Matmon.Core.Sample;

namespace Matmon.Host.Services;

public interface IMonitoringWorkspaceStore
{
    MonitoringWorkspaceSnapshot Workspace { get; }

    IReadOnlyList<MonitoringElement> GetAllElements();

    IReadOnlyList<MonitoringTemplate> GetAllTemplates();

    ProbeElement? FindProbeByProbeId(string probeId);

    MonitoringElement? FindElement(Guid id);

    MonitoringTemplate? FindTemplate(Guid id);

    NotificationSender? FindNotificationSender(Guid id);

    NotificationReceiver? FindNotificationReceiver(Guid id);

    NotificationRule? FindNotificationRule(Guid id);

    bool AcknowledgeAlert(Guid alertId, string? acknowledgedBy = null);

    void RecordSensorObservation(
        Guid sensorId,
        SensorExecutionResult result,
        DateTimeOffset timestampUtc,
        MonitoringSettings? settings = null,
        string? executedByProbeId = null,
        string? executedByProbeName = null);

    IReadOnlyList<SensorObservation> GetSensorHistory();

    IReadOnlyList<SensorObservation> GetSensorHistory(Guid sensorId, TimeSpan? window = null, int? maxCount = null);

    IReadOnlyDictionary<Guid, SensorObservation> GetLatestSensorObservations();

    IReadOnlyDictionary<Guid, SensorObservation[]> GetRecentSensorHistoryBySensor(TimeSpan window, int maxPerSensor);

    IReadOnlyList<MonitoringEvent> GetEvents(int take = 500);

    IReadOnlyList<SensorStatisticsBucket> GetSensorStatistics(Guid sensorId);

    ProbeElement CreateProbe(Guid? parentId, string name, string? description);

    FolderElement CreateFolder(Guid parentId, string name, string? description);

    HostElement CreateHost(Guid parentId, string name, string address, string? description);

    SensorElement CreateSensor(
        Guid parentId,
        string name,
        string sensorTypeKey,
        string target,
        string? description,
        MonitoringSettings? settings = null);

    MonitoringTemplate CreateTemplate(string name, MonitoringTemplateScope targetKind, Guid? parentTemplateId);

    NotificationSender CreateNotificationSender(string name);

    NotificationReceiver CreateNotificationReceiver(string name);

    NotificationRule CreateNotificationRule(string name);

    bool DeleteElement(Guid id);

    bool DeleteTemplate(Guid id);

    bool DeleteNotificationSender(Guid id);

    bool DeleteNotificationReceiver(Guid id);

    bool DeleteNotificationRule(Guid id);

    bool MoveElement(Guid elementId, Guid newParentId);

    bool SetSensorPaused(Guid sensorId, bool paused);

    void SyncAlerts(IEnumerable<MonitoringAlertCandidate> activeAlerts, DateTimeOffset now);

    string RotateProbeToken(Guid probeId);

    bool TryValidateProbe(string probeId, string? probeToken);

    void Save();
}
