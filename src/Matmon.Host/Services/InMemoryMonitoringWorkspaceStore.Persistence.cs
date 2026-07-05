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
    private WorkspaceDocument LoadDocument()
    {
        var loaded = TryLoadWorkspaceDocument(_workspacePath);
        if (loaded?.RootProbe is not null)
        {
            _logger.LogInformation("Workspace loaded from {WorkspacePath}", _workspacePath);
            return NormalizeLoadedDocument(loaded);
        }

        loaded = TryLoadWorkspaceDocument(_workspaceBackupPath);
        if (loaded?.RootProbe is not null)
        {
            _logger.LogWarning(
                "Workspace loaded from backup {WorkspacePathBackup} because the primary file could not be read.",
                _workspaceBackupPath);
            return NormalizeLoadedDocument(loaded);
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

    /// <summary>
    /// Guarantees the singletons + collections that the store dereferences without null checks are never
    /// null, even if a hand-edited or older workspace.json set them to <c>null</c> (a null cloudSettings
    /// otherwise NREs <see cref="GetCloudConnectionSettings"/> and takes the whole host down via TunnelClient).
    /// </summary>
    private static WorkspaceDocument NormalizeLoadedDocument(WorkspaceDocument document)
    {
        document.NotificationConfiguration ??= new();
        document.SummaryReport ??= new();
        document.Cloud ??= new();
        document.CloudSettings ??= new();
        document.Templates ??= [];
        document.SensorDefinitions ??= [];
        document.Users ??= [];
        document.Maps ??= [];
        document.NotificationSenders ??= [];
        document.NotificationReceivers ??= [];
        document.NotificationRules ??= [];
        document.Alerts ??= [];
        document.BackupJobs ??= [];
        document.SensorHistory ??= [];
        document.Events ??= [];
        document.SensorStatistics ??= [];
        return document;
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
                MoveIntoPlace(tempPath, _workspacePath);
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

    private static void MoveIntoPlace(string tempPath, string destinationPath)
    {
        // Retry the atomic move briefly — transient sharing violations (AV / backup holding the file
        // on Windows) are common. Never fall back to a non-atomic direct write: on failure we let the
        // exception propagate so the existing, valid workspace file is left intact and the save retries.
        const int attempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < attempts)
            {
                Thread.Sleep(50 * attempt);
            }
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
}
