using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matmon.Core.Domain;
using Matmon.Core.Sample;
using Matmon.Core.Telemetry;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.DataProtection;

namespace Matmon.Host.Services;

public sealed partial class InMemoryMonitoringWorkspaceStore : IMonitoringWorkspaceStore, IDisposable
{
    private static readonly JsonSerializerOptions FileSerializerOptions = CreateSerializerOptions();
    private const int DefaultEventRetentionDays = 30;
    private const int DefaultObservationRetentionDays = 7;
    private const int DefaultStatisticsRetentionDays = 90;
    private const int DefaultStatisticsBucketMinutes = 60;
    private static readonly TimeSpan ConfigurationSaveDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan TelemetrySaveDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxDirtySaveDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BackupRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly ILogger<InMemoryMonitoringWorkspaceStore> _logger;
    private readonly IDataProtector _credentialProtector;
    private readonly MatmonAuthOptions _authOptions;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly string _workspacePath;
    private readonly string _workspaceBackupPath;
    private readonly string _backupDirectoryPath;
    private readonly Timer _saveTimer;
    private readonly ITelemetryRepository _telemetry;
    private WorkspaceDocument _document;
    private DateTimeOffset? _firstDirtyUtc;
    private DateTimeOffset _lastBackupUtc = DateTimeOffset.MinValue;
    private bool _saveInProgress;
    private bool _disposed;
    private long _dirtyVersion;
    private long _savedVersion;

    public InMemoryMonitoringWorkspaceStore(
        IHostEnvironment environment,
        MatmonRuntimeOptions runtimeOptions,
        MatmonAuthOptions authOptions,
        IDataProtectionProvider dataProtectionProvider,
        ITelemetryRepository telemetry,
        ILogger<InMemoryMonitoringWorkspaceStore> logger)
    {
        _logger = logger;
        _authOptions = authOptions;
        _runtimeOptions = runtimeOptions;
        _telemetry = telemetry;
        _credentialProtector = dataProtectionProvider.CreateProtector("Matmon.Credentials");

        var configuredPath = string.IsNullOrWhiteSpace(runtimeOptions.WorkspacePath)
            ? "data/workspace.json"
            : runtimeOptions.WorkspacePath;

        _workspacePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
        _workspaceBackupPath = _workspacePath + ".bak";
        _backupDirectoryPath = ResolveBackupDirectoryPath(environment, runtimeOptions, _workspacePath);
        _saveTimer = new Timer(FlushPendingSaveFromTimer);

        _document = LoadDocument();
        HydrateCredentialBundles(_document);
        MigrateDocumentTelemetryIntoRepository();
        EnsureSensorDefinitionCatalog();
        EnsureDefaultTemplates();
        EnsureDefaultProbeMetadata(_runtimeOptions.AutoCreateProbeSystemSensors);
        if (_runtimeOptions.ProvisionLocalDockerProbe)
        {
            EnsureDefaultDockerSecondaryProbe();
        }

        if (_runtimeOptions.ProvisionDemoSensors)
        {
            EnsureDefaultWindowsHealthSensor();
            EnsureDefaultProxmoxSensor();
        }

        EnsureDefaultNotificationConfiguration();
        EnsureDefaultUsers();
        EnsureDefaultMaps(_runtimeOptions.CreateStarterMap);
        EnsureDefaultAlertCollection();
        SaveNow();
    }

    private static string ResolveBackupDirectoryPath(IHostEnvironment environment, MatmonRuntimeOptions runtimeOptions, string workspacePath)
    {
        var configuredPath = string.IsNullOrWhiteSpace(runtimeOptions.BackupPath)
            ? Path.Combine(Path.GetDirectoryName(workspacePath) ?? environment.ContentRootPath, "backups")
            : runtimeOptions.BackupPath;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
    }

    public MonitoringWorkspaceSnapshot Workspace
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public IReadOnlyList<MonitoringElement> GetAllElements()
    {
        lock (_gate)
        {
            return EnumerateElements(_document.RootProbe).ToArray();
        }
    }

    public IReadOnlyList<MonitoringTemplate> GetAllTemplates()
    {
        lock (_gate)
        {
            return _document.Templates.ToArray();
        }
    }

    public IReadOnlyList<MatmonUser> GetUsers()
    {
        lock (_gate)
        {
            EnsureDefaultUsers();
            return _document.Users
                .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
                .Select(CloneUser)
                .ToArray();
        }
    }

    public MatmonUser? FindUser(Guid userId)
    {
        lock (_gate)
        {
            EnsureDefaultUsers();
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            return user is null ? null : CloneUser(user);
        }
    }

    public MatmonUser? ValidateUser(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureDefaultUsers();
            var user = _document.Users.FirstOrDefault(candidate =>
                candidate.IsEnabled &&
                string.Equals(candidate.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user is null || !MatmonPasswordHasher.Verify(password, user.PasswordHash))
            {
                return null;
            }

            return CloneUser(user);
        }
    }

