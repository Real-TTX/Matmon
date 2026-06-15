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

public sealed partial class InMemoryMonitoringWorkspaceStore
{
    public IReadOnlyList<WorkspaceBackupJob> GetBackupJobs()
    {
        lock (_gate)
        {
            EnsureBackupJobsCollection();
            return _document.BackupJobs
                .OrderBy(job => job.Name, StringComparer.OrdinalIgnoreCase)
                .Select(job => job.Clone())
                .ToArray();
        }
    }

    public WorkspaceBackupJob? FindBackupJob(Guid jobId)
    {
        lock (_gate)
        {
            EnsureBackupJobsCollection();
            var job = _document.BackupJobs.FirstOrDefault(candidate => candidate.Id == jobId);
            return job?.Clone();
        }
    }

    public WorkspaceBackupJob CreateBackupJob(WorkspaceBackupJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock (_gate)
        {
            EnsureBackupJobsCollection();
            var clone = job.Clone();
            clone.Name = NormalizeBackupJobName(clone.Name);
            if (_document.BackupJobs.Any(candidate => string.Equals(candidate.Name, clone.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Backup job '{clone.Name}' already exists.");
            }

            NormalizeBackupJob(clone);
            clone.NextRunUtc = CalculateNextRunUtc(clone, DateTimeOffset.UtcNow);
            _document.BackupJobs.Add(clone);
            QueueSave(SavePriority.Configuration);
            return clone.Clone();
        }
    }

    public bool UpdateBackupJob(WorkspaceBackupJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock (_gate)
        {
            EnsureBackupJobsCollection();
            var existing = _document.BackupJobs.FirstOrDefault(candidate => candidate.Id == job.Id);
            if (existing is null)
            {
                return false;
            }

            var normalizedName = NormalizeBackupJobName(job.Name);
            if (_document.BackupJobs.Any(candidate =>
                    candidate.Id != job.Id &&
                    string.Equals(candidate.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Backup job '{normalizedName}' already exists.");
            }

            existing.Name = normalizedName;
            existing.Description = job.Description?.Trim();
            existing.Enabled = job.Enabled;
            existing.Schedule = job.Schedule?.Clone() ?? new MonitoringSchedule();
            existing.Sections = job.Sections == WorkspaceBackupSection.None ? WorkspaceBackupSection.All : job.Sections;
            existing.RetentionCount = Math.Clamp(job.RetentionCount, 1, 100);
            existing.LastRunUtc = job.LastRunUtc;
            existing.NextRunUtc = CalculateNextRunUtc(existing, DateTimeOffset.UtcNow);
            existing.LastStatus = job.LastStatus?.Trim();
            existing.LastMessage = job.LastMessage?.Trim();
            existing.LastSnapshotFileName = job.LastSnapshotFileName?.Trim();
            existing.LastSnapshotBytes = job.LastSnapshotBytes;
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool DeleteBackupJob(Guid jobId)
    {
        lock (_gate)
        {
            EnsureBackupJobsCollection();
            var existing = _document.BackupJobs.FirstOrDefault(candidate => candidate.Id == jobId);
            if (existing is null)
            {
                return false;
            }

            _document.BackupJobs.Remove(existing);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public IReadOnlyList<WorkspaceBackupSnapshotInfo> GetBackupSnapshots(int take = 50)
    {
        var directory = ResolveBackupDirectoryPath();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(TryLoadBackupSnapshotInfo)
            .Where(snapshot => snapshot is not null)
            .Select(snapshot => snapshot!)
            .OrderByDescending(snapshot => snapshot.CreatedUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToArray();
    }

    public WorkspaceBackupSnapshotInfo? FindBackupSnapshot(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var path = ResolveBackupFilePath(fileName);
        return TryLoadBackupSnapshotInfo(path);
    }

    public WorkspaceBackupSnapshotDetails? FindBackupSnapshotDetails(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var path = ResolveBackupFilePath(fileName);
        return TryLoadBackupSnapshotDetails(path);
    }

    public Stream? OpenBackupSnapshotReadStream(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var path = ResolveBackupFilePath(fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public WorkspaceBackupSnapshotInfo ImportBackupSnapshot(Stream content, string originalFileName)
    {
        ArgumentNullException.ThrowIfNull(content);

        lock (_gate)
        {
            var directory = ResolveBackupDirectoryPath();
            Directory.CreateDirectory(directory);

            var tempFileName = $"backup-upload-{Guid.NewGuid():N}.tmp";
            var tempPath = Path.Combine(directory, tempFileName);
            try
            {
                using (var tempStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    content.CopyTo(tempStream);
                }

                var package = TryLoadBackupPackage(tempPath) ?? throw new InvalidOperationException("Uploaded backup file could not be read.");
                var finalFileName = BuildImportedBackupFileName(originalFileName, package);
                var finalPath = ResolveBackupFilePath(finalFileName);

                File.Move(tempPath, finalPath, overwrite: true);
                return CreateSnapshotInfo(finalPath, package);
            }
            catch
            {
                TryDeleteTempFile(tempPath);
                throw;
            }
        }
    }

    public WorkspaceBackupSnapshotInfo RunBackupJob(Guid jobId, string? reason = null)
    {
        lock (_gate)
        {
            EnsureBackupJobsCollection();
            var job = _document.BackupJobs.FirstOrDefault(candidate => candidate.Id == jobId)
                ?? throw new InvalidOperationException("Backup job not found.");

            try
            {
                var package = CreateBackupPackageLocked(job, _document, reason);
                var fileName = BuildBackupFileName(job, package.CreatedUtc, package.Id);
                var filePath = ResolveBackupFilePath(fileName);
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(package, FileSerializerOptions);
                var tempPath = filePath + ".tmp";
                try
                {
                    WriteUtf8File(tempPath, json);
                    File.Move(tempPath, filePath, overwrite: true);
                }
                finally
                {
                    TryDeleteTempFile(tempPath);
                }

                var snapshot = CreateSnapshotInfo(filePath, package);
                job.LastRunUtc = package.CreatedUtc;
                job.NextRunUtc = CalculateNextRunUtc(job, package.CreatedUtc);
                job.LastStatus = "ok";
                job.LastMessage = string.IsNullOrWhiteSpace(reason)
                    ? "Backup created."
                    : reason.Trim();
                job.LastSnapshotFileName = fileName;
                job.LastSnapshotBytes = snapshot.Bytes;
                QueueSave(SavePriority.Configuration);

                PruneBackupHistoryLocked(job);
                return snapshot;
            }
            catch (Exception ex)
            {
                job.LastRunUtc = DateTimeOffset.UtcNow;
                job.NextRunUtc = CalculateNextRunUtc(job, job.LastRunUtc.Value);
                job.LastStatus = "error";
                job.LastMessage = ex.Message;
                QueueSave(SavePriority.Configuration);
                throw;
            }
        }
    }

    public WorkspaceBackupRestoreResult RestoreBackupSnapshot(string fileName, WorkspaceBackupSection sections)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name is required.", nameof(fileName));
        }

        if (sections == WorkspaceBackupSection.None)
        {
            throw new ArgumentException("At least one restore section must be selected.", nameof(sections));
        }

        var path = ResolveBackupFilePath(fileName);
        var package = TryLoadBackupPackage(path) ?? throw new InvalidOperationException("Backup file could not be read.");

        lock (_gate)
        {
            HydrateCredentialBundles(package.Document);
            ApplyBackupSections(_document, package.Document, sections);
            QueueSave(SavePriority.Configuration);
        }

        var restoredCount = CountSelectedSections(sections);
        return new WorkspaceBackupRestoreResult(
            fileName,
            sections,
            restoredCount,
            $"Restored {restoredCount} section(s) from '{fileName}'.");
    }

    private void EnsureBackupJobsCollection()
    {
        _document.BackupJobs ??= [];
        foreach (var job in _document.BackupJobs)
        {
            NormalizeBackupJob(job);
        }
    }

    private static string NormalizeBackupJobName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? "Backup job" : name.Trim();
    }

    private static void NormalizeBackupJob(WorkspaceBackupJob job)
    {
        job.Name = NormalizeBackupJobName(job.Name);
        job.Description = job.Description?.Trim();
        job.RetentionCount = Math.Clamp(job.RetentionCount, 1, 100);
        job.Sections = job.Sections == WorkspaceBackupSection.None ? WorkspaceBackupSection.All : job.Sections;
        job.Schedule ??= new MonitoringSchedule();
        job.NextRunUtc ??= CalculateNextRunUtc(job, DateTimeOffset.UtcNow);
    }

    private string ResolveBackupDirectoryPath()
    {
        return _backupDirectoryPath;
    }

    private string ResolveBackupFilePath(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        return Path.Combine(ResolveBackupDirectoryPath(), safeFileName);
    }

    private WorkspaceBackupPackage CreateBackupPackageLocked(WorkspaceBackupJob job, WorkspaceDocument document, string? reason)
    {
        ProtectCredentialBundles(document);
        try
        {
            var documentJson = JsonSerializer.Serialize(document, FileSerializerOptions);
            var documentClone = JsonSerializer.Deserialize<WorkspaceDocument>(documentJson, FileSerializerOptions)
                ?? CreatePlainWorkspaceDocument();

            // Telemetry lives in the repository; pull the selected sections into the snapshot.
            if (job.Sections.HasFlag(WorkspaceBackupSection.SensorHistory))
            {
                documentClone.SensorHistory = _telemetry.GetAllObservations().ToList();
            }

            if (job.Sections.HasFlag(WorkspaceBackupSection.Events))
            {
                documentClone.Events = _telemetry.GetAllEvents().ToList();
            }

            if (job.Sections.HasFlag(WorkspaceBackupSection.Statistics))
            {
                documentClone.SensorStatistics = _telemetry.GetAllStatistics().ToList();
            }

            return new WorkspaceBackupPackage
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                JobName = job.Name,
                Description = string.IsNullOrWhiteSpace(reason) ? job.Description : reason.Trim(),
                CreatedUtc = DateTimeOffset.UtcNow,
                Sections = job.Sections,
                Document = documentClone
            };
        }
        finally
        {
            HydrateCredentialBundles(document);
        }
    }

    private static string BuildBackupFileName(WorkspaceBackupJob job, DateTimeOffset createdUtc, Guid packageId)
    {
        var stamp = createdUtc.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return $"backup-{stamp}-{job.Id:N}-{packageId:N}.json";
    }

    private WorkspaceBackupSnapshotInfo CreateSnapshotInfo(string filePath, WorkspaceBackupPackage package)
    {
        var info = new FileInfo(filePath);
        var elements = EnumerateElements(package.Document.RootProbe).ToArray();
        var probes = elements.OfType<ProbeElement>().Count();
        var sensors = elements.OfType<SensorElement>().Count();

        return new WorkspaceBackupSnapshotInfo(
            Path.GetFileName(filePath),
            package.JobName ?? Path.GetFileNameWithoutExtension(filePath),
            package.JobId,
            package.JobName,
            package.Description,
            package.CreatedUtc,
            info.Exists ? info.Length : 0,
            package.Sections,
            probes,
            sensors,
            package.Document.Templates.Count,
            package.Document.Users.Count,
            package.Document.Alerts.Count,
            package.Document.SensorHistory.Count,
            package.Document.Events.Count,
            package.Document.SensorStatistics.Count);
    }

    private WorkspaceBackupSnapshotDetails? TryLoadBackupSnapshotDetails(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var package = TryLoadBackupPackage(path);
            return package is null ? null : CreateSnapshotDetails(path, package);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read backup snapshot details from {BackupPath}", path);
            return null;
        }
    }

    private WorkspaceBackupSnapshotInfo? TryLoadBackupSnapshotInfo(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var package = TryLoadBackupPackage(path);
            return package is null ? null : CreateSnapshotInfo(path, package);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read backup snapshot info from {BackupPath}", path);
            return null;
        }
    }

    private WorkspaceBackupSnapshotDetails CreateSnapshotDetails(string filePath, WorkspaceBackupPackage package)
    {
        var snapshot = CreateSnapshotInfo(filePath, package);
        return new WorkspaceBackupSnapshotDetails(snapshot, BuildBackupSectionPreviews(package));
    }

    private static WorkspaceBackupPackage? TryLoadBackupPackage(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<WorkspaceBackupPackage>(json, FileSerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<WorkspaceBackupSectionPreview> BuildBackupSectionPreviews(WorkspaceBackupPackage package)
    {
        var document = package.Document;
        var elements = EnumerateElements(document.RootProbe).ToArray();
        var probeCount = elements.OfType<ProbeElement>().Count();
        var folderCount = elements.OfType<FolderElement>().Count();
        var hostCount = elements.OfType<HostElement>().Count();
        var sensorCount = elements.OfType<SensorElement>().Count();
        var notificationCount = document.NotificationSenders.Count + document.NotificationReceivers.Count + document.NotificationRules.Count;

        var previews = new List<WorkspaceBackupSectionPreview>();
        foreach (var choice in BackupSectionCatalog.GetChoices())
        {
            var (itemCount, summary) = choice.Section switch
            {
                WorkspaceBackupSection.Topology => (
                    probeCount + folderCount + hostCount + sensorCount,
                    $"{probeCount} probes, {folderCount} folders, {hostCount} hosts, {sensorCount} sensors"),
                WorkspaceBackupSection.Templates => (
                    document.Templates.Count,
                    $"{document.Templates.Count} templates"),
                WorkspaceBackupSection.SensorDefinitions => (
                    document.SensorDefinitions.Count,
                    $"{document.SensorDefinitions.Count} sensor definitions"),
                WorkspaceBackupSection.Notifications => (
                    notificationCount,
                    $"{document.NotificationSenders.Count} senders, {document.NotificationReceivers.Count} receivers, {document.NotificationRules.Count} rules"),
                WorkspaceBackupSection.Maps => (
                    document.Maps.Count,
                    $"{document.Maps.Count} maps"),
                WorkspaceBackupSection.Users => (
                    document.Users.Count,
                    $"{document.Users.Count} users"),
                WorkspaceBackupSection.Alerts => (
                    document.Alerts.Count,
                    $"{document.Alerts.Count} alerts"),
                WorkspaceBackupSection.SensorHistory => (
                    document.SensorHistory.Count,
                    $"{document.SensorHistory.Count} history entries"),
                WorkspaceBackupSection.Events => (
                    document.Events.Count,
                    $"{document.Events.Count} events"),
                WorkspaceBackupSection.Statistics => (
                    document.SensorStatistics.Count,
                    $"{document.SensorStatistics.Count} statistic buckets"),
                WorkspaceBackupSection.BackupJobs => (
                    document.BackupJobs.Count,
                    $"{document.BackupJobs.Count} backup jobs"),
                _ => (0, "No data")
            };

            previews.Add(new WorkspaceBackupSectionPreview(
                choice.Section,
                choice.Label,
                choice.Description,
                summary,
                itemCount,
                package.Sections.HasFlag(choice.Section)));
        }

        return previews;
    }

    private static string BuildImportedBackupFileName(string originalFileName, WorkspaceBackupPackage package)
    {
        var stamp = DateTimeOffset.UtcNow.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var source = NormalizeBackupFileStem(originalFileName);
        return $"backup-import-{stamp}-{source}-{package.Id:N}.json";
    }

    private static string NormalizeBackupFileStem(string? fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return "backup";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(stem.Select(character => invalid.Contains(character) ? '-' : character).ToArray())
            .Trim('-', '_', '.', ' ');

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "backup";
        }

        return sanitized.Length > 32 ? sanitized[..32] : sanitized;
    }

    private void ApplyBackupSections(WorkspaceDocument target, WorkspaceDocument source, WorkspaceBackupSection sections)
    {
        if (sections.HasFlag(WorkspaceBackupSection.Topology))
        {
            target.RootProbe = source.RootProbe;
        }

        if (sections.HasFlag(WorkspaceBackupSection.Templates))
        {
            target.Templates = source.Templates;
        }

        if (sections.HasFlag(WorkspaceBackupSection.SensorDefinitions))
        {
            target.SensorDefinitions = source.SensorDefinitions;
        }

        if (sections.HasFlag(WorkspaceBackupSection.Notifications))
        {
            target.NotificationConfiguration = source.NotificationConfiguration;
            target.NotificationSenders = source.NotificationSenders;
            target.NotificationReceivers = source.NotificationReceivers;
            target.NotificationRules = source.NotificationRules;
        }

        if (sections.HasFlag(WorkspaceBackupSection.Maps))
        {
            target.Maps = source.Maps;
        }

        if (sections.HasFlag(WorkspaceBackupSection.Users))
        {
            target.Users = source.Users;
        }

        if (sections.HasFlag(WorkspaceBackupSection.Alerts))
        {
            target.Alerts = source.Alerts;
        }

        // Telemetry lives in the repository, not the document: restore straight into it.
        if (sections.HasFlag(WorkspaceBackupSection.SensorHistory))
        {
            _telemetry.ReplaceAllObservations(source.SensorHistory ?? []);
        }

        if (sections.HasFlag(WorkspaceBackupSection.Events))
        {
            _telemetry.ReplaceAllEvents(source.Events ?? []);
        }

        if (sections.HasFlag(WorkspaceBackupSection.Statistics))
        {
            _telemetry.ReplaceAllStatistics(source.SensorStatistics ?? []);
        }

        if (sections.HasFlag(WorkspaceBackupSection.BackupJobs))
        {
            target.BackupJobs = source.BackupJobs;
        }
    }

    private static int CountSelectedSections(WorkspaceBackupSection sections)
    {
        return Enum.GetValues<WorkspaceBackupSection>()
            .Count(section => section is not WorkspaceBackupSection.None and not WorkspaceBackupSection.All && sections.HasFlag(section));
    }

    private static DateTimeOffset? CalculateNextRunUtc(WorkspaceBackupJob job, DateTimeOffset nowUtc)
    {
        var settings = new MonitoringSettings
        {
            PollingSchedule = job.Schedule?.Clone()
        };

        return MonitoringScheduleCalculator.GetNextDueUtc(settings, job.LastRunUtc, nowUtc, TimeSpan.FromSeconds(1));
    }

    private void PruneBackupHistoryLocked(WorkspaceBackupJob job)
    {
        var directory = ResolveBackupDirectoryPath();
        if (!Directory.Exists(directory))
        {
            return;
        }

        var jobToken = $"-{job.Id:N}-";
        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(file => Path.GetFileName(file).Contains(jobToken, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => File.GetCreationTimeUtc(file))
            .ToArray();

        foreach (var file in files.Skip(Math.Max(job.RetentionCount, 1)))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune backup snapshot {BackupFile}", file);
            }
        }
    }

    private sealed class WorkspaceBackupPackage
    {
        public Guid Id { get; set; }

        public Guid? JobId { get; set; }

        public string? JobName { get; set; }

        public string? Description { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }

        public WorkspaceBackupSection Sections { get; set; } = WorkspaceBackupSection.All;

        public WorkspaceDocument Document { get; set; } = new();
    }
}
