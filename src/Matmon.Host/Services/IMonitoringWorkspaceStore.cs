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

    /// <summary>True when no account has been provisioned yet and the first-run setup must run.</summary>
    bool IsSetupRequired();

    /// <summary>Creates the first admin account (e-mail + password) and marks setup completed.</summary>
    MatmonUser CompleteInitialSetup(string email, string password);

    MatmonUser CreateUser(string username, string password, MatmonUserRole role);

    /// <summary>Find-or-create a user for a "Sign in with Matmon Cloud" identity (SSO). Existing accounts win.</summary>
    MatmonUser UpsertCloudUser(string email, MatmonUserRole role);

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

    /// <summary>The primary (root) probe's configured discovery subnets (CIDR strings).</summary>
    IReadOnlyList<string> GetPrimaryProbeSubnets();

    /// <summary>Adds a CIDR subnet to the primary probe (no-op if blank or already present).</summary>
    void AddPrimaryProbeSubnet(string cidr);

    /// <summary>Removes a CIDR subnet from the primary probe.</summary>
    void RemovePrimaryProbeSubnet(string cidr);

    MonitoringElement? FindElement(Guid id);

    /// <summary>
    /// Runs <paramref name="mutate"/> against the live element under the store lock, then queues a
    /// save. Race-free alternative to <see cref="FindElement"/> + mutate + <see cref="Save"/>.
    /// Returns false if the id is unknown.
    /// </summary>
    bool UpdateElement(Guid id, Action<MonitoringElement> mutate);

    /// <summary>
    /// Resolves a target token (an element id, or a <c>tag:&lt;name&gt;</c> tag) to the set of
    /// sensors it points at: the element's subtree sensors, or every sensor whose effective
    /// tags include that tag. Empty for an empty/unknown token.
    /// </summary>
    IReadOnlyList<SensorElement> ResolveTargetSensors(string? targetToken);

    MonitoringTemplate? FindTemplate(Guid id);

    /// <summary>Template counterpart of <see cref="UpdateElement"/>: mutation runs under the store lock.</summary>
    bool UpdateTemplate(Guid id, Action<MonitoringTemplate> mutate);

    /// <summary>
    /// Resolves lineage, effective settings and target for a sensor atomically under the store lock,
    /// returning a detached snapshot for execution. Null if the id is not a sensor.
    /// </summary>
    SensorExecutionPlan? GetSensorExecutionPlan(Guid sensorId);

    /// <summary>The sensor-definition catalog (lightweight; avoids cloning the whole workspace).</summary>
    IReadOnlyList<SensorDefinition> GetSensorDefinitions();

    /// <summary>
    /// A fully detached workspace snapshot with a deep-cloned element tree and templates — for
    /// consumers (the dashboard) that walk the whole tree and must not race concurrent edits.
    /// </summary>
    MonitoringWorkspaceSnapshot GetWorkspaceClone();

    NotificationSender? FindNotificationSender(Guid id);

    NotificationReceiver? FindNotificationReceiver(Guid id);

    NotificationRule? FindNotificationRule(Guid id);

    bool AcknowledgeAlert(Guid alertId, string? acknowledgedBy = null);

    /// <summary>Cheap counts of the persisted active alerts (open vs. acknowledged, plus the error/warning severity split) — no snapshot clone.</summary>
    (int Open, int Acknowledged, int Error, int Warning) GetActiveAlertCounts();

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

    /// <summary>True when at least one e-mail notification sender is configured.</summary>
    bool HasEmailNotifications();

    /// <summary>Creates an e-mail sender + receiver + a Warning/Critical rule in one go (setup wizard).</summary>
    void ConfigureEmailNotifications(string smtpHost, int? smtpPort, string? username, string? password, bool useSsl, string fromEmail, string toEmail);

    /// <summary>The scheduled e-mail summary-report settings (a detached clone).</summary>
    SummaryReportSettings GetSummaryReportSettings();

    /// <summary>Persists the user-set summary-report settings (preserving the runtime last-sent timestamp).</summary>
    void UpdateSummaryReportSettings(SummaryReportSettings settings);

    /// <summary>Records that the scheduled summary report was sent at the given time.</summary>
    void MarkSummaryReportSent(DateTimeOffset sentUtc);

    /// <summary>This instance's persisted Matmon.Cloud link credentials (a detached clone).</summary>
    CloudConnectionState GetCloudConnection();

    /// <summary>Persists the Matmon.Cloud link credentials (after registering).</summary>
    void UpdateCloudConnection(CloudConnectionState state);

    /// <summary>UI-managed cloud link settings (a detached clone; token not exposed as plaintext).</summary>
    CloudConnectionSettings GetCloudConnectionSettings();

    /// <summary>The unprotected instance token for the cloud link (service use); null if none.</summary>
    string? GetCloudConnectionToken();

    /// <summary>Connect/save the cloud link from the UI (a blank token keeps the stored one).</summary>
    void SetCloudConnectionSettings(string? url, string? instanceId, string? token, bool enabled);

    /// <summary>Disconnect the cloud link from the UI (disable + drop token; env no longer re-links).</summary>
    void DisconnectCloud();

    /// <summary>Enable/disable relaying alerts to the cloud gateway + set its recipients.</summary>
    void SetCloudRelaySettings(bool relayAlerts, string? recipients);

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

    /// <summary>Pauses/resumes an element and every sensor in its subtree; returns the number changed.</summary>
    int SetElementPaused(Guid elementId, bool paused);

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