    public MatmonUser CreateUser(string username, string password, MatmonUserRole role)
    {
        lock (_gate)
        {
            EnsureDefaultUsers();
            var normalizedUsername = NormalizeUsername(username);
            if (_document.Users.Any(user => string.Equals(user.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"User '{normalizedUsername}' already exists.");
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Password is required.");
            }

            var now = DateTimeOffset.UtcNow;
            var user = new MatmonUser
            {
                Username = normalizedUsername,
                PasswordHash = MatmonPasswordHasher.Hash(password),
                Role = role,
                IsEnabled = true,
                CreatedUtc = now,
                UpdatedUtc = now
            };

            _document.Users.Add(user);
            QueueSave(SavePriority.Configuration);
            return CloneUser(user);
        }
    }

    public bool UpdateUser(Guid userId, string username, MatmonUserRole role, bool isEnabled, string? password)
    {
        lock (_gate)
        {
            EnsureDefaultUsers();
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return false;
            }

            var normalizedUsername = NormalizeUsername(username);
            if (_document.Users.Any(candidate =>
                    candidate.Id != userId &&
                    string.Equals(candidate.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"User '{normalizedUsername}' already exists.");
            }

            user.Username = normalizedUsername;
            user.Role = role;
            user.IsEnabled = isEnabled;
            user.UpdatedUtc = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(password))
            {
                user.PasswordHash = MatmonPasswordHasher.Hash(password);
            }

            if (!_document.Users.Any(candidate => candidate.IsEnabled && candidate.Role == MatmonUserRole.Admin))
            {
                user.Role = MatmonUserRole.Admin;
                user.IsEnabled = true;
            }

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool DeleteUser(Guid userId)
    {
        lock (_gate)
        {
            EnsureDefaultUsers();
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return false;
            }

            if (user.Role == MatmonUserRole.Admin &&
                _document.Users.Count(candidate => candidate.IsEnabled && candidate.Role == MatmonUserRole.Admin) <= 1)
            {
                throw new InvalidOperationException("At least one enabled admin user is required.");
            }

            _document.Users.Remove(user);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public IReadOnlyList<MonitoringMap> GetMaps()
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            return _document.Maps
                .OrderBy(map => map.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CloneMap)
                .ToArray();
        }
    }

    public MonitoringMap? FindMap(Guid id)
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            var map = _document.Maps.FirstOrDefault(candidate => candidate.Id == id);
            return map is null ? null : CloneMap(map);
        }
    }

    public MonitoringMap? FindMapByPublicToken(string publicToken)
    {
        if (string.IsNullOrWhiteSpace(publicToken))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureDefaultMaps();
            var normalizedToken = publicToken.Trim();
            var map = _document.Maps.FirstOrDefault(candidate =>
                string.Equals(candidate.PublicToken, normalizedToken, StringComparison.OrdinalIgnoreCase));
            return map is null ? null : CloneMap(map);
        }
    }

    public MonitoringMap CreateMap(
        string name,
        string? description,
        int columns,
        int rows,
        MonitoringMapDisplayPreset displayPreset,
        IReadOnlyList<MonitoringMapTile> tiles)
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            var now = DateTimeOffset.UtcNow;
            var map = new MonitoringMap
            {
                Name = NormalizeMapName(name),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Columns = Math.Clamp(columns, 4, 24),
                Rows = Math.Clamp(rows, 3, 16),
                DisplayPreset = displayPreset,
                PublicToken = CreateToken(),
                CreatedUtc = now,
                UpdatedUtc = now,
                Tiles = NormalizeMapTiles(tiles, Math.Clamp(columns, 4, 24), Math.Clamp(rows, 3, 16)).ToList()
            };

            _document.Maps.Add(map);
            QueueSave(SavePriority.Configuration);
            return CloneMap(map);
        }
    }

    public bool UpdateMap(
        Guid mapId,
        string name,
        string? description,
        int columns,
        int rows,
        MonitoringMapDisplayPreset displayPreset,
        IReadOnlyList<MonitoringMapTile> tiles)
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            var map = _document.Maps.FirstOrDefault(candidate => candidate.Id == mapId);
            if (map is null)
            {
                return false;
            }

            var normalizedColumns = Math.Clamp(columns, 4, 24);
            var normalizedRows = Math.Clamp(rows, 3, 16);
            map.Name = NormalizeMapName(name);
            map.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            map.Columns = normalizedColumns;
            map.Rows = normalizedRows;
            map.DisplayPreset = displayPreset;
            map.Tiles = NormalizeMapTiles(tiles, normalizedColumns, normalizedRows).ToList();
            map.UpdatedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public string RotateMapPublicToken(Guid mapId)
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            var map = _document.Maps.FirstOrDefault(candidate => candidate.Id == mapId);
            if (map is null)
            {
                return string.Empty;
            }

            map.PublicToken = CreateToken();
            map.UpdatedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return map.PublicToken;
        }
    }

    public bool DeleteMap(Guid mapId)
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            var map = _document.Maps.FirstOrDefault(candidate => candidate.Id == mapId);
            if (map is null)
            {
                return false;
            }

            _document.Maps.Remove(map);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public ProbeElement? FindProbeByProbeId(string probeId)
    {
        if (string.IsNullOrWhiteSpace(probeId))
        {
            return null;
        }

        lock (_gate)
        {
            return EnumerateElements(_document.RootProbe)
                .OfType<ProbeElement>()
                .FirstOrDefault(probe => string.Equals(probe.ProbeId, probeId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public MonitoringElement? FindElement(Guid id)
    {
        lock (_gate)
        {
            return EnumerateElements(_document.RootProbe)
                .FirstOrDefault(element => element.Id == id);
        }
    }

    public MonitoringTemplate? FindTemplate(Guid id)
    {
        lock (_gate)
        {
            return _document.Templates.FirstOrDefault(template => template.Id == id);
        }
    }

    public NotificationSender? FindNotificationSender(Guid id)
    {
        lock (_gate)
        {
            return _document.NotificationSenders.FirstOrDefault(sender => sender.Id == id);
        }
    }

    public NotificationReceiver? FindNotificationReceiver(Guid id)
    {
        lock (_gate)
        {
            return _document.NotificationReceivers.FirstOrDefault(receiver => receiver.Id == id);
        }
    }

    public NotificationRule? FindNotificationRule(Guid id)
    {
        lock (_gate)
        {
            return _document.NotificationRules.FirstOrDefault(rule => rule.Id == id);
        }
    }

    public bool AcknowledgeAlert(Guid alertId, string? acknowledgedBy = null)
    {
        lock (_gate)
        {
            var alert = _document.Alerts.FirstOrDefault(candidate => candidate.Id == alertId);
            if (alert is null || !alert.IsActive)
            {
                return false;
            }

            var changed = false;
            if (!alert.IsAcknowledged)
            {
                alert.AcknowledgedUtc = DateTimeOffset.UtcNow;
                changed = true;
            }

            if (alert.AcknowledgedState != alert.State)
            {
                alert.AcknowledgedState = alert.State;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(acknowledgedBy) &&
                !string.Equals(alert.AcknowledgedBy, acknowledgedBy.Trim(), StringComparison.Ordinal))
            {
                alert.AcknowledgedBy = acknowledgedBy.Trim();
                changed = true;
            }

            if (changed)
            {
                AddEvent(new MonitoringEvent
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Kind = MonitoringEventKind.AlertAcknowledged,
                    ElementId = alert.ElementId,
                    ElementKind = alert.ElementKind,
                    ElementName = alert.ElementName,
                    ElementPath = alert.ElementPath,
                    State = alert.State,
                    Message = $"Alert acknowledged{(string.IsNullOrWhiteSpace(alert.AcknowledgedBy) ? string.Empty : $" by {alert.AcknowledgedBy}")}"
                });
                QueueSave(SavePriority.Configuration);
            }

            return true;
        }
    }

    public void RecordSensorObservation(
        Guid sensorId,
        SensorExecutionResult result,
        DateTimeOffset timestampUtc,
        MonitoringSettings? settings = null,
        string? executedByProbeId = null,
        string? executedByProbeName = null)
    {
        lock (_gate)
        {
            EnsureDefaultAlertCollection();

            var previousObservation = _telemetry.GetLatestObservation(sensorId);

            var observation = new SensorObservation
            {
                SensorId = sensorId,
                TimestampUtc = timestampUtc,
                State = result.State,
                Value = result.Value,
                DefaultChannelKey = result.DefaultChannelKey,
                Channels = result.Channels.Select(channel => channel with { }).ToList(),
                ExecutedByProbeId = string.IsNullOrWhiteSpace(executedByProbeId) ? null : executedByProbeId.Trim(),
                ExecutedByProbeName = string.IsNullOrWhiteSpace(executedByProbeName) ? null : executedByProbeName.Trim(),
                Duration = result.Duration,
                Message = result.Message
            };

            _telemetry.AppendObservation(observation);

            if (ShouldRecordStateChangeEvent(previousObservation, result))
            {
                AddEvent(new MonitoringEvent
                {
                    TimestampUtc = timestampUtc,
                    Kind = MonitoringEventKind.StateChanged,
                    ElementId = sensorId,
                    ElementKind = MonitoringElementKind.Sensor,
                    ElementName = GetElementName(sensorId),
                    ElementPath = GetElementPath(sensorId),
                    State = result.State,
                    Message = AppendExecutionProbe(
                        BuildStateChangeMessage(previousObservation?.State, result.State, result.Message),
                        executedByProbeName,
                        executedByProbeId)
                });
            }

            SyncSensorAlertFromObservation(sensorId, result, timestampUtc);
            PruneSensorHistory(sensorId, timestampUtc, settings);
            UpdateSensorStatistics(sensorId, result, timestampUtc, settings);
            PruneEvents(timestampUtc, settings);
            PruneStatistics(sensorId, timestampUtc, settings);
            QueueSave(SavePriority.Telemetry);
        }
    }

    public IReadOnlyList<SensorObservation> GetSensorHistory()
    {
        return _telemetry.GetAllObservations();
    }

    public IReadOnlyList<SensorObservation> GetSensorHistory(Guid sensorId, TimeSpan? window = null, int? maxCount = null)
    {
        if (maxCount is <= 0)
        {
            return Array.Empty<SensorObservation>();
        }

        var cutoffUtc = window is { } requestedWindow && requestedWindow > TimeSpan.Zero
            ? DateTimeOffset.UtcNow - requestedWindow
            : DateTimeOffset.MinValue;
        return _telemetry.GetObservations(sensorId, cutoffUtc, maxCount);
    }

    public IReadOnlyDictionary<Guid, SensorObservation> GetLatestSensorObservations()
    {
        return _telemetry.GetLatestObservations();
    }

    public IReadOnlyDictionary<Guid, SensorObservation[]> GetRecentSensorHistoryBySensor(TimeSpan window, int maxPerSensor)
    {
        var cutoffUtc = window > TimeSpan.Zero
            ? DateTimeOffset.UtcNow - window
            : DateTimeOffset.MinValue;
        return _telemetry.GetRecentObservationsBySensor(cutoffUtc, maxPerSensor);
    }

    public IReadOnlyList<MonitoringEvent> GetEvents(int take = 500)
    {
        return _telemetry.GetEvents(take);
    }

    public IReadOnlyList<SensorStatisticsBucket> GetSensorStatistics(Guid sensorId)
    {
        return _telemetry.GetStatistics(sensorId);
    }

    public StorageTelemetryOverview GetStorageTelemetryOverview()
    {
        var counts = _telemetry.GetCounts();
        return new StorageTelemetryOverview(counts.Observations, counts.Events, counts.Statistics);
    }

    public StorageCleanupResult CleanupStorage(StorageCleanupScope scope, int olderThanDays)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown cleanup scope.");
        }

        if (olderThanDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(olderThanDays), olderThanDays, "Cleanup age must be zero or greater.");
        }

        DateTimeOffset? olderThanUtc = olderThanDays == 0
            ? null
            : DateTimeOffset.UtcNow - TimeSpan.FromDays(olderThanDays);

        var historyRemoved = ShouldCleanupHistory(scope) ? _telemetry.DeleteObservations(olderThanUtc) : 0;
        var eventsRemoved = ShouldCleanupEvents(scope) ? _telemetry.DeleteEvents(olderThanUtc) : 0;
        var statisticsRemoved = ShouldCleanupStatistics(scope) ? _telemetry.DeleteStatistics(olderThanUtc) : 0;

        return new StorageCleanupResult(historyRemoved, eventsRemoved, statisticsRemoved);
    }


    private static bool ShouldCleanupHistory(StorageCleanupScope scope)
    {
        return scope is StorageCleanupScope.Telemetry
            or StorageCleanupScope.SensorHistory
            or StorageCleanupScope.Everything;
    }

    private static bool ShouldCleanupEvents(StorageCleanupScope scope)
    {
        return scope is StorageCleanupScope.Events
            or StorageCleanupScope.Everything;
    }

    private static bool ShouldCleanupStatistics(StorageCleanupScope scope)
    {
        return scope is StorageCleanupScope.Telemetry
            or StorageCleanupScope.Statistics
            or StorageCleanupScope.Everything;
    }


    public ProbeElement CreateProbe(Guid? parentId, string name, string? description)
    {
        lock (_gate)
        {
            var parent = ResolveParentContainer(parentId, MonitoringElementKind.Probe);
            var probe = new ProbeElement(string.IsNullOrWhiteSpace(name) ? "Probe" : name.Trim())
            {
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                ProbeId = GenerateUniqueProbeId(name),
                EnrollmentToken = CreateToken()
            };

            AddChild(parent, probe);
            EnsureProbeHeartbeatSensor(probe);
            QueueSave(SavePriority.Configuration);
            return probe;
        }
    }

    public FolderElement CreateFolder(Guid parentId, string name, string? description)
    {
        lock (_gate)
        {
            var parent = ResolveParentContainer(parentId, MonitoringElementKind.Probe, MonitoringElementKind.Folder);
            var folder = new FolderElement(string.IsNullOrWhiteSpace(name) ? "Folder" : name.Trim())
            {
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
            };

            AddChild(parent, folder);
            QueueSave(SavePriority.Configuration);
            return folder;
        }
    }

    public HostElement CreateHost(Guid parentId, string name, string address, string? description)
    {
        lock (_gate)
        {
            var parent = ResolveParentContainer(parentId, MonitoringElementKind.Probe, MonitoringElementKind.Folder);
            var host = new HostElement(string.IsNullOrWhiteSpace(name) ? "Host" : name.Trim())
            {
                Address = address?.Trim() ?? string.Empty,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
            };

            AddChild(parent, host);
            QueueSave(SavePriority.Configuration);
            return host;
        }
    }

    public SensorElement CreateSensor(
        Guid parentId,
        string name,
        string sensorTypeKey,
        string target,
        string? description,
        MonitoringSettings? settings = null)
    {
        lock (_gate)
        {
            if (string.Equals(sensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException("Heartbeat sensors must be attached to a non-root probe.");
            }

            var parent = ResolveParentContainer(parentId, MonitoringElementKind.Probe, MonitoringElementKind.Folder, MonitoringElementKind.Host);
            if (!_document.SensorDefinitions.Any(definition => string.Equals(definition.Key, sensorTypeKey, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Unknown sensor type '{sensorTypeKey}'.");
            }

            var sensor = new SensorElement(
                string.IsNullOrWhiteSpace(name) ? "Sensor" : name.Trim(),
                sensorTypeKey.Trim(),
                target?.Trim() ?? string.Empty)
            {
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
            };

            if (settings is not null)
            {
                sensor.Settings.ApplyFrom(settings);
            }

            AddChild(parent, sensor);
            QueueSave(SavePriority.Configuration);
            return sensor;
        }
    }

    public MonitoringTemplate CreateTemplate(string name, MonitoringTemplateScope targetKind, Guid? parentTemplateId)
    {
        lock (_gate)
        {
            if (parentTemplateId is Guid parentId && _document.Templates.All(template => template.Id != parentId))
            {
                throw new InvalidOperationException($"Template parent '{parentId}' does not exist.");
            }

            var template = new MonitoringTemplate
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Template" : name.Trim(),
                TargetKind = targetKind,
                ParentTemplateId = parentTemplateId
            };

            _document.Templates.Add(template);
            QueueSave(SavePriority.Configuration);
            return template;
        }
    }

    public NotificationSender CreateNotificationSender(string name)
    {
        lock (_gate)
        {
            var sender = new NotificationSender
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Notification sender" : name.Trim()
            };

            _document.NotificationSenders.Add(sender);
            QueueSave(SavePriority.Configuration);
            return sender;
        }
    }

    public NotificationReceiver CreateNotificationReceiver(string name)
    {
        lock (_gate)
        {
            var receiver = new NotificationReceiver
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Notification receiver" : name.Trim()
            };

            _document.NotificationReceivers.Add(receiver);
            QueueSave(SavePriority.Configuration);
            return receiver;
        }
    }

    public NotificationRule CreateNotificationRule(string name)
    {
        lock (_gate)
        {
            var rule = new NotificationRule
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Notification rule" : name.Trim()
            };

            rule.TriggerStates.Add(SensorState.Warning);
            rule.TriggerStates.Add(SensorState.Critical);

            _document.NotificationRules.Add(rule);
            QueueSave(SavePriority.Configuration);
            return rule;
        }
    }

    public bool DeleteElement(Guid id)
    {
        lock (_gate)
        {
            if (_document.RootProbe.Id == id)
            {
                return false;
            }

            if (FindElement(id) is SensorElement sensor &&
                string.Equals(sensor.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var removed = RemoveChild(_document.RootProbe, id);
            if (removed)
            {
                QueueSave(SavePriority.Configuration);
            }

            return removed;
        }
    }

    public bool DeleteTemplate(Guid id)
    {
        lock (_gate)
        {
            var template = _document.Templates.FirstOrDefault(candidate => candidate.Id == id);
            if (template is null)
            {
                return false;
            }

            _document.Templates.Remove(template);

            foreach (var element in EnumerateElements(_document.RootProbe))
            {
                element.AppliedTemplateIds.RemoveAll(templateId => templateId == id);
            }

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool DeleteNotificationSender(Guid id)
    {
        lock (_gate)
        {
            if (_document.NotificationRules.Any(rule => rule.SenderId == id))
            {
                return false;
            }

            var sender = _document.NotificationSenders.FirstOrDefault(candidate => candidate.Id == id);
            if (sender is null)
            {
                return false;
            }

            _document.NotificationSenders.Remove(sender);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool DeleteNotificationReceiver(Guid id)
    {
        lock (_gate)
        {
            if (_document.NotificationRules.Any(rule => rule.ReceiverId == id))
            {
                return false;
            }

            var receiver = _document.NotificationReceivers.FirstOrDefault(candidate => candidate.Id == id);
            if (receiver is null)
            {
                return false;
            }

            _document.NotificationReceivers.Remove(receiver);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool DeleteNotificationRule(Guid id)
    {
        lock (_gate)
        {
            var rule = _document.NotificationRules.FirstOrDefault(candidate => candidate.Id == id);
            if (rule is null)
            {
                return false;
            }

            _document.NotificationRules.Remove(rule);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool MoveElement(Guid elementId, Guid newParentId)
    {
        lock (_gate)
        {
            if (_document.RootProbe.Id == elementId)
            {
                return false;
            }

            var element = FindElement(elementId);
            if (element is null)
            {
                return false;
            }

            if (element is MonitoringContainerElement container &&
                EnumerateElements(container).Any(candidate => candidate.Id == newParentId))
            {
                return false;
            }

            var oldParent = FindParentContainer(_document.RootProbe, elementId);
            var newParent = ResolveParentContainer(newParentId, GetAllowedParentKinds(element));

            if (oldParent is null)
            {
                return false;
            }

            if (oldParent.Id == newParent.Id)
            {
                return true;
            }

            if (!oldParent.Children.Remove(element))
            {
                return false;
            }

            AddChild(newParent, element);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool MoveElementBefore(Guid elementId, Guid siblingId)
    {
        return MoveElementRelative(elementId, siblingId, before: true);
    }

    public bool MoveElementAfter(Guid elementId, Guid siblingId)
    {
        return MoveElementRelative(elementId, siblingId, before: false);
    }

    public bool SetSensorPaused(Guid sensorId, bool paused)
    {
        lock (_gate)
        {
            var sensor = EnumerateElements(_document.RootProbe)
                .OfType<SensorElement>()
                .FirstOrDefault(candidate => candidate.Id == sensorId);

            if (sensor is null)
            {
                return false;
            }

            if (sensor.IsPaused == paused)
            {
                return true;
            }

            sensor.IsPaused = paused;

            if (paused)
            {
                ResolveAlertsForElement(sensor.Id, DateTimeOffset.UtcNow, "sensor paused");
            }

            AddEvent(new MonitoringEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = paused ? MonitoringEventKind.Paused : MonitoringEventKind.Resumed,
                ElementId = sensor.Id,
                ElementKind = sensor.Kind,
                ElementName = sensor.Name,
                ElementPath = GetElementPath(sensor),
                State = paused ? SensorState.Paused : SensorState.Healthy,
                Message = paused ? "Sensor paused" : "Sensor resumed"
            });

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    private bool MoveElementRelative(Guid elementId, Guid siblingId, bool before)
    {
        lock (_gate)
        {
            if (_document.RootProbe.Id == elementId || elementId == siblingId)
            {
                return false;
            }

            var element = FindElement(elementId);
            var sibling = FindElement(siblingId);
            if (element is null || sibling is null)
            {
                return false;
            }

            if (element is MonitoringContainerElement container &&
                EnumerateElements(container).Any(candidate => candidate.Id == siblingId))
            {
                return false;
            }

            var oldParent = FindParentContainer(_document.RootProbe, elementId);
            var newParent = FindParentContainer(_document.RootProbe, siblingId);
            if (oldParent is null || newParent is null)
            {
                return false;
            }

            if (!GetAllowedParentKinds(element).Contains(newParent.Kind))
            {
                return false;
            }

            var sourceIndex = oldParent.Children.FindIndex(candidate => candidate.Id == elementId);
            var targetIndex = newParent.Children.FindIndex(candidate => candidate.Id == siblingId);
            if (sourceIndex < 0 || targetIndex < 0)
            {
                return false;
            }

            if (!oldParent.Children.Remove(element))
            {
                return false;
            }

            if (ReferenceEquals(oldParent, newParent) && targetIndex > sourceIndex)
            {
                targetIndex--;
            }

            var insertIndex = before ? targetIndex : targetIndex + 1;
            insertIndex = Math.Clamp(insertIndex, 0, newParent.Children.Count);
            newParent.Children.Insert(insertIndex, element);
            element.ParentId = newParent.Id;
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public void SyncAlerts(IEnumerable<MonitoringAlertCandidate> activeAlerts, DateTimeOffset now)
    {
        lock (_gate)
        {
            EnsureDefaultAlertCollection();

            var activeAlertList = activeAlerts.ToList();
            var activeByElementId = activeAlertList.ToDictionary(candidate => candidate.ElementId);
            var changed = false;

            foreach (var candidate in activeAlertList)
            {
                var existing = _document.Alerts.FirstOrDefault(alert => alert.IsActive && alert.ElementId == candidate.ElementId);
                if (existing is null)
                {
                    _document.Alerts.Add(new MonitoringAlert
                    {
                        ElementId = candidate.ElementId,
                        ElementKind = candidate.ElementKind,
                        ElementName = candidate.ElementName,
                        ElementPath = candidate.ElementPath,
                        State = candidate.State,
                        Message = candidate.Message,
                        FirstSeenUtc = now,
                        LastSeenUtc = now
                    });
                    AddEvent(new MonitoringEvent
                    {
                        TimestampUtc = now,
                        Kind = MonitoringEventKind.AlertRaised,
                        ElementId = candidate.ElementId,
                        ElementKind = candidate.ElementKind,
                        ElementName = candidate.ElementName,
                        ElementPath = candidate.ElementPath,
                        State = candidate.State,
                        Message = candidate.Message
                    });
                    changed = true;
                    continue;
                }

                if (existing.ElementKind != candidate.ElementKind)
                {
                    existing.ElementKind = candidate.ElementKind;
                    changed = true;
                }

                if (!string.Equals(existing.ElementName, candidate.ElementName, StringComparison.Ordinal))
                {
                    existing.ElementName = candidate.ElementName;
                    changed = true;
                }

                if (!string.Equals(existing.ElementPath, candidate.ElementPath, StringComparison.Ordinal))
                {
                    existing.ElementPath = candidate.ElementPath;
                    changed = true;
                }

                if (existing.State != candidate.State)
                {
                    existing.State = candidate.State;
                    changed = true;
                }

                if (!string.Equals(existing.Message, candidate.Message, StringComparison.Ordinal))
                {
                    existing.Message = candidate.Message;
                    changed = true;
                }

                existing.LastSeenUtc = now;
            }

            foreach (var alert in _document.Alerts.Where(alert => alert.IsActive))
            {
                if (activeByElementId.ContainsKey(alert.ElementId))
                {
                    continue;
                }

                alert.ResolvedUtc = now;
                AddEvent(new MonitoringEvent
                {
                    TimestampUtc = now,
                    Kind = MonitoringEventKind.AlertResolved,
                    ElementId = alert.ElementId,
                    ElementKind = alert.ElementKind,
                    ElementName = alert.ElementName,
                    ElementPath = alert.ElementPath,
                    State = alert.State,
                    Message = alert.Message
                });
                changed = true;
            }

            if (changed)
            {
                QueueSave(SavePriority.Telemetry);
            }
        }
    }

    public string RotateProbeToken(Guid probeId)
    {
        lock (_gate)
        {
            var probe = EnumerateElements(_document.RootProbe)
                .OfType<ProbeElement>()
                .FirstOrDefault(candidate => candidate.Id == probeId);

            if (probe is null)
            {
                throw new InvalidOperationException($"Probe '{probeId}' does not exist.");
            }

            probe.EnrollmentToken = CreateToken();
            QueueSave(SavePriority.Configuration);
            return probe.EnrollmentToken;
        }
    }

    public bool TryValidateProbe(string probeId, string? probeToken)
    {
        if (string.IsNullOrWhiteSpace(probeId))
        {
            return false;
        }

        lock (_gate)
        {
            var probe = EnumerateElements(_document.RootProbe)
                .OfType<ProbeElement>()
                .FirstOrDefault(candidate => string.Equals(candidate.ProbeId, probeId, StringComparison.OrdinalIgnoreCase));

            if (probe is null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(probe.EnrollmentToken))
            {
                return true;
            }

            return string.Equals(probe.EnrollmentToken, probeToken, StringComparison.Ordinal);
        }
    }

    public void Save()
    {
        QueueSave(SavePriority.Configuration);
    }

    public void Dispose()
    {
        lock (_saveGate)
        {
            _disposed = true;
            _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            while (_saveInProgress)
            {
                Monitor.Wait(_saveGate);
            }
        }

        try
        {
            FlushPendingSave();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush workspace during shutdown");
        }

        _saveTimer.Dispose();
    }

    private MonitoringWorkspaceSnapshot CreateSnapshot()
    {
        return new MonitoringWorkspaceSnapshot(
            _document.RootProbe,
            _document.Templates,
            _document.SensorDefinitions,
            _document.NotificationConfiguration,
            _document.NotificationSenders,
            _document.NotificationReceivers,
            _document.NotificationRules,
            _document.Alerts);
    }

    private WorkspaceDocument LoadDocument()
    {
        var loaded = TryLoadWorkspaceDocument(_workspacePath);
        if (loaded?.RootProbe is not null)
        {
            _logger.LogInformation("Workspace loaded from {WorkspacePath}", _workspacePath);
            return loaded;
        }

        loaded = TryLoadWorkspaceDocument(_workspaceBackupPath);
        if (loaded?.RootProbe is not null)
        {
            _logger.LogWarning(
                "Workspace loaded from backup {WorkspacePathBackup} because the primary file could not be read.",
                _workspaceBackupPath);
            return loaded;
        }

        if (File.Exists(_workspacePath) || File.Exists(_workspaceBackupPath))
        {
            _logger.LogWarning(
                "Failed to load workspace from {WorkspacePath} or backup {WorkspacePathBackup}, creating a new plain workspace",
                _workspacePath,
                _workspaceBackupPath);
        }

        if (_runtimeOptions.SeedSampleData)
        {
            _logger.LogInformation("Seeding sample workspace because Matmon:SeedSampleData is enabled");
            var sample = SampleTopologyFactory.Create();
            return new WorkspaceDocument
            {
                RootProbe = sample.RootProbe,
                Templates = sample.Templates.ToList(),
                SensorDefinitions = sample.SensorDefinitions.ToList(),
                NotificationConfiguration = sample.NotificationConfiguration,
                NotificationSenders = sample.NotificationSenders.ToList(),
                NotificationReceivers = sample.NotificationReceivers.ToList(),
                NotificationRules = sample.NotificationRules.ToList(),
                Alerts = sample.Alerts.ToList(),
                BackupJobs = [],
                SensorHistory = [],
                Events = [],
                SensorStatistics = []
            };
        }

        return CreatePlainWorkspaceDocument();
    }

    private static WorkspaceDocument CreatePlainWorkspaceDocument()
    {
        return new WorkspaceDocument
        {
            RootProbe = new ProbeElement("Primary Probe")
            {
                ProbeId = "primary",
                Description = "Local primary probe"
            },
            Templates = [],
            SensorDefinitions = [],
            NotificationConfiguration = new NotificationWorkspaceConfiguration(),
            NotificationSenders = [],
            NotificationReceivers = [],
            NotificationRules = [],
            Alerts = [],
            BackupJobs = [],
            SensorHistory = [],
            Events = [],
            SensorStatistics = [],
            Maps = [],
            Users = []
        };
    }

    private void QueueSave(SavePriority priority)
    {
        Interlocked.Increment(ref _dirtyVersion);

        lock (_saveGate)
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            _firstDirtyUtc ??= now;
            ScheduleSaveLocked(priority, now);
        }
    }

    private void ScheduleSaveLocked(SavePriority priority, DateTimeOffset now)
    {
        var delay = priority == SavePriority.Configuration
            ? ConfigurationSaveDelay
            : TelemetrySaveDelay;

        if (_firstDirtyUtc is DateTimeOffset firstDirtyUtc)
        {
            var maxDelay = MaxDirtySaveDelay - (now - firstDirtyUtc);
            if (maxDelay < delay)
            {
                delay = maxDelay;
            }
        }

        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        _saveTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void FlushPendingSaveFromTimer(object? _)
    {
        FlushPendingSave(scheduleAgain: true);
    }

    private void FlushPendingSave(bool scheduleAgain = false)
    {
        long versionToSave;

        lock (_saveGate)
        {
            if (_saveInProgress)
            {
                return;
            }

            versionToSave = Volatile.Read(ref _dirtyVersion);
            if (versionToSave == Volatile.Read(ref _savedVersion))
            {
                _firstDirtyUtc = null;
                return;
            }

            _saveInProgress = true;
            _firstDirtyUtc = null;
        }

        try
        {
            SaveNow(versionToSave);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background workspace save failed");
        }
        finally
        {
            lock (_saveGate)
            {
                _saveInProgress = false;
                Monitor.PulseAll(_saveGate);

                if (scheduleAgain &&
                    !_disposed &&
                    Volatile.Read(ref _dirtyVersion) != Volatile.Read(ref _savedVersion))
                {
                    _firstDirtyUtc ??= DateTimeOffset.UtcNow;
                    ScheduleSaveLocked(SavePriority.Configuration, DateTimeOffset.UtcNow);
                }
            }
        }
    }

    private void SaveNow(long? versionToSave = null)
    {
        lock (_gate)
        {
            SaveDocumentLocked();
        }

        Interlocked.Exchange(ref _savedVersion, versionToSave ?? Volatile.Read(ref _dirtyVersion));
    }

    private void SaveDocumentLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_workspacePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            ProtectCredentialBundles(_document);
            var json = JsonSerializer.Serialize(_document, FileSerializerOptions);
            var tempPath = _workspacePath + ".tmp";
            try
            {
                WriteUtf8File(tempPath, json);
                File.Move(tempPath, _workspacePath, overwrite: true);
            }
            catch (Exception moveEx)
            {
                _logger.LogWarning(moveEx, "Atomic workspace move failed, falling back to direct write");
                WriteUtf8File(_workspacePath, json);
            }
            finally
            {
                TryDeleteTempFile(tempPath);
            }

            RefreshBackupIfDue();

            HydrateCredentialBundles(_document);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save workspace to {WorkspacePath}", _workspacePath);
            throw;
        }
    }

    private static void WriteUtf8File(string path, string content)
    {
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // A stale temp file is harmless and will be overwritten on the next save.
        }
    }

    private void RefreshBackupIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (File.Exists(_workspaceBackupPath) && now - _lastBackupUtc < BackupRefreshInterval)
        {
            return;
        }

        try
        {
            File.Copy(_workspacePath, _workspaceBackupPath, overwrite: true);
            _lastBackupUtc = now;
        }
        catch (Exception backupEx)
        {
            _logger.LogWarning(backupEx, "Failed to refresh workspace backup {WorkspacePathBackup}", _workspaceBackupPath);
        }
    }

    private static WorkspaceDocument? TryLoadWorkspaceDocument(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<WorkspaceDocument>(json, FileSerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private void EnsureDefaultProbeMetadata(bool createSystemSensors)
    {
        lock (_gate)
        {
            EnsureProbeMetadataRecursive(_document.RootProbe, isRoot: true);
            if (createSystemSensors)
            {
                EnsureProbeHeartbeatSensorsRecursive(_document.RootProbe, isRoot: true);
                EnsureProbeHealthSensorsRecursive(_document.RootProbe);
            }
        }
    }

    private void EnsureDefaultDockerSecondaryProbe()
    {
        const string probeId = "probe-01";
        const string probeName = "Remote Probe 01";
        const string probeToken = "probe-01-token";

        var probe = EnumerateElements(_document.RootProbe)
            .OfType<ProbeElement>()
            .FirstOrDefault(candidate => string.Equals(candidate.ProbeId, probeId, StringComparison.OrdinalIgnoreCase));

        if (probe is null)
        {
            probe = new ProbeElement(probeName)
            {
                ProbeId = probeId,
                EnrollmentToken = probeToken,
                Description = "Local Docker secondary probe"
            };
            AddChild(_document.RootProbe, probe);
        }
        else if (string.IsNullOrWhiteSpace(probe.EnrollmentToken))
        {
            probe.EnrollmentToken = probeToken;
        }

        if (string.IsNullOrWhiteSpace(probe.Name))
        {
            probe.Name = probeName;
        }

        probe.ParentId = _document.RootProbe.Id;
        EnsureProbeMetadataRecursive(probe);
        EnsureProbeHeartbeatSensor(probe);
        EnsureProbeHealthSensor(probe);
        EnsureDefaultDockerSecondaryTestSensor(probe);
    }

    private static void EnsureDefaultDockerSecondaryTestSensor(ProbeElement probe)
    {
        const string sensorName = "Secondary -> Primary Port 8099";

        var sensor = probe.Children
            .OfType<SensorElement>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, sensorName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.SensorTypeKey, TcpPortSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase));

        if (sensor is null)
        {
            sensor = new SensorElement(sensorName, TcpPortSensorExecutor.Definition.Key, "primary")
            {
                Description = "Local Docker secondary execution test"
            };
            AddChild(probe, sensor);
        }

        sensor.ParentId = probe.Id;
        sensor.SensorTypeKey = TcpPortSensorExecutor.Definition.Key;
        sensor.Target = "primary";
        sensor.Settings.Parameters["tcp.port"] = "8099";
        sensor.Settings.Parameters["tcp.expectedOpen"] = "true";
        sensor.Settings.Timeout ??= TimeSpan.FromSeconds(3);
    }

    private void HydrateCredentialBundles(WorkspaceDocument document)
    {
        foreach (var settings in EnumerateSettings(document))
        {
            foreach (var credential in settings.Credentials)
            {
                if (credential.Values.Count > 0 || string.IsNullOrWhiteSpace(credential.ProtectedValues))
                {
                    continue;
                }

                try
                {
                    var payload = _credentialProtector.Unprotect(credential.ProtectedValues);
                    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(payload, FileSerializerOptions);
                    credential.Values = values is null
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt credential bundle {CredentialId}", credential.Id);
                    credential.Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }

    private void ProtectCredentialBundles(WorkspaceDocument document)
    {
        foreach (var settings in EnumerateSettings(document))
        {
            foreach (var credential in settings.Credentials)
            {
                try
                {
                    var payload = JsonSerializer.Serialize(credential.Values, FileSerializerOptions);
                    credential.ProtectedValues = _credentialProtector.Protect(payload);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to protect credential bundle {CredentialId}", credential.Id);
                }
            }
        }
    }

    private static IEnumerable<MonitoringSettings> EnumerateSettings(WorkspaceDocument document)
    {
        yield return document.RootProbe.Settings;

        foreach (var element in EnumerateElements(document.RootProbe))
        {
            yield return element.Settings;
        }

        foreach (var template in document.Templates)
        {
            yield return template.Settings;
        }
    }

    private void EnsureDefaultTemplates()
    {
        lock (_gate)
        {
            EnsureSmallOfficeHomeLabTemplates();
            EnsureWindowsHealthTemplate();
            EnsureSynologyNasTemplates();
            EnsureProxmoxPveTemplates();
        }
    }

    private void EnsureDefaultNotificationConfiguration()
    {
        lock (_gate)
        {
            _document.NotificationConfiguration ??= new NotificationWorkspaceConfiguration();
            _document.NotificationConfiguration.Email ??= new EmailNotificationSettings();
            _document.NotificationConfiguration.Webhook ??= new WebhookNotificationSettings();
            _document.NotificationSenders ??= [];
            _document.NotificationReceivers ??= [];
            _document.NotificationRules ??= [];

            if (_document.NotificationSenders.Count == 0)
            {
                _document.NotificationSenders.Add(CreateDefaultEmailSender(_document.NotificationConfiguration.Email));
                _document.NotificationSenders.Add(CreateDefaultWebhookSender(_document.NotificationConfiguration.Webhook));
            }

            EnsureDefaultReceivers();

            foreach (var rule in _document.NotificationRules)
            {
                if (rule.TriggerStates.Count == 0)
                {
                    rule.TriggerStates.Add(SensorState.Warning);
                    rule.TriggerStates.Add(SensorState.Critical);
                }

                if (rule.SenderId is null || _document.NotificationSenders.All(sender => sender.Id != rule.SenderId))
                {
                    rule.SenderId = ResolveSenderIdForRule(rule.ChannelKind);
                }

                if (rule.ReceiverId is null || _document.NotificationReceivers.All(receiver => receiver.Id != rule.ReceiverId))
                {
                    rule.ReceiverId = ResolveReceiverIdForRule(rule.ChannelKind, rule.Recipient);
                }

                SynchronizeLegacyRuleFields(rule);
            }
        }
    }

    private void EnsureDefaultUsers()
    {
        _document.Users ??= [];

        if (_document.Users.Count > 0)
        {
            if (!_document.Users.Any(user => user.IsEnabled && user.Role == MatmonUserRole.Admin))
            {
                _document.Users[0].Role = MatmonUserRole.Admin;
                _document.Users[0].IsEnabled = true;
                _document.Users[0].UpdatedUtc = DateTimeOffset.UtcNow;
            }

            return;
        }

        var username = string.IsNullOrWhiteSpace(_authOptions.Username) ? "admin" : _authOptions.Username.Trim();
        var password = string.IsNullOrWhiteSpace(_authOptions.Password) ? "admin" : _authOptions.Password;
        var now = DateTimeOffset.UtcNow;
        _document.Users.Add(new MatmonUser
        {
            Username = username,
            PasswordHash = MatmonPasswordHasher.Hash(password),
            Role = MatmonUserRole.Admin,
            IsEnabled = true,
            CreatedUtc = now,
            UpdatedUtc = now
        });
    }

    private void EnsureDefaultMaps(bool createStarterMap = false)
    {
        _document.Maps ??= [];

        if (_document.Maps.Count > 0 || !createStarterMap)
        {
            EnsureMapPublicTokens();
            return;
        }

        var root = _document.RootProbe;
        _document.Maps.Add(new MonitoringMap
        {
            Name = "Operations Wall",
            Description = "A starter map for wall displays and office screens.",
            DisplayPreset = MonitoringMapDisplayPreset.FullHd1080,
            PublicToken = CreateToken(),
            Columns = 12,
            Rows = 6,
            Tiles =
            [
                new MonitoringMapTile
                {
                    Kind = MonitoringMapTileKind.Status,
                    Title = "Overall status",
                    ElementId = root.Id,
                    X = 1,
                    Y = 1,
                    Width = 4,
                    Height = 2
                },
                new MonitoringMapTile
                {
                    Kind = MonitoringMapTileKind.Text,
                    Title = "Matmon Map",
                    Text = "Assign sensors, folders or probes to tiles in edit mode.",
                    X = 5,
                    Y = 1,
                    Width = 4,
                    Height = 2
                }
            ]
        });
    }

    private void EnsureMapPublicTokens()
    {
        foreach (var map in _document.Maps)
        {
            if (string.IsNullOrWhiteSpace(map.PublicToken))
            {
                map.PublicToken = CreateToken();
                map.UpdatedUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private NotificationSender CreateDefaultEmailSender(EmailNotificationSettings settings)
    {
        return new NotificationSender
        {
            Name = "Email sender",
            Kind = NotificationEndpointKind.Email,
            Email = new EmailNotificationSettings
            {
                SenderName = settings.SenderName,
                SenderEmail = settings.SenderEmail,
                SmtpHost = settings.SmtpHost,
                SmtpPort = settings.SmtpPort,
                UseSsl = settings.UseSsl,
                Username = settings.Username,
                Password = settings.Password
            }
        };
    }

    private static NotificationSender CreateDefaultWebhookSender(WebhookNotificationSettings settings)
    {
        return new NotificationSender
        {
            Name = "Webhook sender",
            Kind = NotificationEndpointKind.Webhook,
            Webhook = new WebhookNotificationSettings
            {
                EndpointUrl = settings.EndpointUrl,
                Secret = settings.Secret,
                TimeoutSeconds = settings.TimeoutSeconds
            }
        };
    }

    private void EnsureDefaultReceivers()
    {
        if (_document.NotificationReceivers.Count > 0)
        {
            return;
        }

        var emailTarget = _document.NotificationRules
            .Where(rule => rule.ChannelKind == NotificationChannelKind.Email && !string.IsNullOrWhiteSpace(rule.Recipient))
            .Select(rule => rule.Recipient.Trim())
            .FirstOrDefault();
        var webhookTarget = _document.NotificationRules
            .Where(rule => rule.ChannelKind == NotificationChannelKind.Webhook && !string.IsNullOrWhiteSpace(rule.Recipient))
            .Select(rule => rule.Recipient.Trim())
            .FirstOrDefault();

        _document.NotificationReceivers.Add(new NotificationReceiver
        {
            Name = string.IsNullOrWhiteSpace(emailTarget) ? "Email receiver" : $"Email receiver ({emailTarget})",
            Kind = NotificationEndpointKind.Email,
            Target = emailTarget ?? "ops@example.local"
        });

        _document.NotificationReceivers.Add(new NotificationReceiver
        {
            Name = string.IsNullOrWhiteSpace(webhookTarget) ? "Webhook receiver" : $"Webhook receiver ({webhookTarget})",
            Kind = NotificationEndpointKind.Webhook,
            Target = webhookTarget ?? "https://hooks.example.local/matmon"
        });
    }

    private Guid? ResolveSenderIdForRule(NotificationChannelKind channelKind)
    {
        var kind = channelKind == NotificationChannelKind.Webhook
            ? NotificationEndpointKind.Webhook
            : NotificationEndpointKind.Email;

        var sender = _document.NotificationSenders.FirstOrDefault(candidate => candidate.Kind == kind)
            ?? _document.NotificationSenders.FirstOrDefault();

        return sender?.Id;
    }

    private Guid? ResolveReceiverIdForRule(NotificationChannelKind channelKind, string recipient)
    {
        var kind = channelKind == NotificationChannelKind.Webhook
            ? NotificationEndpointKind.Webhook
            : NotificationEndpointKind.Email;

        var normalizedRecipient = recipient?.Trim() ?? string.Empty;

        var receiver = _document.NotificationReceivers.FirstOrDefault(candidate =>
            candidate.Kind == kind &&
            string.Equals(candidate.Target, normalizedRecipient, StringComparison.OrdinalIgnoreCase));

        if (receiver is not null)
        {
            return receiver.Id;
        }

        if (string.IsNullOrWhiteSpace(normalizedRecipient))
        {
            return _document.NotificationReceivers.FirstOrDefault(candidate => candidate.Kind == kind)?.Id
                ?? _document.NotificationReceivers.FirstOrDefault()?.Id;
        }

        receiver = new NotificationReceiver
        {
            Name = kind == NotificationEndpointKind.Webhook
                ? $"Webhook receiver ({normalizedRecipient})"
                : $"Email receiver ({normalizedRecipient})",
            Kind = kind,
            Target = normalizedRecipient
        };

        _document.NotificationReceivers.Add(receiver);
        return receiver.Id;
    }

    private void SynchronizeLegacyRuleFields(NotificationRule rule)
    {
        if (rule.SenderId is Guid senderId)
        {
            var sender = _document.NotificationSenders.FirstOrDefault(candidate => candidate.Id == senderId);
            if (sender is not null)
            {
                rule.ChannelKind = sender.Kind == NotificationEndpointKind.Webhook
                    ? NotificationChannelKind.Webhook
                    : NotificationChannelKind.Email;
            }
        }

        if (rule.ReceiverId is Guid receiverId)
        {
            var receiver = _document.NotificationReceivers.FirstOrDefault(candidate => candidate.Id == receiverId);
            if (receiver is not null)
            {
                rule.Recipient = receiver.Target;
            }
        }
    }

    private void EnsureDefaultAlertCollection()
    {
        lock (_gate)
        {
            _document.Alerts ??= [];
        }
    }

    private void MigrateDocumentTelemetryIntoRepository()
    {
        _document.SensorHistory ??= [];
        _document.Events ??= [];
        _document.SensorStatistics ??= [];

        var hasDocumentTelemetry = _document.SensorHistory.Count > 0
            || _document.Events.Count > 0
            || _document.SensorStatistics.Count > 0;

        if (hasDocumentTelemetry && _telemetry.GetCounts().Total == 0)
        {
            _telemetry.ReplaceAllObservations(_document.SensorHistory);
            _telemetry.ReplaceAllEvents(_document.Events);
            _telemetry.ReplaceAllStatistics(_document.SensorStatistics);
            _logger.LogInformation(
                "Migrated telemetry from workspace into the telemetry database: {Observations} observations, {Events} events, {Statistics} statistics buckets",
                _document.SensorHistory.Count,
                _document.Events.Count,
                _document.SensorStatistics.Count);
        }

        // Telemetry now lives in the repository; never serialize it back into workspace.json.
        _document.SensorHistory = [];
        _document.Events = [];
        _document.SensorStatistics = [];
    }

    private void EnsureSensorDefinitionCatalog()
    {
        lock (_gate)
        {
            var builtIns = new[]
            {
                PingSensorExecutor.Definition,
                HttpSensorExecutor.Definition,
                HttpAdvancedSensorExecutor.Definition,
                SnmpSensorExecutor.Definition,
                SynologyNasSensorExecutor.Definition,
                SynologyHealthSensorExecutor.Definition,
                SnmpInterfaceSensorExecutor.Definition,
                UpsSnmpSensorExecutor.Definition,
                ProxmoxPveSensorExecutor.Definition,
                PowerShellRemoteSensorExecutor.Definition,
                WindowsServiceSensorExecutor.Definition,
                WindowsProcessSensorExecutor.Definition,
                LinuxSshHealthSensorExecutor.Definition,
                SslCertificateSensorExecutor.Definition,
                CertificateChainSensorExecutor.Definition,
                MssqlSensorExecutor.Definition,
                TcpPortSensorExecutor.Definition,
                DnsSensorExecutor.Definition,
                NtpSensorExecutor.Definition,
                DockerContainerSensorExecutor.Definition,
                BackupJobSensorExecutor.Definition,
                DiskSmartSensorExecutor.Definition,
                ProbeHeartbeatSensorExecutor.Definition,
                ProbeHealthSensorExecutor.Definition
            };

            var merged = new List<SensorDefinition>(_document.SensorDefinitions.Count + builtIns.Length);
            var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var builtIn in builtIns)
            {
                merged.Add(CloneSensorDefinition(builtIn));
                addedKeys.Add(builtIn.Key);
            }

            foreach (var definition in _document.SensorDefinitions)
            {
                if (addedKeys.Contains(definition.Key))
                {
                    continue;
                }

                merged.Add(CloneSensorDefinition(definition));
            }

            _document.SensorDefinitions = merged;
        }
    }

    private void EnsureWindowsHealthTemplate()
    {
        const string templateKey = "windows-health";
        const string templateName = "Windows Health";
        var windowsHostTemplate = _document.Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, "windows-host-defaults", StringComparison.OrdinalIgnoreCase));

        var template = _document.Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, templateKey, StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(candidate.Key) &&
             string.Equals(candidate.Name, templateName, StringComparison.OrdinalIgnoreCase) &&
             candidate.TargetKind == MonitoringTemplateScope.Sensor));

        if (template is null)
        {
            template = new MonitoringTemplate
            {
                Key = templateKey,
                Name = templateName,
                TargetKind = MonitoringTemplateScope.Sensor,
                SensorTypeKey = PowerShellRemoteSensorExecutor.Definition.Key
            };
            _document.Templates.Add(template);
        }
        else if (string.IsNullOrWhiteSpace(template.Key))
        {
            template.Key = templateKey;
        }

        template.TargetKind = MonitoringTemplateScope.Sensor;
        template.SensorTypeKey = string.IsNullOrWhiteSpace(template.SensorTypeKey)
            ? PowerShellRemoteSensorExecutor.Definition.Key
            : template.SensorTypeKey;
        template.ParentTemplateId ??= windowsHostTemplate?.Id;
        template.Settings.Enabled ??= true;
        SetDefaultPollingInterval(template.Settings, TimeSpan.FromSeconds(30));
        template.Settings.Timeout ??= TimeSpan.FromSeconds(30);
        template.Settings.DefaultChannelKey ??= "cpuLoad";

        template.Settings.Parameters["outputFormat"] = "json";
        template.Settings.Parameters["defaultChannelKey"] = "cpuLoad";
        template.Settings.Parameters["script"] = """
$ErrorActionPreference = 'SilentlyContinue'
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average | Select-Object -ExpandProperty Average
$totalMemoryMb = [double]$os.TotalVisibleMemorySize / 1024
$freeMemoryMb = [double]$os.FreePhysicalMemory / 1024
$memoryUsedPercent = if ($totalMemoryMb -gt 0) { (($totalMemoryMb - $freeMemoryMb) / $totalMemoryMb) * 100 } else { 0 }

$systemDriveId = $env:SystemDrive
if ([string]::IsNullOrWhiteSpace($systemDriveId)) { $systemDriveId = 'C:' }
$systemDriveId = $systemDriveId.Replace('\', '')
$systemDrive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$systemDriveId'"
$systemDriveFreeGb = if ($systemDrive) { [double]$systemDrive.FreeSpace / 1GB } else { 0 }
$systemDriveSizeGb = if ($systemDrive) { [double]$systemDrive.Size / 1GB } else { 0 }
$systemDriveFreePercent = if ($systemDriveSizeGb -gt 0) { ($systemDriveFreeGb / $systemDriveSizeGb) * 100 } else { 0 }

$lastHotfix = Get-HotFix | Where-Object InstalledOn | Sort-Object InstalledOn -Descending | Select-Object -First 1
$daysSinceLastUpdate = if ($lastHotfix -and $lastHotfix.InstalledOn) {
    [math]::Round(((Get-Date) - $lastHotfix.InstalledOn).TotalDays, 2)
} else {
    -1
}

$pendingUpdates = -1
try {
    $updateSession = New-Object -ComObject Microsoft.Update.Session
    $updateSearcher = $updateSession.CreateUpdateSearcher()
    $pendingUpdates = $updateSearcher.Search("IsInstalled=0 and Type='Software'").Updates.Count
} catch {
    $pendingUpdates = -1
}

$diskHealthOk = 1
try {
    $physicalDisks = Get-PhysicalDisk
    if ($physicalDisks) {
        $diskHealthOk = @($physicalDisks | Where-Object { $_.HealthStatus -notin @('Healthy', 'OK') }).Count -eq 0
        $diskHealthOk = if ($diskHealthOk) { 1 } else { 0 }
    }
} catch {
    $diskHealthOk = 1
}

$rebootPending = 0
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $rebootPending = 1 }
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { $rebootPending = 1 }
try {
    $sessionManager = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
    if ($sessionManager.PendingFileRenameOperations) { $rebootPending = 1 }
} catch {}

[pscustomobject]@{
    cpuLoad = [math]::Round([double]$cpu, 2)
    memoryUsedPercent = [math]::Round([double]$memoryUsedPercent, 2)
    memoryFreeMb = [math]::Round([double]$freeMemoryMb, 2)
    systemDriveFreePercent = [math]::Round([double]$systemDriveFreePercent, 2)
    systemDriveFreeGb = [math]::Round([double]$systemDriveFreeGb, 2)
    daysSinceLastUpdate = [math]::Round([double]$daysSinceLastUpdate, 2)
    pendingUpdates = [double]$pendingUpdates
    diskHealthOk = [double]$diskHealthOk
    rebootPending = [double]$rebootPending
}
""";

        SetDefaultChannelThreshold(template.Settings, "cpuLoad", "warning", new ThresholdRule(ThresholdDirection.Above, 85));
        SetDefaultChannelThreshold(template.Settings, "cpuLoad", "critical", new ThresholdRule(ThresholdDirection.Above, 95));
        SetDefaultChannelThreshold(template.Settings, "memoryUsedPercent", "warning", new ThresholdRule(ThresholdDirection.Above, 85));
        SetDefaultChannelThreshold(template.Settings, "memoryUsedPercent", "critical", new ThresholdRule(ThresholdDirection.Above, 95));
        SetDefaultChannelThreshold(template.Settings, "systemDriveFreePercent", "warning", new ThresholdRule(ThresholdDirection.Below, 15));
        SetDefaultChannelThreshold(template.Settings, "systemDriveFreePercent", "critical", new ThresholdRule(ThresholdDirection.Below, 8));
        SetDefaultChannelThreshold(template.Settings, "daysSinceLastUpdate", "warning", new ThresholdRule(ThresholdDirection.Above, 35));
        SetDefaultChannelThreshold(template.Settings, "daysSinceLastUpdate", "critical", new ThresholdRule(ThresholdDirection.Above, 60));
        SetDefaultOrMigrateChannelThreshold(template.Settings, "pendingUpdates", "warning", new ThresholdRule(ThresholdDirection.Above, 0.5), "above:0");
        SetDefaultChannelThreshold(template.Settings, "pendingUpdates", "critical", new ThresholdRule(ThresholdDirection.Above, 10));
        SetDefaultOrMigrateChannelThreshold(template.Settings, "diskHealthOk", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5), "below:1");
        SetDefaultOrMigrateChannelThreshold(template.Settings, "rebootPending", "warning", new ThresholdRule(ThresholdDirection.Above, 0.5), "above:0");
    }

    private void EnsureSmallOfficeHomeLabTemplates()
    {
        var baseline = EnsureTemplate("small-office-baseline", "Small Office Baseline", MonitoringTemplateScope.Any);
        baseline.Settings.Enabled ??= true;
        SetDefaultPollingInterval(baseline.Settings, TimeSpan.FromMinutes(1));
        baseline.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        baseline.Settings.RetryCount ??= 1;
        baseline.Settings.EventRetentionDays ??= 30;
        baseline.Settings.ObservationRetentionDays ??= 14;
        baseline.Settings.StatisticsRetentionDays ??= 365;
        baseline.Settings.StatisticsBucketMinutes ??= 60;
        SetDefaultParameter(baseline.Settings, "profile", "small-office-home-lab");

        var networkDevice = EnsureTemplate(
            "network-device-defaults",
            "Network Device Defaults",
            MonitoringTemplateScope.Host,
            parentTemplateId: baseline.Id);
        networkDevice.Settings.Enabled ??= true;
        SetDefaultPollingInterval(networkDevice.Settings, TimeSpan.FromMinutes(1));
        networkDevice.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        SetDefaultParameter(networkDevice.Settings, "snmp.community", "public");
        SetDefaultParameter(networkDevice.Settings, "snmp.version", "v2c");
        SetDefaultParameter(networkDevice.Settings, "snmp.port", "161");

        var windowsHost = EnsureTemplate(
            "windows-host-defaults",
            "Windows Host Defaults",
            MonitoringTemplateScope.Host,
            parentTemplateId: baseline.Id);
        windowsHost.Settings.Enabled ??= true;
        SetDefaultPollingInterval(windowsHost.Settings, TimeSpan.FromMinutes(1));
        windowsHost.Settings.Timeout ??= TimeSpan.FromSeconds(10);
        SetDefaultParameter(windowsHost.Settings, "winrm.port", "5985");
        SetDefaultParameter(windowsHost.Settings, "winrm.useSsl", "false");
        SetDefaultParameter(windowsHost.Settings, "winrm.configurationName", "Microsoft.PowerShell");

        var ping = EnsureTemplate(
            "ping-availability",
            "Ping - Availability",
            MonitoringTemplateScope.Sensor,
            PingSensorExecutor.Definition.Key,
            baseline.Id);
        ping.Settings.Enabled ??= true;
        SetDefaultPollingInterval(ping.Settings, TimeSpan.FromSeconds(30));
        ping.Settings.Timeout ??= TimeSpan.FromSeconds(2);
        ping.Settings.DefaultChannelKey ??= "latency";
        SetDefaultParameter(ping.Settings, "payloadSize", "32");
        SetDefaultParameter(ping.Settings, "dontFragment", "false");
        SetDefaultChannelThreshold(ping.Settings, "latency", "warning", new ThresholdRule(ThresholdDirection.Above, 80));
        SetDefaultChannelThreshold(ping.Settings, "latency", "critical", new ThresholdRule(ThresholdDirection.Above, 200));

        var http = EnsureTemplate(
            "http-web-endpoint",
            "HTTP - Web Endpoint",
            MonitoringTemplateScope.Sensor,
            HttpSensorExecutor.Definition.Key,
            baseline.Id);
        http.Settings.Enabled ??= true;
        SetDefaultPollingInterval(http.Settings, TimeSpan.FromMinutes(1));
        http.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        http.Settings.DefaultChannelKey ??= "latency";
        SetDefaultParameter(http.Settings, "method", "GET");
        SetDefaultParameter(http.Settings, "expectedStatus", "200");
        SetDefaultChannelThreshold(http.Settings, "latency", "warning", new ThresholdRule(ThresholdDirection.Above, 500));
        SetDefaultChannelThreshold(http.Settings, "latency", "critical", new ThresholdRule(ThresholdDirection.Above, 2000));

        var httpAdvanced = EnsureTemplate(
            "http-advanced-extraction",
            "HTTP Advanced - Extraction",
            MonitoringTemplateScope.Sensor,
            HttpAdvancedSensorExecutor.Definition.Key,
            baseline.Id);
        httpAdvanced.Settings.Enabled ??= true;
        SetDefaultPollingInterval(httpAdvanced.Settings, TimeSpan.FromMinutes(1));
        httpAdvanced.Settings.Timeout ??= TimeSpan.FromSeconds(10);
        httpAdvanced.Settings.DefaultChannelKey ??= "latency";
        SetDefaultParameter(httpAdvanced.Settings, "method", "GET");
        SetDefaultParameter(httpAdvanced.Settings, "expectedStatus", "200");
        SetDefaultParameter(httpAdvanced.Settings, "extractMode", "none");
        SetDefaultChannelThreshold(httpAdvanced.Settings, "latency", "warning", new ThresholdRule(ThresholdDirection.Above, 1000));
        SetDefaultChannelThreshold(httpAdvanced.Settings, "latency", "critical", new ThresholdRule(ThresholdDirection.Above, 3000));

        var sslCertificate = EnsureTemplate(
            "ssl-certificate-30-7",
            "SSL Certificate - 30/7 Days",
            MonitoringTemplateScope.Sensor,
            SslCertificateSensorExecutor.Definition.Key,
            baseline.Id);
        sslCertificate.Settings.Enabled ??= true;
        SetDefaultDailySchedule(sslCertificate.Settings, TimeSpan.FromHours(6));
        sslCertificate.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        sslCertificate.Settings.DefaultChannelKey ??= "remainingDays";
        SetDefaultParameter(sslCertificate.Settings, "ssl.port", "443");
        SetDefaultParameter(sslCertificate.Settings, "ssl.warningDays", "30");
        SetDefaultParameter(sslCertificate.Settings, "ssl.criticalDays", "7");
        SetDefaultChannelThreshold(sslCertificate.Settings, "remainingDays", "warning", new ThresholdRule(ThresholdDirection.Below, 30));
        SetDefaultChannelThreshold(sslCertificate.Settings, "remainingDays", "critical", new ThresholdRule(ThresholdDirection.Below, 7));
        SetDefaultChannelThreshold(sslCertificate.Settings, "valid", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));

        var certificateChain = EnsureTemplate(
            "certificate-chain-validation",
            "Certificate Chain - Validation",
            MonitoringTemplateScope.Sensor,
            CertificateChainSensorExecutor.Definition.Key,
            baseline.Id);
        certificateChain.Settings.Enabled ??= true;
        SetDefaultDailySchedule(certificateChain.Settings, TimeSpan.FromHours(6));
        certificateChain.Settings.Timeout ??= TimeSpan.FromSeconds(8);
        certificateChain.Settings.DefaultChannelKey ??= "valid";
        SetDefaultParameter(certificateChain.Settings, "cert.port", "443");
        SetDefaultParameter(certificateChain.Settings, "cert.checkRevocation", "false");
        SetDefaultChannelThreshold(certificateChain.Settings, "valid", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(certificateChain.Settings, "hostnameMatch", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(certificateChain.Settings, "chainErrors", "critical", new ThresholdRule(ThresholdDirection.Above, 0.5));
        SetDefaultChannelThreshold(certificateChain.Settings, "remainingDays", "warning", new ThresholdRule(ThresholdDirection.Below, 30));
        SetDefaultChannelThreshold(certificateChain.Settings, "remainingDays", "critical", new ThresholdRule(ThresholdDirection.Below, 7));

        var tcpGeneric = EnsureTemplate(
            "tcp-port-generic",
            "TCP Port - Generic",
            MonitoringTemplateScope.Sensor,
            TcpPortSensorExecutor.Definition.Key,
            baseline.Id);
        tcpGeneric.Settings.Enabled ??= true;
        SetDefaultPollingInterval(tcpGeneric.Settings, TimeSpan.FromMinutes(1));
        tcpGeneric.Settings.Timeout ??= TimeSpan.FromSeconds(3);
        tcpGeneric.Settings.DefaultChannelKey ??= "connectMs";
        SetDefaultParameter(tcpGeneric.Settings, "tcp.expectedOpen", "true");
        SetDefaultChannelThreshold(tcpGeneric.Settings, "open", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(tcpGeneric.Settings, "connectMs", "warning", new ThresholdRule(ThresholdDirection.Above, 500));
        SetDefaultChannelThreshold(tcpGeneric.Settings, "connectMs", "critical", new ThresholdRule(ThresholdDirection.Above, 2000));

        var tcpSsh = EnsureTemplate(
            "tcp-port-ssh",
            "TCP Port - SSH",
            MonitoringTemplateScope.Sensor,
            TcpPortSensorExecutor.Definition.Key,
            tcpGeneric.Id);
        SetDefaultParameter(tcpSsh.Settings, "tcp.port", "22");

        var tcpRdp = EnsureTemplate(
            "tcp-port-rdp",
            "TCP Port - RDP",
            MonitoringTemplateScope.Sensor,
            TcpPortSensorExecutor.Definition.Key,
            tcpGeneric.Id);
        SetDefaultParameter(tcpRdp.Settings, "tcp.port", "3389");

        var snmpUptime = EnsureTemplate(
            "snmp-uptime",
            "SNMP - Uptime",
            MonitoringTemplateScope.Sensor,
            SnmpSensorExecutor.Definition.Key,
            networkDevice.Id);
        snmpUptime.Settings.Enabled ??= true;
        SetDefaultPollingInterval(snmpUptime.Settings, TimeSpan.FromMinutes(5));
        snmpUptime.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        snmpUptime.Settings.DefaultChannelKey ??= "uptime";
        SetDefaultParameter(snmpUptime.Settings, "snmp.oids", "1.3.6.1.2.1.1.3.0|Uptime");

        var snmpInterface = EnsureTemplate(
            "snmp-interface-basic",
            "SNMP - Interface",
            MonitoringTemplateScope.Sensor,
            SnmpInterfaceSensorExecutor.Definition.Key,
            networkDevice.Id);
        snmpInterface.Settings.Enabled ??= true;
        SetDefaultPollingInterval(snmpInterface.Settings, TimeSpan.FromMinutes(1));
        snmpInterface.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        snmpInterface.Settings.DefaultChannelKey ??= "oper_status";
        SetDefaultParameter(snmpInterface.Settings, "snmp.interfaceIndex", "1");
        SetDefaultChannelThreshold(snmpInterface.Settings, "oper_status", "critical", new ThresholdRule(ThresholdDirection.Above, 1.5));
        SetDefaultChannelThreshold(snmpInterface.Settings, "in_errors", "warning", new ThresholdRule(ThresholdDirection.Above, 0.5));
        SetDefaultChannelThreshold(snmpInterface.Settings, "out_errors", "warning", new ThresholdRule(ThresholdDirection.Above, 0.5));

        var ups = EnsureTemplate(
            "ups-snmp-basic",
            "UPS - SNMP",
            MonitoringTemplateScope.Sensor,
            UpsSnmpSensorExecutor.Definition.Key,
            networkDevice.Id);
        ups.Settings.Enabled ??= true;
        SetDefaultPollingInterval(ups.Settings, TimeSpan.FromMinutes(1));
        ups.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        ups.Settings.DefaultChannelKey ??= "battery_charge";
        SetDefaultChannelThreshold(ups.Settings, "battery_charge", "warning", new ThresholdRule(ThresholdDirection.Below, 50));
        SetDefaultChannelThreshold(ups.Settings, "battery_charge", "critical", new ThresholdRule(ThresholdDirection.Below, 20));
        SetDefaultChannelThreshold(ups.Settings, "runtime_minutes", "warning", new ThresholdRule(ThresholdDirection.Below, 15));
        SetDefaultChannelThreshold(ups.Settings, "runtime_minutes", "critical", new ThresholdRule(ThresholdDirection.Below, 5));
        SetDefaultChannelThreshold(ups.Settings, "load_percent", "warning", new ThresholdRule(ThresholdDirection.Above, 80));
        SetDefaultChannelThreshold(ups.Settings, "load_percent", "critical", new ThresholdRule(ThresholdDirection.Above, 95));

        var mssql = EnsureTemplate(
            "mssql-query-value",
            "MSSQL - Query Value",
            MonitoringTemplateScope.Sensor,
            MssqlSensorExecutor.Definition.Key,
            baseline.Id);
        mssql.Settings.Enabled ??= true;
        SetDefaultPollingInterval(mssql.Settings, TimeSpan.FromMinutes(5));
        mssql.Settings.Timeout ??= TimeSpan.FromSeconds(10);
        mssql.Settings.DefaultChannelKey ??= "value";
        SetDefaultParameter(mssql.Settings, "mssql.database", "master");
        SetDefaultParameter(mssql.Settings, "mssql.port", "1433");
        SetDefaultParameter(mssql.Settings, "mssql.encrypt", "true");
        SetDefaultParameter(mssql.Settings, "mssql.trustServerCertificate", "true");
        SetDefaultParameter(mssql.Settings, "defaultChannelKey", "value");
        SetDefaultParameter(mssql.Settings, "query", "SELECT CAST(1 AS float) AS value;");
        SetDefaultChannelThreshold(mssql.Settings, "value", "critical", new ThresholdRule(ThresholdDirection.Below, 1));

        var dns = EnsureTemplate(
            "dns-resolution",
            "DNS - Resolution",
            MonitoringTemplateScope.Sensor,
            DnsSensorExecutor.Definition.Key,
            baseline.Id);
        dns.Settings.Enabled ??= true;
        SetDefaultPollingInterval(dns.Settings, TimeSpan.FromMinutes(1));
        dns.Settings.Timeout ??= TimeSpan.FromSeconds(3);
        dns.Settings.DefaultChannelKey ??= "resolveMs";
        SetDefaultParameter(dns.Settings, "dns.recordType", "A");
        SetDefaultParameter(dns.Settings, "dns.port", "53");
        SetDefaultChannelThreshold(dns.Settings, "resolveMs", "warning", new ThresholdRule(ThresholdDirection.Above, 100));
        SetDefaultChannelThreshold(dns.Settings, "resolveMs", "critical", new ThresholdRule(ThresholdDirection.Above, 500));
        SetDefaultChannelThreshold(dns.Settings, "recordCount", "critical", new ThresholdRule(ThresholdDirection.Below, 1));
        SetDefaultChannelThreshold(dns.Settings, "expectedMatched", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));

        var ntp = EnsureTemplate(
            "ntp-time-offset",
            "NTP - Time Offset",
            MonitoringTemplateScope.Sensor,
            NtpSensorExecutor.Definition.Key,
            baseline.Id);
        ntp.Settings.Enabled ??= true;
        SetDefaultPollingInterval(ntp.Settings, TimeSpan.FromMinutes(5));
        ntp.Settings.Timeout ??= TimeSpan.FromSeconds(3);
        ntp.Settings.DefaultChannelKey ??= "absoluteOffsetMs";
        SetDefaultParameter(ntp.Settings, "ntp.port", "123");
        SetDefaultChannelThreshold(ntp.Settings, "absoluteOffsetMs", "warning", new ThresholdRule(ThresholdDirection.Above, 100));
        SetDefaultChannelThreshold(ntp.Settings, "absoluteOffsetMs", "critical", new ThresholdRule(ThresholdDirection.Above, 1000));
        SetDefaultChannelThreshold(ntp.Settings, "delayMs", "warning", new ThresholdRule(ThresholdDirection.Above, 500));
        SetDefaultChannelThreshold(ntp.Settings, "reachable", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));

        var linuxHealth = EnsureTemplate(
            "linux-ssh-health",
            "Linux SSH Health",
            MonitoringTemplateScope.Sensor,
            LinuxSshHealthSensorExecutor.Definition.Key,
            baseline.Id);
        linuxHealth.Settings.Enabled ??= true;
        SetDefaultPollingInterval(linuxHealth.Settings, TimeSpan.FromMinutes(1));
        linuxHealth.Settings.Timeout ??= TimeSpan.FromSeconds(10);
        linuxHealth.Settings.DefaultChannelKey ??= "load1";
        SetDefaultParameter(linuxHealth.Settings, "ssh.port", "22");
        SetDefaultChannelThreshold(linuxHealth.Settings, "load1", "warning", new ThresholdRule(ThresholdDirection.Above, 2));
        SetDefaultChannelThreshold(linuxHealth.Settings, "load1", "critical", new ThresholdRule(ThresholdDirection.Above, 5));
        SetDefaultChannelThreshold(linuxHealth.Settings, "memoryUsedPercent", "warning", new ThresholdRule(ThresholdDirection.Above, 85));
        SetDefaultChannelThreshold(linuxHealth.Settings, "memoryUsedPercent", "critical", new ThresholdRule(ThresholdDirection.Above, 95));
        SetDefaultChannelThreshold(linuxHealth.Settings, "rootUsedPercent", "warning", new ThresholdRule(ThresholdDirection.Above, 85));
        SetDefaultChannelThreshold(linuxHealth.Settings, "rootUsedPercent", "critical", new ThresholdRule(ThresholdDirection.Above, 95));

        var dockerContainer = EnsureTemplate(
            "docker-container-running",
            "Docker Container - Running",
            MonitoringTemplateScope.Sensor,
            DockerContainerSensorExecutor.Definition.Key,
            baseline.Id);
        dockerContainer.Settings.Enabled ??= true;
        SetDefaultPollingInterval(dockerContainer.Settings, TimeSpan.FromMinutes(1));
        dockerContainer.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        dockerContainer.Settings.DefaultChannelKey ??= "running";
        SetDefaultParameter(dockerContainer.Settings, "docker.socket", "/var/run/docker.sock");
        SetDefaultChannelThreshold(dockerContainer.Settings, "running", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(dockerContainer.Settings, "healthOk", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));

        var windowsService = EnsureTemplate(
            "windows-service-running",
            "Windows Service - Running",
            MonitoringTemplateScope.Sensor,
            WindowsServiceSensorExecutor.Definition.Key,
            windowsHost.Id);
        windowsService.Settings.Enabled ??= true;
        SetDefaultPollingInterval(windowsService.Settings, TimeSpan.FromMinutes(1));
        windowsService.Settings.Timeout ??= TimeSpan.FromSeconds(15);
        windowsService.Settings.DefaultChannelKey ??= "stateOk";
        SetDefaultParameter(windowsService.Settings, "windows.serviceExpectedState", "Running");
        SetDefaultChannelThreshold(windowsService.Settings, "stateOk", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(windowsService.Settings, "exists", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));

        var windowsProcess = EnsureTemplate(
            "windows-process-count",
            "Windows Process - Count",
            MonitoringTemplateScope.Sensor,
            WindowsProcessSensorExecutor.Definition.Key,
            windowsHost.Id);
        windowsProcess.Settings.Enabled ??= true;
        SetDefaultPollingInterval(windowsProcess.Settings, TimeSpan.FromMinutes(1));
        windowsProcess.Settings.Timeout ??= TimeSpan.FromSeconds(15);
        windowsProcess.Settings.DefaultChannelKey ??= "processCount";
        SetDefaultParameter(windowsProcess.Settings, "windows.processMinCount", "1");
        SetDefaultChannelThreshold(windowsProcess.Settings, "countOk", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));

        var backupJob = EnsureTemplate(
            "backup-job-windows",
            "Backup Job - Windows",
            MonitoringTemplateScope.Sensor,
            BackupJobSensorExecutor.Definition.Key,
            windowsHost.Id);
        backupJob.Settings.Enabled ??= true;
        SetDefaultDailySchedule(backupJob.Settings, TimeSpan.FromHours(7));
        backupJob.Settings.Timeout ??= TimeSpan.FromSeconds(30);
        backupJob.Settings.DefaultChannelKey ??= "success";
        SetDefaultParameter(backupJob.Settings, "backup.mode", "windows-eventlog");
        SetDefaultParameter(backupJob.Settings, "backup.lookbackHours", "48");
        SetDefaultChannelThreshold(backupJob.Settings, "success", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(backupJob.Settings, "ageHours", "warning", new ThresholdRule(ThresholdDirection.Above, 36));
        SetDefaultChannelThreshold(backupJob.Settings, "ageHours", "critical", new ThresholdRule(ThresholdDirection.Above, 48));
        SetDefaultChannelThreshold(backupJob.Settings, "failedEvents", "warning", new ThresholdRule(ThresholdDirection.Above, 0.5));

        var diskSmart = EnsureTemplate(
            "disk-smart-windows",
            "Disk SMART - Windows",
            MonitoringTemplateScope.Sensor,
            DiskSmartSensorExecutor.Definition.Key,
            windowsHost.Id);
        diskSmart.Settings.Enabled ??= true;
        SetDefaultPollingInterval(diskSmart.Settings, TimeSpan.FromMinutes(30));
        diskSmart.Settings.Timeout ??= TimeSpan.FromSeconds(20);
        diskSmart.Settings.DefaultChannelKey ??= "healthy";
        SetDefaultChannelThreshold(diskSmart.Settings, "healthy", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(diskSmart.Settings, "unhealthyDisks", "critical", new ThresholdRule(ThresholdDirection.Above, 0.5));

        var heartbeat = EnsureTemplate(
            "probe-heartbeat-default",
            "Probe Heartbeat",
            MonitoringTemplateScope.Sensor,
            ProbeHeartbeatSensorExecutor.Definition.Key,
            baseline.Id);
        heartbeat.Settings.Enabled ??= true;
        SetDefaultPollingInterval(heartbeat.Settings, TimeSpan.FromSeconds(30));
        heartbeat.Settings.Timeout ??= TimeSpan.FromSeconds(2);
        heartbeat.Settings.DefaultChannelKey ??= "ageSeconds";
        SetDefaultChannelThreshold(heartbeat.Settings, "ageSeconds", "warning", new ThresholdRule(ThresholdDirection.Above, 45));
        SetDefaultChannelThreshold(heartbeat.Settings, "ageSeconds", "critical", new ThresholdRule(ThresholdDirection.Above, 90));
    }

    private void EnsureSynologyNasTemplates()
    {
        const string hostTemplateKey = "synology-nas-host-defaults";
        const string hostTemplateName = "Synology NAS Defaults";
        const string sensorTemplateKey = "synology-nas";
        const string sensorTemplateName = "Synology NAS";
        var networkDeviceTemplate = _document.Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, "network-device-defaults", StringComparison.OrdinalIgnoreCase));

        var hostTemplate = EnsureTemplate(
            hostTemplateKey,
            hostTemplateName,
            MonitoringTemplateScope.Host,
            parentTemplateId: networkDeviceTemplate?.Id);
        hostTemplate.Settings.Enabled ??= true;
        SetDefaultPollingInterval(hostTemplate.Settings, TimeSpan.FromSeconds(30));
        hostTemplate.Settings.Timeout ??= TimeSpan.FromSeconds(10);
        SetDefaultParameter(hostTemplate.Settings, "snmp.community", "public");
        SetDefaultParameter(hostTemplate.Settings, "snmp.version", "v2c");
        SetDefaultParameter(hostTemplate.Settings, "snmp.port", "161");

        var sensorTemplate = EnsureTemplate(
            sensorTemplateKey,
            sensorTemplateName,
            MonitoringTemplateScope.Sensor,
            SynologyNasSensorExecutor.Definition.Key,
            hostTemplate.Id);
        sensorTemplate.Settings.Enabled ??= true;
        SetDefaultPollingInterval(sensorTemplate.Settings, TimeSpan.FromSeconds(30));
        sensorTemplate.Settings.Timeout ??= TimeSpan.FromSeconds(10);
        sensorTemplate.Settings.DefaultChannelKey ??= "uptime";
        SetDefaultParameter(sensorTemplate.Settings, "snmp.oids", "1.3.6.1.2.1.1.3.0|Uptime");

        var healthTemplate = EnsureTemplate(
            "synology-health",
            "Synology Health",
            MonitoringTemplateScope.Sensor,
            SynologyHealthSensorExecutor.Definition.Key,
            hostTemplate.Id);
        healthTemplate.Settings.Enabled ??= true;
        SetDefaultPollingInterval(healthTemplate.Settings, TimeSpan.FromSeconds(30));
        healthTemplate.Settings.Timeout ??= TimeSpan.FromSeconds(10);
        healthTemplate.Settings.DefaultChannelKey ??= "cpuUtilization";
        SetDefaultChannelThreshold(healthTemplate.Settings, "cpuUtilization", "warning", new ThresholdRule(ThresholdDirection.Above, 85));
        SetDefaultChannelThreshold(healthTemplate.Settings, "cpuUtilization", "critical", new ThresholdRule(ThresholdDirection.Above, 95));
        SetDefaultChannelThreshold(healthTemplate.Settings, "memoryUtilization", "warning", new ThresholdRule(ThresholdDirection.Above, 85));
        SetDefaultChannelThreshold(healthTemplate.Settings, "memoryUtilization", "critical", new ThresholdRule(ThresholdDirection.Above, 95));
        SetDefaultChannelThreshold(healthTemplate.Settings, "storageFreePercent", "warning", new ThresholdRule(ThresholdDirection.Below, 15));
        SetDefaultChannelThreshold(healthTemplate.Settings, "storageFreePercent", "critical", new ThresholdRule(ThresholdDirection.Below, 8));
        SetDefaultChannelThreshold(healthTemplate.Settings, "diskWarningCount", "warning", new ThresholdRule(ThresholdDirection.Above, 0.5));
        SetDefaultChannelThreshold(healthTemplate.Settings, "diskCriticalCount", "critical", new ThresholdRule(ThresholdDirection.Above, 0.5));
        SetDefaultChannelThreshold(healthTemplate.Settings, "diskFailingCount", "critical", new ThresholdRule(ThresholdDirection.Above, 0.5));
        SetDefaultChannelThreshold(healthTemplate.Settings, "raidWarningCount", "warning", new ThresholdRule(ThresholdDirection.Above, 0.5));
        SetDefaultChannelThreshold(healthTemplate.Settings, "raidDegradedCount", "warning", new ThresholdRule(ThresholdDirection.Above, 0.5));
        SetDefaultChannelThreshold(healthTemplate.Settings, "raidCrashedCount", "critical", new ThresholdRule(ThresholdDirection.Above, 0.5));
        SetDefaultChannelThreshold(healthTemplate.Settings, "systemStatusOk", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(healthTemplate.Settings, "powerStatusOk", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(healthTemplate.Settings, "systemFanStatusOk", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(healthTemplate.Settings, "cpuFanStatusOk", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(healthTemplate.Settings, "thermalStatusOk", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
    }

    private void EnsureProxmoxPveTemplates()
    {
        const string hostTemplateKey = "proxmox-pve-host-defaults";
        const string hostTemplateName = "Proxmox PVE Defaults";
        const string clusterTemplateKey = "proxmox-pve-cluster";
        const string clusterTemplateName = "Proxmox PVE Cluster";
        const string nodeTemplateKey = "proxmox-pve-node";
        const string nodeTemplateName = "Proxmox PVE Node";
        var baselineTemplate = _document.Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, "small-office-baseline", StringComparison.OrdinalIgnoreCase));

        var hostTemplate = EnsureTemplate(
            hostTemplateKey,
            hostTemplateName,
            MonitoringTemplateScope.Host,
            parentTemplateId: baselineTemplate?.Id);
        hostTemplate.Settings.Enabled ??= true;
        SetDefaultPollingInterval(hostTemplate.Settings, TimeSpan.FromSeconds(20));
        hostTemplate.Settings.Timeout ??= TimeSpan.FromSeconds(10);
        SetDefaultParameter(hostTemplate.Settings, "pve.port", "8006");
        SetDefaultParameter(hostTemplate.Settings, "pve.user", "root@pam");
        SetDefaultParameter(hostTemplate.Settings, "pve.tokenId", "monitoring");
        SetDefaultParameter(hostTemplate.Settings, "pve.verifySsl", "false");

        var clusterTemplate = EnsureTemplate(
            clusterTemplateKey,
            clusterTemplateName,
            MonitoringTemplateScope.Sensor,
            ProxmoxPveSensorExecutor.Definition.Key,
            hostTemplate.Id);
        clusterTemplate.Settings.Enabled ??= true;
        SetDefaultPollingInterval(clusterTemplate.Settings, TimeSpan.FromSeconds(20));
        clusterTemplate.Settings.Timeout ??= TimeSpan.FromSeconds(10);
        clusterTemplate.Settings.DefaultChannelKey ??= "onlineNodes";
        SetDefaultParameter(clusterTemplate.Settings, "pve.scope", "cluster");
        SetDefaultChannelThreshold(clusterTemplate.Settings, "offlineNodes", "warning", new ThresholdRule(ThresholdDirection.Above, 0.5));
        SetDefaultChannelThreshold(clusterTemplate.Settings, "nodeOnlineRatio", "critical", new ThresholdRule(ThresholdDirection.Below, 100));

        var nodeTemplate = EnsureTemplate(
            nodeTemplateKey,
            nodeTemplateName,
            MonitoringTemplateScope.Sensor,
            ProxmoxPveSensorExecutor.Definition.Key,
            hostTemplate.Id);
        nodeTemplate.Settings.Enabled ??= true;
        SetDefaultPollingInterval(nodeTemplate.Settings, TimeSpan.FromSeconds(20));
        nodeTemplate.Settings.Timeout ??= TimeSpan.FromSeconds(10);
        nodeTemplate.Settings.DefaultChannelKey ??= "cpu";
        SetDefaultParameter(nodeTemplate.Settings, "pve.scope", "node");
        SetDefaultChannelThreshold(nodeTemplate.Settings, "cpu", "warning", new ThresholdRule(ThresholdDirection.Above, 85));
        SetDefaultChannelThreshold(nodeTemplate.Settings, "cpu", "critical", new ThresholdRule(ThresholdDirection.Above, 95));
        SetDefaultChannelThreshold(nodeTemplate.Settings, "memory", "warning", new ThresholdRule(ThresholdDirection.Above, 85));
        SetDefaultChannelThreshold(nodeTemplate.Settings, "memory", "critical", new ThresholdRule(ThresholdDirection.Above, 95));
        SetDefaultChannelThreshold(nodeTemplate.Settings, "rootfs", "warning", new ThresholdRule(ThresholdDirection.Above, 85));
        SetDefaultChannelThreshold(nodeTemplate.Settings, "rootfs", "critical", new ThresholdRule(ThresholdDirection.Above, 95));
    }

    private MonitoringTemplate EnsureTemplate(
        string templateKey,
        string templateName,
        MonitoringTemplateScope targetKind,
        string? sensorTypeKey = null,
        Guid? parentTemplateId = null)
    {
        var template = _document.Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, templateKey, StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(candidate.Key) &&
             string.Equals(candidate.Name, templateName, StringComparison.OrdinalIgnoreCase) &&
             candidate.TargetKind == targetKind));

        if (template is null)
        {
            template = new MonitoringTemplate
            {
                Key = templateKey,
                Name = templateName,
                TargetKind = targetKind,
                SensorTypeKey = sensorTypeKey,
                ParentTemplateId = parentTemplateId
            };
            _document.Templates.Add(template);
            return template;
        }

        if (string.IsNullOrWhiteSpace(template.Key))
        {
            template.Key = templateKey;
        }

        if (string.IsNullOrWhiteSpace(template.Name))
        {
            template.Name = templateName;
        }

        if (template.TargetKind == MonitoringTemplateScope.Any)
        {
            template.TargetKind = targetKind;
        }

        if (string.IsNullOrWhiteSpace(template.SensorTypeKey) && !string.IsNullOrWhiteSpace(sensorTypeKey))
        {
            template.SensorTypeKey = sensorTypeKey;
        }

        if (template.ParentTemplateId is null && parentTemplateId.HasValue)
        {
            template.ParentTemplateId = parentTemplateId;
        }

        return template;
    }

    private static void SetDefaultParameter(MonitoringSettings settings, string key, string value)
    {
        if (!settings.Parameters.TryGetValue(key, out var currentValue) || string.IsNullOrWhiteSpace(currentValue))
        {
            settings.Parameters[key] = value;
        }
    }

    private static void SetDefaultPollingInterval(MonitoringSettings settings, TimeSpan interval)
    {
        if (settings.PollingInterval is null && settings.PollingSchedule is null)
        {
            settings.PollingInterval = interval;
        }
    }

    private static void SetDefaultDailySchedule(MonitoringSettings settings, TimeSpan timeOfDay)
    {
        if (settings.PollingInterval is null && settings.PollingSchedule is null)
        {
            settings.PollingSchedule = new MonitoringSchedule
            {
                Mode = MonitoringScheduleMode.Daily,
                TimeOfDay = timeOfDay
            };
        }
    }

    private static void SetDefaultChannelThreshold(
        MonitoringSettings settings,
        string channelKey,
        string severity,
        ThresholdRule rule)
    {
        var key = MonitoringSettings.BuildChannelThresholdKey(channelKey, severity);
        if (!settings.Thresholds.ContainsKey(key))
        {
            MonitoringSettings.SetChannelThreshold(settings, channelKey, severity, rule);
        }
    }

    private static void SetDefaultOrMigrateChannelThreshold(
        MonitoringSettings settings,
        string channelKey,
        string severity,
        ThresholdRule rule,
        params string[] migrateFrom)
    {
        _ = migrateFrom;
        var key = MonitoringSettings.BuildChannelThresholdKey(channelKey, severity);
        if (!settings.Thresholds.ContainsKey(key))
        {
            MonitoringSettings.SetChannelThreshold(settings, channelKey, severity, rule);
        }
    }

    private void EnsureDefaultWindowsHealthSensor()
    {
        const string sensorName = "Windows Health";
        const string sensorTarget = "windows-host";
        const string templateKey = "windows-health";

        var template = _document.Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, templateKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Name, sensorName, StringComparison.OrdinalIgnoreCase));

        var sensor = EnumerateElements(_document.RootProbe)
            .OfType<SensorElement>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.SensorTypeKey, PowerShellRemoteSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Name, sensorName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Target, sensorTarget, StringComparison.OrdinalIgnoreCase));

        if (sensor is null)
        {
            sensor = new SensorElement(sensorName, PowerShellRemoteSensorExecutor.Definition.Key, sensorTarget)
            {
                Description = "Windows workstation health monitor"
            };
            AddChild(_document.RootProbe, sensor);
        }

        sensor.ParentId = _document.RootProbe.Id;
        sensor.SensorTypeKey = PowerShellRemoteSensorExecutor.Definition.Key;
        sensor.Target = sensorTarget;

        if (template is not null && !sensor.AppliedTemplateIds.Contains(template.Id))
        {
            sensor.AppliedTemplateIds.Add(template.Id);
        }

        sensor.Settings.Highlight = true;
    }

    private void EnsureDefaultProxmoxSensor()
    {
        const string sensorName = "PVE";
        const string sensorTarget = "proxmox-host";
        const string templateKey = "proxmox-pve-node";

        var template = _document.Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, templateKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Name, "Proxmox PVE Node", StringComparison.OrdinalIgnoreCase));

        var sensor = EnumerateElements(_document.RootProbe)
            .OfType<SensorElement>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.SensorTypeKey, ProxmoxPveSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Name, sensorName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Target, sensorTarget, StringComparison.OrdinalIgnoreCase));

        if (sensor is null)
        {
            sensor = new SensorElement(sensorName, ProxmoxPveSensorExecutor.Definition.Key, sensorTarget)
            {
                Description = "Proxmox node health monitor"
            };
            AddChild(_document.RootProbe, sensor);
        }

        sensor.ParentId = _document.RootProbe.Id;
        sensor.SensorTypeKey = ProxmoxPveSensorExecutor.Definition.Key;
        sensor.Target = sensorTarget;

        if (template is not null && !sensor.AppliedTemplateIds.Contains(template.Id))
        {
            sensor.AppliedTemplateIds.Add(template.Id);
        }

        if (sensor.Settings.Parameters.TryGetValue("payloadSize", out var legacyScope) &&
            !sensor.Settings.Parameters.ContainsKey("pve.scope"))
        {
            sensor.Settings.Parameters["pve.scope"] = string.Equals(legacyScope, "node", StringComparison.OrdinalIgnoreCase)
                ? "node"
                : "node";
            sensor.Settings.Parameters.Remove("payloadSize");
        }
        else
        {
            sensor.Settings.Parameters["pve.scope"] = "node";
            sensor.Settings.Parameters.Remove("payloadSize");
        }

        if (!sensor.Settings.Parameters.ContainsKey("pve.user"))
        {
            sensor.Settings.Parameters["pve.user"] = "root@pam";
        }

        if (!sensor.Settings.Parameters.ContainsKey("pve.tokenId"))
        {
            sensor.Settings.Parameters["pve.tokenId"] = "monitoring";
        }

        sensor.Settings.Highlight = true;
    }

    private void EnsureProbeMetadataRecursive(ProbeElement probe, bool isRoot = false)
    {
        if (string.IsNullOrWhiteSpace(probe.ProbeId))
        {
            probe.ProbeId = isRoot
                ? "master"
                : GenerateUniqueProbeId(probe.Name);
        }

        if (string.IsNullOrWhiteSpace(probe.EnrollmentToken) && !isRoot)
        {
            probe.EnrollmentToken = CreateToken();
        }

        foreach (var childProbe in probe.Children.OfType<ProbeElement>())
        {
            EnsureProbeMetadataRecursive(childProbe);
        }
    }

    private void EnsureProbeHeartbeatSensorsRecursive(ProbeElement probe, bool isRoot = false)
    {
        if (!isRoot)
        {
            EnsureProbeHeartbeatSensor(probe);
        }

        foreach (var childProbe in probe.Children.OfType<ProbeElement>())
        {
            EnsureProbeHeartbeatSensorsRecursive(childProbe);
        }
    }

    private void EnsureProbeHealthSensorsRecursive(ProbeElement probe)
    {
        EnsureProbeHealthSensor(probe);

        foreach (var childProbe in probe.Children.OfType<ProbeElement>())
        {
            EnsureProbeHealthSensorsRecursive(childProbe);
        }
    }

    private static void EnsureProbeHeartbeatSensor(ProbeElement probe)
    {
        var sensor = probe.Children
            .OfType<SensorElement>()
            .FirstOrDefault(candidate => string.Equals(candidate.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase));

        if (sensor is null)
        {
            sensor = new SensorElement("Heartbeat", ProbeHeartbeatSensorExecutor.Definition.Key, probe.ProbeId)
            {
                Description = "Probe heartbeat monitor"
            };
            AddChild(probe, sensor);
        }
        else
        {
            sensor.ParentId = probe.Id;
            sensor.Target = probe.ProbeId;
            if (string.IsNullOrWhiteSpace(sensor.Name))
            {
                sensor.Name = "Heartbeat";
            }
        }

        ApplyParameterDefaults(sensor.Settings, ProbeHeartbeatSensorExecutor.Definition);
        EnsureHeartbeatThresholdDefaults(sensor.Settings);
    }

    private static void EnsureProbeHealthSensor(ProbeElement probe)
    {
        var sensor = probe.Children
            .OfType<SensorElement>()
            .FirstOrDefault(candidate => string.Equals(candidate.SensorTypeKey, ProbeHealthSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase));

        if (sensor is null)
        {
            sensor = new SensorElement("Probe Health", ProbeHealthSensorExecutor.Definition.Key, probe.ProbeId)
            {
                Description = "Probe connection and storage health"
            };
            AddChild(probe, sensor);
        }
        else
        {
            sensor.ParentId = probe.Id;
            sensor.Target = probe.ProbeId;
            if (string.IsNullOrWhiteSpace(sensor.Name))
            {
                sensor.Name = "Probe Health";
            }
        }

        sensor.Settings.Enabled ??= true;
        sensor.Settings.PollingInterval ??= TimeSpan.FromSeconds(30);
        sensor.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        sensor.Settings.DefaultChannelKey ??= "storageFreePercent";
        ApplyParameterDefaults(sensor.Settings, ProbeHealthSensorExecutor.Definition);
        EnsureProbeHealthThresholdDefaults(sensor.Settings);
    }

    private static void EnsureHeartbeatThresholdDefaults(MonitoringSettings settings)
    {
        var warningKey = MonitoringSettings.BuildChannelThresholdKey("ageSeconds", "warning");
        var criticalKey = MonitoringSettings.BuildChannelThresholdKey("ageSeconds", "critical");

        if (!settings.Thresholds.ContainsKey(warningKey))
        {
            settings.Thresholds[warningKey] = MonitoringSettings.FormatThresholdRule(new ThresholdRule(ThresholdDirection.Above, 30));
        }

        if (!settings.Thresholds.ContainsKey(criticalKey))
        {
            settings.Thresholds[criticalKey] = MonitoringSettings.FormatThresholdRule(new ThresholdRule(ThresholdDirection.Above, 60));
        }
    }

    private static void EnsureProbeHealthThresholdDefaults(MonitoringSettings settings)
    {
        SetDefaultChannelThreshold(settings, "storageFreePercent", "warning", new ThresholdRule(ThresholdDirection.Below, 15));
        SetDefaultChannelThreshold(settings, "storageFreePercent", "critical", new ThresholdRule(ThresholdDirection.Below, 8));
        SetDefaultChannelThreshold(settings, "connected", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(settings, "dataPathAvailable", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
    }

    private static void ApplyParameterDefaults(MonitoringSettings settings, SensorDefinition definition)
    {
        foreach (var parameter in definition.Parameters)
        {
            if (parameter.DefaultValue is null)
            {
                continue;
            }

            if (!settings.Parameters.ContainsKey(parameter.Key))
            {
                settings.Parameters[parameter.Key] = parameter.DefaultValue;
            }
        }
    }

    private MonitoringContainerElement ResolveParentContainer(Guid? parentId, params MonitoringElementKind[] allowedKinds)
    {
        var parent = parentId is Guid id
            ? FindElement(id)
            : _document.RootProbe;

        if (parent is null)
        {
            throw new InvalidOperationException("Parent element was not found.");
        }

        if (parent is not MonitoringContainerElement container)
        {
            throw new InvalidOperationException("Selected parent cannot contain children.");
        }

        if (allowedKinds.Length > 0 && !allowedKinds.Contains(parent.Kind))
        {
            throw new InvalidOperationException($"Parent kind '{parent.Kind}' is not allowed here.");
        }

        return container;
    }

    private static MonitoringElementKind[] GetAllowedParentKinds(MonitoringElement element)
    {
        return element switch
        {
            ProbeElement => [MonitoringElementKind.Probe],
            FolderElement => [MonitoringElementKind.Probe, MonitoringElementKind.Folder],
            HostElement => [MonitoringElementKind.Probe, MonitoringElementKind.Folder],
            SensorElement => [MonitoringElementKind.Probe, MonitoringElementKind.Folder, MonitoringElementKind.Host],
            _ => []
        };
    }

    private static MonitoringContainerElement? FindParentContainer(MonitoringContainerElement parent, Guid childId)
    {
        if (parent.Children.Any(candidate => candidate.Id == childId))
        {
            return parent;
        }

        foreach (var childContainer in parent.Children.OfType<MonitoringContainerElement>())
        {
            var found = FindParentContainer(childContainer, childId);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static void AddChild(MonitoringContainerElement parent, MonitoringElement child)
    {
        child.ParentId = parent.Id;
        parent.Children.Add(child);
    }

    private static bool RemoveChild(MonitoringContainerElement parent, Guid id)
    {
        var child = parent.Children.FirstOrDefault(candidate => candidate.Id == id);
        if (child is not null)
        {
            parent.Children.Remove(child);
            return true;
        }

        foreach (var childContainer in parent.Children.OfType<MonitoringContainerElement>())
        {
            if (RemoveChild(childContainer, id))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<MonitoringElement> EnumerateElements(MonitoringElement element)
    {
        yield return element;

        if (element is not MonitoringContainerElement container)
        {
            yield break;
        }

        foreach (var child in container.Children)
        {
            foreach (var nested in EnumerateElements(child))
            {
                yield return nested;
            }
        }
    }

    private string GenerateUniqueProbeId(string name)
    {
        var baseId = Slugify(name);
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = "probe";
        }

        var existing = EnumerateElements(_document.RootProbe)
            .OfType<ProbeElement>()
            .Select(probe => probe.ProbeId)
            .Where(probeId => !string.IsNullOrWhiteSpace(probeId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidate = baseId;
        var suffix = 2;
        while (existing.Contains(candidate))
        {
            candidate = $"{baseId}-{suffix++}";
        }

        return candidate;
    }

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasDash = false;
                continue;
            }

            if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string CreateToken()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    private void AddEvent(MonitoringEvent monitoringEvent)
    {
        _telemetry.AppendEvent(monitoringEvent);
    }

    private void PruneEvents(DateTimeOffset now, MonitoringSettings? settings)
    {
        var retentionDays = ResolveRetentionDays(settings?.EventRetentionDays, DefaultEventRetentionDays);
        if (retentionDays <= 0)
        {
            return;
        }

        var cutoff = now - TimeSpan.FromDays(retentionDays);
        _telemetry.PruneEvents(cutoff);
    }

    private void PruneSensorHistory(Guid sensorId, DateTimeOffset now, MonitoringSettings? settings)
    {
        var retentionDays = ResolveRetentionDays(settings?.ObservationRetentionDays, DefaultObservationRetentionDays);
        if (retentionDays <= 0)
        {
            return;
        }

        var cutoff = now - TimeSpan.FromDays(retentionDays);
        _telemetry.PruneObservations(sensorId, cutoff);
    }

    private void PruneStatistics(Guid sensorId, DateTimeOffset now, MonitoringSettings? settings)
    {
        var retentionDays = ResolveRetentionDays(settings?.StatisticsRetentionDays, DefaultStatisticsRetentionDays);
        if (retentionDays <= 0)
        {
            return;
        }

        var cutoff = now - TimeSpan.FromDays(retentionDays);
        _telemetry.PruneStatistics(sensorId, cutoff);
    }

    private void UpdateSensorStatistics(Guid sensorId, SensorExecutionResult result, DateTimeOffset timestampUtc, MonitoringSettings? settings)
    {
        if (!TryGetStatisticSample(result, out var sampleValue, out var channelKey, out var unit))
        {
            return;
        }

        var bucketMinutes = ResolveRetentionDays(settings?.StatisticsBucketMinutes, DefaultStatisticsBucketMinutes);
        if (bucketMinutes <= 0)
        {
            return;
        }

        var bucketStartUtc = FloorToBucket(timestampUtc, bucketMinutes);
        var bucket = _telemetry.GetStatisticsBucket(sensorId, bucketMinutes, bucketStartUtc)
            ?? new SensorStatisticsBucket
            {
                SensorId = sensorId,
                BucketStartUtc = bucketStartUtc,
                BucketMinutes = bucketMinutes,
                DefaultChannelKey = channelKey,
                Unit = unit
            };

        bucket.DefaultChannelKey = channelKey;
        bucket.Unit = unit ?? bucket.Unit;
        bucket.SampleCount++;
        bucket.Average = bucket.Average is double average
            ? ((average * (bucket.SampleCount - 1)) + sampleValue) / bucket.SampleCount
            : sampleValue;
        bucket.Minimum = bucket.Minimum is double minimum ? Math.Min(minimum, sampleValue) : sampleValue;
        bucket.Maximum = bucket.Maximum is double maximum ? Math.Max(maximum, sampleValue) : sampleValue;
        bucket.LastValue = sampleValue;
        bucket.State = result.State;
        bucket.Message = result.Message;
        _telemetry.UpsertStatisticsBucket(bucket);
    }

    private static bool TryGetStatisticSample(
        SensorExecutionResult result,
        out double value,
        out string channelKey,
        out string? unit)
    {
        var defaultChannel = result.Channels.FirstOrDefault(channel =>
            channel.IsDefault ||
            (!string.IsNullOrWhiteSpace(result.DefaultChannelKey) &&
             string.Equals(channel.Key, result.DefaultChannelKey, StringComparison.OrdinalIgnoreCase)));

        if (defaultChannel is null && result.Channels.Count > 0)
        {
            defaultChannel = result.Channels[0];
        }

        if (defaultChannel?.Value is double channelValue)
        {
            value = channelValue;
            channelKey = string.IsNullOrWhiteSpace(defaultChannel.Key) ? result.DefaultChannelKey ?? "default" : defaultChannel.Key;
            unit = defaultChannel.Unit;
            return true;
        }

        if (result.Value.HasValue)
        {
            value = result.Value.Value;
            channelKey = string.IsNullOrWhiteSpace(result.DefaultChannelKey) ? "default" : result.DefaultChannelKey;
            unit = defaultChannel?.Unit;
            return true;
        }

        if (result.State == SensorState.Critical)
        {
            value = 0d;
            channelKey = string.IsNullOrWhiteSpace(result.DefaultChannelKey) ? "default" : result.DefaultChannelKey;
            unit = defaultChannel?.Unit;
            return true;
        }

        value = default;
        channelKey = string.Empty;
        unit = null;
        return false;
    }

    private static int ResolveRetentionDays(int? configuredValue, int fallback)
    {
        return configuredValue is int configured && configured > 0 ? configured : fallback;
    }

    private static DateTimeOffset FloorToBucket(DateTimeOffset timestampUtc, int bucketMinutes)
    {
        var bucketSpan = TimeSpan.FromMinutes(Math.Max(bucketMinutes, 1));
        var ticks = timestampUtc.UtcTicks - (timestampUtc.UtcTicks % bucketSpan.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private bool ShouldRecordStateChangeEvent(SensorObservation? previousObservation, SensorExecutionResult result)
    {
        return previousObservation is null || previousObservation.State != result.State;
    }

    private string GetElementName(Guid elementId)
    {
        return FindElementInternal(elementId)?.Name ?? elementId.ToString("N");
    }

    private string GetElementPath(Guid elementId)
    {
        var element = FindElementInternal(elementId);
        return element is null ? elementId.ToString("N") : GetElementPath(element);
    }

    private string GetElementPath(MonitoringElement element)
    {
        var lineage = BuildLineage(element);
        return string.Join(" / ", lineage.Select(node => node.Name));
    }

    private IReadOnlyList<MonitoringElement> BuildLineage(MonitoringElement element)
    {
        var lineage = new List<MonitoringElement>();
        var current = element;

        while (true)
        {
            lineage.Add(current);

            if (current.ParentId is not Guid parentId)
            {
                break;
            }

            var parent = FindElementInternal(parentId);
            if (parent is null)
            {
                break;
            }

            current = parent;
        }

        lineage.Reverse();
        return lineage;
    }

    private MonitoringElement? FindElementInternal(Guid id)
    {
        return EnumerateElements(_document.RootProbe).FirstOrDefault(element => element.Id == id);
    }

    private static string BuildStateChangeMessage(SensorState? previousState, SensorState currentState, string? message)
    {
        var transition = previousState is null
            ? $"state {MonitoringStatePresentation.Label(currentState)}"
            : $"{MonitoringStatePresentation.Label(previousState.Value)} -> {MonitoringStatePresentation.Label(currentState)}";

        if (string.IsNullOrWhiteSpace(message))
        {
            return transition;
        }

        return $"{transition}: {message}";
    }

    private static string AppendExecutionProbe(string message, string? probeName, string? probeId)
    {
        if (string.IsNullOrWhiteSpace(probeName) && string.IsNullOrWhiteSpace(probeId))
        {
            return message;
        }

        var probe = string.IsNullOrWhiteSpace(probeName)
            ? probeId!.Trim()
            : string.IsNullOrWhiteSpace(probeId)
                ? probeName.Trim()
                : $"{probeName.Trim()} ({probeId.Trim()})";
        return $"{message} via {probe}";
    }

    private void ResolveAlertsForElement(Guid elementId, DateTimeOffset resolvedAt, string message)
    {
        foreach (var alert in _document.Alerts.Where(alert => alert.IsActive && alert.ElementId == elementId))
        {
            alert.ResolvedUtc = resolvedAt;
            if (!string.IsNullOrWhiteSpace(message))
            {
                alert.Message = message;
            }

            AddEvent(new MonitoringEvent
            {
                TimestampUtc = resolvedAt,
                Kind = MonitoringEventKind.AlertResolved,
                ElementId = alert.ElementId,
                ElementKind = alert.ElementKind,
                ElementName = alert.ElementName,
                ElementPath = alert.ElementPath,
                State = alert.State,
                Message = alert.Message
            });
        }
    }

    private void SyncSensorAlertFromObservation(
        Guid sensorId,
        SensorExecutionResult result,
        DateTimeOffset timestampUtc)
    {
        if (result.State is not (SensorState.Warning or SensorState.Critical))
        {
            ResolveAlertsForElement(sensorId, timestampUtc, string.Empty);
            return;
        }

        var sensor = FindElementInternal(sensorId) as SensorElement;
        if (sensor is null)
        {
            return;
        }

        var path = GetElementPath(sensor);
        var message = string.IsNullOrWhiteSpace(result.Message)
            ? MonitoringStatePresentation.Label(result.State)
            : result.Message;
        var existing = _document.Alerts.FirstOrDefault(alert => alert.IsActive && alert.ElementId == sensorId);
        if (existing is null)
        {
            _document.Alerts.Add(new MonitoringAlert
            {
                ElementId = sensor.Id,
                ElementKind = sensor.Kind,
                ElementName = sensor.Name,
                ElementPath = path,
                State = result.State,
                Message = message,
                FirstSeenUtc = timestampUtc,
                LastSeenUtc = timestampUtc
            });

            AddEvent(new MonitoringEvent
            {
                TimestampUtc = timestampUtc,
                Kind = MonitoringEventKind.AlertRaised,
                ElementId = sensor.Id,
                ElementKind = sensor.Kind,
                ElementName = sensor.Name,
                ElementPath = path,
                State = result.State,
                Message = message
            });
            return;
        }

        existing.ElementKind = sensor.Kind;
        existing.ElementName = sensor.Name;
        existing.ElementPath = path;
        existing.State = result.State;
        existing.Message = message;
        existing.LastSeenUtc = timestampUtc;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static SensorDefinition CloneSensorDefinition(SensorDefinition source)
    {
        return new SensorDefinition
        {
            Key = source.Key,
            DisplayName = source.DisplayName,
            Description = source.Description,
            UsageLevel = source.UsageLevel ?? SensorUsageCatalog.Resolve(source.Key),
            ChannelMode = source.ChannelMode,
            Parameters = source.Parameters.Select(parameter => new SensorParameterDefinition
            {
                Key = parameter.Key,
                Label = parameter.Label,
                Kind = parameter.Kind,
                Description = parameter.Description,
                Required = parameter.Required,
                DefaultValue = parameter.DefaultValue,
                Placeholder = parameter.Placeholder,
                Min = parameter.Min,
                Max = parameter.Max,
                Step = parameter.Step,
                CredentialKind = parameter.CredentialKind,
                Options = parameter.Options.Select(option => new SensorParameterOption
                {
                    Value = option.Value,
                    Label = option.Label
                }).ToArray()
            }).ToArray()
        };
    }

    private static MatmonUser CloneUser(MatmonUser source)
    {
        return new MatmonUser
        {
            Id = source.Id,
            Username = source.Username,
            PasswordHash = string.Empty,
            Role = source.Role,
            IsEnabled = source.IsEnabled,
            CreatedUtc = source.CreatedUtc,
            UpdatedUtc = source.UpdatedUtc
        };
    }

    private static MonitoringMap CloneMap(MonitoringMap source)
    {
        return new MonitoringMap
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
            PublicToken = source.PublicToken,
            Columns = source.Columns,
            Rows = source.Rows,
            DisplayPreset = source.DisplayPreset,
            CreatedUtc = source.CreatedUtc,
            UpdatedUtc = source.UpdatedUtc,
            Tiles = source.Tiles.Select(tile => new MonitoringMapTile
            {
                Id = tile.Id,
                Kind = tile.Kind,
                Title = tile.Title,
                ElementId = tile.ElementId,
                Text = tile.Text,
                X = tile.X,
                Y = tile.Y,
                Width = tile.Width,
                Height = tile.Height,
                BackgroundColor = tile.BackgroundColor,
                AccentColor = tile.AccentColor,
                TextColor = tile.TextColor,
                GraphType = tile.GraphType,
                VisualType = tile.VisualType,
                ShowTitle = tile.ShowTitle,
                ShowStateBadge = tile.ShowStateBadge,
                ShowElementName = tile.ShowElementName
            }).ToList()
        };
    }

    private static string NormalizeUsername(string username)
    {
        var normalized = username?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Username is required.");
        }

        if (normalized.Length > 80)
        {
            throw new InvalidOperationException("Username is too long.");
        }

        return normalized;
    }

    private static string NormalizeMapName(string name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized) ? "Map" : normalized;
    }

    private static IReadOnlyList<MonitoringMapTile> NormalizeMapTiles(
        IReadOnlyList<MonitoringMapTile> tiles,
        int columns,
        int rows)
    {
        return tiles
            .Where(tile => !string.IsNullOrWhiteSpace(tile.Title) || !string.IsNullOrWhiteSpace(tile.Text) || tile.ElementId.HasValue)
            .Select(tile =>
            {
                var sizeLimits = GetMapTileSizeLimits(tile.Kind, columns, rows);
                var width = Math.Clamp(tile.Width <= 0 ? sizeLimits.DefaultWidth : tile.Width, sizeLimits.MinWidth, sizeLimits.MaxWidth);
                var height = Math.Clamp(tile.Height <= 0 ? sizeLimits.DefaultHeight : tile.Height, sizeLimits.MinHeight, sizeLimits.MaxHeight);
                var x = Math.Clamp(tile.X <= 0 ? 1 : tile.X, 1, Math.Max(1, columns - width + 1));
                var y = Math.Clamp(tile.Y <= 0 ? 1 : tile.Y, 1, Math.Max(1, rows - height + 1));

                return new MonitoringMapTile
                {
                    Id = tile.Id == Guid.Empty ? Guid.NewGuid() : tile.Id,
                    Kind = tile.Kind,
                    Title = string.IsNullOrWhiteSpace(tile.Title) ? "Tile" : tile.Title.Trim(),
                    ElementId = tile.ElementId == Guid.Empty ? null : tile.ElementId,
                    Text = string.IsNullOrWhiteSpace(tile.Text) ? null : tile.Text.Trim(),
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height,
                    BackgroundColor = NormalizeColor(tile.BackgroundColor),
                    AccentColor = NormalizeColor(tile.AccentColor),
                    TextColor = NormalizeColor(tile.TextColor),
                    GraphType = tile.GraphType,
                    VisualType = tile.VisualType,
                    ShowTitle = tile.ShowTitle,
                    ShowStateBadge = tile.ShowStateBadge,
                    ShowElementName = tile.ShowElementName
                };
            })
            .ToArray();
    }

    private static MapTileSizeLimits GetMapTileSizeLimits(MonitoringMapTileKind kind, int columns, int rows)
    {
        var limits = kind switch
        {
            MonitoringMapTileKind.Text => new MapTileSizeLimits(2, 1, 12, 6, 4, 2),
            MonitoringMapTileKind.Status => new MapTileSizeLimits(3, 2, 12, 8, 4, 2),
            MonitoringMapTileKind.Value => new MapTileSizeLimits(2, 2, 8, 6, 3, 2),
            MonitoringMapTileKind.Graph => new MapTileSizeLimits(4, 3, 12, 10, 5, 3),
            _ => new MapTileSizeLimits(2, 1, 8, 6, 3, 2)
        };

        var maxWidth = Math.Clamp(limits.MaxWidth, limits.MinWidth, columns);
        var maxHeight = Math.Clamp(limits.MaxHeight, limits.MinHeight, rows);
        return limits with
        {
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            DefaultWidth = Math.Clamp(limits.DefaultWidth, limits.MinWidth, maxWidth),
            DefaultHeight = Math.Clamp(limits.DefaultHeight, limits.MinHeight, maxHeight)
        };
    }

    private static string? NormalizeColor(string? color)
    {
        var normalized = color?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length == 7 &&
            normalized[0] == '#' &&
            normalized.Skip(1).All(Uri.IsHexDigit))
        {
            return normalized.ToLowerInvariant();
        }

        return null;
    }

    private sealed record MapTileSizeLimits(
        int MinWidth,
        int MinHeight,
        int MaxWidth,
        int MaxHeight,
        int DefaultWidth,
        int DefaultHeight);

    private sealed class WorkspaceDocument
    {
        public ProbeElement RootProbe { get; set; } = default!;

        public List<MonitoringTemplate> Templates { get; set; } = [];

        public List<SensorDefinition> SensorDefinitions { get; set; } = [];

        public List<MatmonUser> Users { get; set; } = [];

        public List<MonitoringMap> Maps { get; set; } = [];

        public NotificationWorkspaceConfiguration NotificationConfiguration { get; set; } = new();

        public List<NotificationSender> NotificationSenders { get; set; } = [];

        public List<NotificationReceiver> NotificationReceivers { get; set; } = [];

        public List<NotificationRule> NotificationRules { get; set; } = [];

        public List<MonitoringAlert> Alerts { get; set; } = [];

        public List<WorkspaceBackupJob> BackupJobs { get; set; } = [];

        public List<SensorObservation> SensorHistory { get; set; } = [];

        public List<MonitoringEvent> Events { get; set; } = [];

        public List<SensorStatisticsBucket> SensorStatistics { get; set; } = [];
    }


    private enum SavePriority
    {
        Configuration,
        Telemetry
    }
}
