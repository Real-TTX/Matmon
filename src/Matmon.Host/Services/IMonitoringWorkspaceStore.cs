using Matmon.Core.Domain;
using Matmon.Core.Sample;

namespace Matmon.Host.Services;

public interface IMonitoringWorkspaceStore
{
    MonitoringWorkspaceSnapshot Workspace { get; }

    IReadOnlyList<MonitoringElement> GetAllElements();

    IReadOnlyList<MonitoringTemplate> GetAllTemplates();

    IReadOnlyList<MatmonUser> GetUsers();

    MatmonUser? FindUser(Guid userId);

    MatmonUser? ValidateUser(string username, string password);

    MatmonUser CreateUser(string username, string password, MatmonUserRole role);

    bool UpdateUser(Guid userId, string username, MatmonUserRole role, bool isEnabled, string? password);

    bool DeleteUser(Guid userId);

    IReadOnlyList<MonitoringMap> GetMaps();

    MonitoringMap? FindMap(Guid id);

    MonitoringMap? FindMapByPublicToken(string publicToken);

    MonitoringMap CreateMap(string name, string? description, int columns, int rows, MonitoringMapDisplayPreset displayPreset, IReadOnlyList<MonitoringMapTile> tiles);

    bool UpdateMap(Guid mapId, string name, string? description, int columns, int rows, MonitoringMapDisplayPreset displayPreset, IReadOnlyList<MonitoringMapTile> tiles);

    MonitoringMap CreateMapWithSlides(string name, string? description, int columns, int rows, MonitoringMapDisplayPreset displayPreset, int autoRotateSeconds, MonitoringMapPaginationMode paginationMode, IReadOnlyList<MonitoringMapSlide> slides);

    bool UpdateMapWithSlides(Guid mapId, string name, string? description, int columns, int rows, MonitoringMapDisplayPreset displayPreset, int autoRotateSeconds, MonitoringMapPaginationMode paginationMode, IReadOnlyList<MonitoringMapSlide> slides);

    string RotateMapPublicToken(Guid mapId);

    bool DeleteMap(Guid mapId);

    ProbeElement? FindProbeByProbeId(string probeId);

    MonitoringElement? FindElement(Guid id);

    /// <summary>
    /// Resolves a target token (an element id, or a <c>tag:&lt;name&gt;</c> tag) to the set of
    /// sensors it points at: the element's subtree sensors, or every sensor whose effective
    /// tags include that tag. Empty for an empty/unknown token.
    /// </summary>
    IReadOnlyList<SensorElement> ResolveTargetSensors(string? targetToken);

    MonitoringTemplate? FindTemplate(Guid id);

    NotificationSender? FindNotificationSender(Guid id);

    NotificationReceiver? FindNotificationReceiver(Guid id);

    NotificationRule? FindNotificationRule(Guid id);

    bool AcknowledgeAlert(Guid alertId, string? acknowledgedBy = null);

    /// <summary>Cheap counts of the persisted active alerts (open vs. acknowledged) — no snapshot clone.</summary>
    (int Open, int Acknowledged) GetActiveAlertCounts();

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

    /// <summary>Actual stored raw-observation count per sensor (the "log length").</summary>
    IReadOnlyDictionary<Guid, int> GetSensorObservationCounts();

    IReadOnlyDictionary<Guid, SensorObservation[]> GetRecentSensorHistoryBySensor(TimeSpan window, int maxPerSensor);

    IReadOnlyList<MonitoringEvent> GetEvents(int take = 500);

    IReadOnlyList<SensorStatisticsBucket> GetSensorStatistics(Guid sensorId);

    /// <summary>
    /// Recomputes recent statistics buckets from raw observations and applies
    /// telemetry retention. Invoked periodically by the rollup service.
    /// </summary>
    TelemetryMaintenanceResult RunTelemetryMaintenance(DateTimeOffset nowUtc);

    StorageTelemetryOverview GetStorageTelemetryOverview();

    StorageCleanupResult CleanupStorage(StorageCleanupScope scope, int olderThanDays);

    IReadOnlyList<WorkspaceBackupJob> GetBackupJobs();

    WorkspaceBackupJob? FindBackupJob(Guid jobId);

    WorkspaceBackupJob CreateBackupJob(WorkspaceBackupJob job);

    bool UpdateBackupJob(WorkspaceBackupJob job);

    bool DeleteBackupJob(Guid jobId);

    IReadOnlyList<WorkspaceBackupSnapshotInfo> GetBackupSnapshots(int take = 50);

    WorkspaceBackupSnapshotInfo? FindBackupSnapshot(string fileName);

    WorkspaceBackupSnapshotDetails? FindBackupSnapshotDetails(string fileName);

    Stream? OpenBackupSnapshotReadStream(string fileName);

    WorkspaceBackupSnapshotInfo ImportBackupSnapshot(Stream content, string originalFileName);

    WorkspaceBackupSnapshotInfo RunBackupJob(Guid jobId, string? reason = null);

    WorkspaceBackupRestoreResult RestoreBackupSnapshot(string fileName, WorkspaceBackupSection sections);

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

    bool MoveElementBefore(Guid elementId, Guid siblingId);

    bool MoveElementAfter(Guid elementId, Guid siblingId);

    bool SetSensorPaused(Guid sensorId, bool paused);

    void SyncAlerts(IEnumerable<MonitoringAlertCandidate> activeAlerts, DateTimeOffset now);

    string RotateProbeToken(Guid probeId);

    bool TryValidateProbe(string probeId, string? probeToken);

    void Save();
}

public enum StorageCleanupScope
{
    Telemetry = 0,
    SensorHistory = 1,
    Events = 2,
    Statistics = 3,
    Everything = 4
}

public sealed record StorageTelemetryOverview(
    long SensorHistoryCount,
    long EventCount,
    long StatisticsBucketCount)
{
    public long TotalEntryCount => SensorHistoryCount + EventCount + StatisticsBucketCount;
}

public sealed record StorageCleanupResult(
    long SensorHistoryRemoved,
    long EventsRemoved,
    long StatisticsRemoved)
{
    public long TotalRemoved => SensorHistoryRemoved + EventsRemoved + StatisticsRemoved;
}
