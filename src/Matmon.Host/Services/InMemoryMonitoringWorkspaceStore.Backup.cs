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
            existing.Destination = job.Destination;
            existing.RetentionCount = Math.Clamp(job.RetentionCount, 1, 100);
            // Blank passphrase field = keep the stored one (never prefilled in the editor, like credential secrets);
            // a new value replaces it and is re-encrypted on save. (Clearing = set Destination to Local, unused.)
            if (!string.IsNullOrEmpty(job.Passphrase))
            {
                existing.Passphrase = job.Passphrase;
                existing.ProtectedPassphrase = null;
                existing.PassphraseHydrationFailed = false;
            }

            existing.SmbPath = string.IsNullOrWhiteSpace(job.SmbPath) ? null : job.SmbPath.Trim();
            existing.SmbUsername = string.IsNullOrWhiteSpace(job.SmbUsername) ? null : job.SmbUsername.Trim();
            existing.SmbDomain = string.IsNullOrWhiteSpace(job.SmbDomain) ? null : job.SmbDomain.Trim();
            // Blank password field = keep the stored one, like the passphrase.
            if (!string.IsNullOrEmpty(job.SmbPassword))
            {
                existing.SmbPassword = job.SmbPassword;
                existing.ProtectedSmbPassword = null;
                existing.SmbPasswordHydrationFailed = false;
            }
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
                // With a backup passphrase a local snapshot is sealed with it like a cloud push: copied off the machine,
                // its credentials restore on any instance.
                SealPortableLocked(package, ResolveBackupPassphraseLocked(null));
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

    /// <summary>Records the outcome of a Cloud-destination backup job run (the HTTP push itself is done by the
    /// scheduler via <c>CloudBackupClient</c>, since the store does no networking). Advances the schedule the same
    /// way <see cref="RunBackupJob"/> does and stamps Last* status - no disk snapshot is written for a cloud job.</summary>
    /// <summary>A run of a job whose snapshot went somewhere else (Matmon.Cloud, an SMB share): advances its schedule and
    /// says how it went; no local artifact.</summary>
    public void RecordRemoteBackupJobRun(Guid jobId, bool success, string message, long? bytes) => RecordCloudBackupJobRun(jobId, success, message, bytes);

    public void RecordCloudBackupJobRun(Guid jobId, bool success, string message, long? bytes)
    {
        lock (_gate)
        {
            EnsureBackupJobsCollection();
            var job = _document.BackupJobs.FirstOrDefault(candidate => candidate.Id == jobId);
            if (job is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            job.LastRunUtc = now;
            job.NextRunUtc = CalculateNextRunUtc(job, now);
            job.LastStatus = success ? "ok" : "error";
            job.LastMessage = string.IsNullOrWhiteSpace(message) ? (success ? "Backed up to Matmon.Cloud." : "Cloud backup failed.") : message.Trim();
            job.LastSnapshotFileName = null; // no local artifact for a cloud push
            job.LastSnapshotBytes = success ? bytes : null;
            QueueSave(SavePriority.Configuration);
        }
    }

    public WorkspaceBackupRestoreResult RestoreBackupSnapshot(string fileName, WorkspaceBackupSection sections, string? passphrase = null)
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
        passphrase = ResolveRestorePassphrase(package, passphrase);

        // Honour a portable (passphrase-sealed) package on the FILE restore path too - previously it always used the
        // instance protector, so a portable .matmonbak imported here dropped every credential behind a "success".
        var secretProtector = ResolveSecretProtector(package, passphrase);

        lock (_gate)
        {
            return ApplyRestoreLocked(package, sections, secretProtector, fileName);
        }
    }

    /// <summary>Builds a backup package in memory and returns its bytes - the exact same JSON the file-based
    /// backup writes, so it stays download-compatible with the upload/restore UI. Used to push a snapshot to
    /// the cloud without a disk artifact.</summary>
    /// <summary>Minimum length for a portable-backup passphrase - it derives the AES key that seals every stored
    /// credential, so a trivially short one is brute-forceable offline against the package's verifier.</summary>
    public const int MinPortablePassphraseLength = 12;

    /// <summary>Builds a package in memory. Without an explicit passphrase the instance's backup passphrase seals it (when one is set).</summary>
    public byte[] CreateBackupBytes(WorkspaceBackupSection sections, string? reason = null, string? passphrase = null)
    {
        if (!string.IsNullOrEmpty(passphrase) && passphrase.Length < MinPortablePassphraseLength)
        {
            throw new ArgumentException($"The backup passphrase must be at least {MinPortablePassphraseLength} characters.", nameof(passphrase));
        }

        lock (_gate)
        {
            var job = new WorkspaceBackupJob
            {
                Name = "cloud",
                Sections = sections == WorkspaceBackupSection.None ? WorkspaceBackupSection.All : sections
            };
            var package = CreateBackupPackageLocked(job, _document, reason);
            SealPortableLocked(package, ResolveBackupPassphraseLocked(passphrase));
            return JsonSerializer.SerializeToUtf8Bytes(package, FileSerializerOptions);
        }
    }

    /// <summary>The passphrase a new package is sealed with: the one asked for, else the instance's backup passphrase.</summary>
    private string? ResolveBackupPassphraseLocked(string? explicitPassphrase) =>
        !string.IsNullOrEmpty(explicitPassphrase) ? explicitPassphrase
        : string.IsNullOrEmpty(_document.BackupSettings?.Passphrase) ? null : _document.BackupSettings.Passphrase;

    /// <summary>A sealed package restores on THIS instance without anyone typing the passphrase when it is the instance's own:
    /// an empty passphrase falls back to the backup passphrase. (A wrong typed one is still refused.)</summary>
    private string? ResolveRestorePassphrase(WorkspaceBackupPackage package, string? passphrase)
    {
        if (!package.SecretsPortable || !string.IsNullOrEmpty(passphrase))
        {
            return passphrase;
        }
        lock (_gate)
        {
            return ResolveBackupPassphraseLocked(null);
        }
    }

    /// <summary>
    /// Portable secrets: re-seals the package's secrets with a passphrase-derived key so a restore on a DIFFERENT instance
    /// recovers credentials too (the default DataProtection ciphertext is instance-bound). The package clone holds instance-DP
    /// ciphertext - decrypt it with the local key, then re-protect with the passphrase key. The live document is untouched.
    /// No passphrase = nothing to do.
    /// </summary>
    private void SealPortableLocked(WorkspaceBackupPackage package, string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            return;
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var portable = new PassphraseSecretProtector(passphrase, salt, PortableSecretsPbkdf2Iterations);
        HydrateCredentialBundles(package.Document, _credentialProtector);
        ProtectCredentialBundles(package.Document, portable);
        package.SecretsPortable = true;
        package.SecretsSalt = Convert.ToBase64String(salt);
        package.SecretsIterations = PortableSecretsPbkdf2Iterations;
        package.SecretsVerifier = Convert.ToBase64String(portable.Protect(Encoding.UTF8.GetBytes(PortableSecretsVerifierPlaintext)));
    }

    /// <summary>Restores from an in-memory backup blob (e.g. downloaded from the cloud) - same effect as
    /// <see cref="RestoreBackupSnapshot"/> but without touching disk.</summary>
    public WorkspaceBackupRestoreResult RestoreBackupBytes(byte[] blob, WorkspaceBackupSection sections, string? passphrase = null)
    {
        if (blob is null || blob.Length == 0)
        {
            throw new ArgumentException("Backup content is required.", nameof(blob));
        }

        if (sections == WorkspaceBackupSection.None)
        {
            throw new ArgumentException("At least one restore section must be selected.", nameof(sections));
        }

        var package = JsonSerializer.Deserialize<WorkspaceBackupPackage>(blob, FileSerializerOptions)
            ?? throw new InvalidOperationException("The backup could not be read.");
        passphrase = ResolveRestorePassphrase(package, passphrase);

        var secretProtector = ResolveSecretProtector(package, passphrase);

        lock (_gate)
        {
            return ApplyRestoreLocked(package, sections, secretProtector, "cloud");
        }
    }

    /// <summary>Resolves the protector used to unseal a package's secrets: the passphrase-derived key for a portable
    /// package (validated up front so a wrong passphrase / corrupt metadata fails cleanly rather than silently
    /// dropping credentials), else null = the instance DataProtection ring. Runs BEFORE the lock, so a bad passphrase
    /// never partially applies a restore.</summary>
    private IDataProtector? ResolveSecretProtector(WorkspaceBackupPackage package, string? passphrase)
    {
        if (!package.SecretsPortable)
        {
            return null; // instance DP ring (only round-trips on the same instance)
        }

        if (string.IsNullOrEmpty(passphrase))
        {
            throw new InvalidOperationException("This backup is passphrase-protected. Enter the passphrase to restore it.");
        }

        // The package fields are untrusted (they come off the wire / disk). Validate them BEFORE deriving the key,
        // so corrupt/tampered metadata fails cleanly and can't turn PBKDF2 into a CPU DoS via a huge iteration count.
        if (package.SecretsIterations is < 1 or > MaxPortableSecretsPbkdf2Iterations
            || string.IsNullOrEmpty(package.SecretsSalt)
            || string.IsNullOrEmpty(package.SecretsVerifier))
        {
            throw new InvalidOperationException("This backup is not a valid portable backup (corrupt metadata).");
        }

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(package.SecretsSalt);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("This backup is not a valid portable backup (corrupt metadata).");
        }

        var portable = new PassphraseSecretProtector(passphrase, salt, package.SecretsIterations);

        // Verify the passphrase up front so a wrong one is rejected cleanly (rather than silently producing
        // undecryptable credentials that then get dropped).
        try
        {
            var check = Encoding.UTF8.GetString(portable.Unprotect(Convert.FromBase64String(package.SecretsVerifier)));
            if (!string.Equals(check, PortableSecretsVerifierPlaintext, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Incorrect passphrase.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Incorrect passphrase.");
        }

        return portable;
    }

    /// <summary>Shared restore body (must hold <c>_gate</c>): hydrate the package's secrets, swap in the selected
    /// sections, re-run the load-time normalisers so an older backup's catalog/invariants are re-established without
    /// a restart, then report counts + any credentials that could not be decrypted (dropped).</summary>
    private WorkspaceBackupRestoreResult ApplyRestoreLocked(WorkspaceBackupPackage package, WorkspaceBackupSection sections, IDataProtector? secretProtector, string fileName)
    {
        // A backup made by a NEWER Matmon may carry shapes this build does not know - restoring it would drop them
        // without a word. Said before anything is touched.
        if (package.Document.SchemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"This backup was made by a newer Matmon (data version {package.Document.SchemaVersion}, this one reads up to {CurrentSchemaVersion}). Update this installation first, then restore it.");
        }

        HydrateCredentialBundles(package.Document, secretProtector);
        var migratedFrom = MigratePackageLocked(package.Document);
        ApplyBackupSections(_document, package.Document, sections);
        NormalizeAfterRestoreLocked();
        QueueSave(SavePriority.Configuration);

        var restoredCount = CountSelectedSections(sections);
        var elements = _document.RootProbe is null
            ? new List<MonitoringElement>()
            : EnumerateElements(_document.RootProbe).ToList();
        var probes = elements.OfType<ProbeElement>().Count();
        var sensors = elements.OfType<SensorElement>().Count();
        var templates = _document.Templates?.Count ?? 0;
        var rules = _document.NotificationRules?.Count ?? 0;
        var users = _document.Users?.Count ?? 0;
        var dropped = CollectDroppedSecretNames(_document);

        var message = $"Restored {restoredCount} section(s): {probes} probe(s), {sensors} sensor(s), {templates} template(s), {rules} rule(s).";
        if (migratedFrom is { } version)
        {
            message += $" The backup was made by an older Matmon (data version {version}) and was brought up to date ({CurrentSchemaVersion}).";
        }
        if (dropped.Count > 0)
        {
            message += $" {dropped.Count} credential(s) could not be decrypted and were dropped - re-enter them.";
        }

        return new WorkspaceBackupRestoreResult(fileName, sections, restoredCount, message, probes, sensors, templates, rules, users, dropped);
    }

    /// <summary>Credential bundles + notification secrets that failed to decrypt on this restore (dropped, must be
    /// re-entered). Scans the live document after the sections were applied.</summary>
    private List<string> CollectDroppedSecretNames(WorkspaceDocument document)
    {
        var names = new List<string>();
        foreach (var settings in EnumerateSettings(document))
        {
            foreach (var credential in settings.Credentials)
            {
                if (credential.HydrationFailed)
                {
                    names.Add(string.IsNullOrWhiteSpace(credential.Name) ? $"credential {credential.Id:N}" : $"credential '{credential.Name}'");
                }
            }
        }
        foreach (var slot in EnumerateNotificationSecrets(document))
        {
            if (slot.GetFailed())
            {
                names.Add("notification secret");
            }
        }
        return names;
    }

    /// <summary>Re-runs the safe subset of the load-time normalisers after a restore so a restored (possibly older)
    /// topology/catalog/template set is migrated + its invariants re-established immediately, not only on the next
    /// boot. Must hold <c>_gate</c>. Deliberately skips one-time + demo/provision seeders.</summary>
    private void NormalizeAfterRestoreLocked()
    {
        // The package was already brought to this build's schema on its own (MigratePackageLocked) - the live
        // document is current, so there is nothing to migrate here, only the invariants to re-establish.
        EnsureSensorDefinitionCatalog();
        EnsureDefaultTemplates();
        EnsureDefaultNotificationConfiguration();
        CloseAlertsOfMissingElements();
        EnsureBackupJobsCollection();
        // A restored backup can reintroduce v0 (grid-cell) map geometry - e.g. an old export, or one that
        // was never opened since v1 shipped - so re-run the same migration a normal load would apply.
        _document.Maps ??= [];
        MigrateMapLayouts();
        EnsureStatusPageTokens();
    }

    /// <summary>
    /// Brings an OLDER backup up to this build's schema before any of it is applied: the package document is migrated
    /// on its own, by the same numbered pipeline a boot runs, from the version the package records (0 for a backup made
    /// before versions existed). It used to be the live document's turn after the sections were swapped in - whose
    /// version is always current, so no migration ever ran and an old backup came back in its old shape (agents without
    /// a host, UPS sensors without the power-cut channels, ...). Migration 1 is left out: it moves telemetry from the
    /// document into the database, and a restore puts the telemetry sections there itself. Returns the version the
    /// package came from when it had to be migrated, else null. Must hold <c>_gate</c>.
    /// </summary>
    private int? MigratePackageLocked(WorkspaceDocument package)
    {
        var from = package.SchemaVersion;
        if (from >= CurrentSchemaVersion)
        {
            return null;
        }

        NormalizeLoadedDocument(package);
        var live = _document;
        _document = package;
        try
        {
            RunSchemaMigrations(skipVersions: [1]);
        }
        finally
        {
            _document = live;
        }

        return from;
    }

    /// <summary>
    /// Migration 5. The job editor used to offer eleven sections, so a job with all eleven ticked meant "everything"
    /// - but it was stored as exactly those eleven, and "everything" has grown since (map images, MIBs). A job
    /// stored as the word "All" picks the new ones up by itself; one stored as the list does not, so it is brought
    /// up to date here. A job the admin narrowed down is left alone, and a cloud job ignores its sections anyway.
    /// </summary>
    private void MigrateBackupJobsToAllSections()
    {
        // The eleven sections the editor offered then - spelled out: derived from today's "All" this stopped matching the
        // day "All" grew again (collectors, syslog messages, general settings).
        const WorkspaceBackupSection original = WorkspaceBackupSection.Topology | WorkspaceBackupSection.Templates
            | WorkspaceBackupSection.SensorDefinitions | WorkspaceBackupSection.Notifications | WorkspaceBackupSection.Maps
            | WorkspaceBackupSection.Users | WorkspaceBackupSection.Alerts | WorkspaceBackupSection.SensorHistory
            | WorkspaceBackupSection.Events | WorkspaceBackupSection.Statistics | WorkspaceBackupSection.BackupJobs;
        foreach (var job in _document.BackupJobs ?? [])
        {
            if (job.Destination == BackupDestination.Local && (job.Sections & original) == original)
            {
                job.Sections |= WorkspaceBackupSection.MapAssets | WorkspaceBackupSection.Mibs;
            }
        }
    }

    /// <summary>
    /// Migration 11. Three sections joined (collectors, syslog messages, general settings). A local job that held every
    /// configuration section of before gets the two new configuration ones; one that held the history too also gets the
    /// syslog messages - so "everything" and "everything but the history" keep meaning what they meant. A job stored as
    /// the word "All" picks them up by itself, and a narrowed one is left alone.
    /// </summary>
    private void MigrateBackupJobsToCollectorSections()
    {
        const WorkspaceBackupSection oldConfig = WorkspaceBackupSection.Topology | WorkspaceBackupSection.Templates
            | WorkspaceBackupSection.SensorDefinitions | WorkspaceBackupSection.Notifications | WorkspaceBackupSection.Maps
            | WorkspaceBackupSection.Users | WorkspaceBackupSection.Alerts | WorkspaceBackupSection.BackupJobs
            | WorkspaceBackupSection.MapAssets | WorkspaceBackupSection.Mibs;
        const WorkspaceBackupSection oldHistory = WorkspaceBackupSection.SensorHistory | WorkspaceBackupSection.Events | WorkspaceBackupSection.Statistics;
        foreach (var job in _document.BackupJobs ?? [])
        {
            if (job.Destination != BackupDestination.Local || (job.Sections & oldConfig) != oldConfig)
            {
                continue;
            }

            job.Sections |= WorkspaceBackupSection.Collectors | WorkspaceBackupSection.GeneralSettings;
            if ((job.Sections & oldHistory) == oldHistory)
            {
                job.Sections |= WorkspaceBackupSection.SyslogMessages;
            }
        }
    }

    /// <summary>Name of the nightly backup on this machine every instance gets once.</summary>
    public const string DefaultLocalJobName = "Nightly backup";

    /// <summary>Name of the nightly cloud backup an instance gets once, when it is first linked to Matmon.Cloud.</summary>
    public const string DefaultCloudJobName = "Nightly cloud backup";

    /// <summary>
    /// Every instance backs itself up without anyone having to think of it: once, a nightly job on this machine (02:30, the
    /// configuration with users, maps and MIBs - no telemetry -, the last 7 kept), and once the instance is linked to
    /// Matmon.Cloud, a nightly cloud job (03:30, the cloud's own retention). Each is set up ONE time (a flag in the backup
    /// settings) - an admin who removed or changed one keeps it that way - and not at all when a job of that destination
    /// exists already. Returns whether it added something. Must hold <c>_gate</c>.
    /// </summary>
    private bool EnsureDefaultBackupJobsLocked(bool cloudConnected)
    {
        EnsureBackupJobsCollection();
        var settings = _document.BackupSettings ??= new();
        var added = false;

        if (!settings.DefaultLocalJobCreated)
        {
            settings.DefaultLocalJobCreated = true;
            if (!_document.BackupJobs.Any(job => job.Destination == BackupDestination.Local))
            {
                _document.BackupJobs.Add(DefaultJob(DefaultLocalJobName, BackupDestination.Local, new TimeSpan(2, 30, 0),
                    WorkspaceBackupSection.All & ~WorkspaceBackupSections.History,
                    "Set up with the instance: the configuration every night, without telemetry."));
            }
            added = true;
        }

        if (cloudConnected && !settings.DefaultCloudJobCreated)
        {
            settings.DefaultCloudJobCreated = true;
            if (!_document.BackupJobs.Any(job => job.Destination == BackupDestination.Cloud))
            {
                _document.BackupJobs.Add(DefaultJob(DefaultCloudJobName, BackupDestination.Cloud, new TimeSpan(3, 30, 0),
                    WorkspaceBackupSections.CloudConfig,
                    "Set up when the instance was linked to Matmon.Cloud: the configuration every night, off-site."));
            }
            added = true;
        }

        if (added)
        {
            QueueSave(SavePriority.Configuration);
        }
        return added;
    }

    private static WorkspaceBackupJob DefaultJob(string name, BackupDestination destination, TimeSpan at, WorkspaceBackupSection sections, string description)
    {
        var job = new WorkspaceBackupJob
        {
            Name = name,
            Description = description,
            Enabled = true,
            Destination = destination,
            Sections = sections,
            RetentionCount = 7,
            Schedule = new MonitoringSchedule { Mode = MonitoringScheduleMode.Daily, TimeOfDay = at }
        };
        NormalizeBackupJob(job);
        return job;
    }

    /// <summary>The nightly cloud backup switched on or off (the setup assistant's cloud step).</summary>
    public void SetNightlyCloudBackup(bool enabled)
    {
        lock (_gate)
        {
            EnsureBackupJobsCollection();
            var job = _document.BackupJobs.FirstOrDefault(candidate => candidate.Destination == BackupDestination.Cloud);
            if (job is null)
            {
                if (!enabled)
                {
                    return;
                }
                (_document.BackupSettings ??= new()).DefaultCloudJobCreated = true;
                job = DefaultJob(DefaultCloudJobName, BackupDestination.Cloud, new TimeSpan(3, 30, 0), WorkspaceBackupSections.CloudConfig,
                    "Set up in the setup assistant: the configuration every night, off-site.");
                _document.BackupJobs.Add(job);
            }
            job.Enabled = enabled;
            job.NextRunUtc = enabled ? CalculateNextRunUtc(job, DateTimeOffset.UtcNow) : job.NextRunUtc;
            QueueSave(SavePriority.Configuration);
        }
    }

    /// <summary>Whether a cloud backup job exists and is switched on.</summary>
    public bool HasNightlyCloudBackup()
    {
        lock (_gate)
        {
            return (_document.BackupJobs ?? []).Any(job => job.Destination == BackupDestination.Cloud && job.Enabled);
        }
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
            StripUnselectedSections(documentClone, job.Sections);

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

            // Uploaded pictures live as files, not in the document, so they are pulled in the same way the
            // telemetry sections are.
            if (job.Sections.HasFlag(WorkspaceBackupSection.MapAssets) && _mapAssets is { } assets)
            {
                documentClone.MapAssets = assets.EnumerateAll()
                    .Select(asset => new WorkspaceMapAsset { Id = asset.Id, Data = Convert.ToBase64String(asset.Bytes) })
                    .ToList();
            }

            // The MIB files an admin uploaded are files too. The standard set is not carried: it ships with the
            // image, so a restore onto any build already has it.
            // The central log lives in its own database; at most this many of its newest messages travel.
            if (job.Sections.HasFlag(WorkspaceBackupSection.SyslogMessages) && _syslog is { } syslog)
            {
                documentClone.SyslogMessages = syslog.ExportNewest(MaxSyslogMessagesInBackup).ToList();
            }

            if (job.Sections.HasFlag(WorkspaceBackupSection.Mibs) && _mibLibrary is { } mibs)
            {
                documentClone.Mibs = mibs.ReadUploadedFiles()
                    .Select(content => new WorkspaceMibFile { Data = Convert.ToBase64String(content) })
                    .ToList();
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

    /// <summary>A package carries ONLY the sections it was created for. The live document is serialised whole (so the
    /// clone is a faithful snapshot), then everything outside the mask is emptied. Without this a "config-only" cloud
    /// backup still shipped every user + password hash, the cloud-link token and the license token - the "Users are
    /// excluded" promise held only on the restore side. Instance-bound state (cloud link identity/status, license
    /// token, cached partner branding) is never part of any section: it is meaningless - or harmful - on another
    /// instance and is always dropped.</summary>
    private void StripUnselectedSections(WorkspaceDocument document, WorkspaceBackupSection sections)
    {
        if (!sections.HasFlag(WorkspaceBackupSection.Topology))
        {
            document.RootProbe = CreatePlainWorkspaceDocument().RootProbe;
        }

        if (!sections.HasFlag(WorkspaceBackupSection.Templates))
        {
            document.Templates = [];
        }

        if (!sections.HasFlag(WorkspaceBackupSection.SensorDefinitions))
        {
            document.SensorDefinitions = [];
        }

        if (!sections.HasFlag(WorkspaceBackupSection.Notifications))
        {
            document.NotificationConfiguration = new();
            document.NotificationSenders = [];
            document.NotificationReceivers = [];
            document.NotificationRules = [];
            document.SummaryReport = new();
            document.MaintenanceWindows = [];
        }

        if (!sections.HasFlag(WorkspaceBackupSection.Maps))
        {
            document.Maps = [];
            document.StatusPages = [];
        }

        if (!sections.HasFlag(WorkspaceBackupSection.Users))
        {
            document.Users = [];
        }

        if (!sections.HasFlag(WorkspaceBackupSection.Alerts))
        {
            document.Alerts = [];
            document.AlertMutes = [];
        }

        if (!sections.HasFlag(WorkspaceBackupSection.MapAssets))
        {
            document.MapAssets.Clear();
        }

        if (!sections.HasFlag(WorkspaceBackupSection.Mibs))
        {
            document.Mibs.Clear();
        }

        // A pending enrolment code belongs to this instance and today: restored elsewhere (or next month) it
        // would let an agent enrol against a workspace whose admin never issued it.
        document.AgentEnrollments = [];

        // An API token opens this instance's API; restored elsewhere it would open that one too.
        document.ApiTokens = [];

        // A browser's push subscription belongs to this instance's VAPID key pair and to the browser - it means nothing anywhere
        // else, and a restore must not make another machine notify someone's phone.
        document.PushDevices = [];

        // The collectors (where they run, ports, allow-lists), the log's retention, the trap communities and v3 users.
        if (!sections.HasFlag(WorkspaceBackupSection.Collectors))
        {
            document.Syslog = new();
            document.SnmpTraps = new();
            document.Plugins = [];
        }

        if (!sections.HasFlag(WorkspaceBackupSection.SyslogMessages))
        {
            document.SyslogMessages = [];
        }

        if (!sections.HasFlag(WorkspaceBackupSection.GeneralSettings))
        {
            document.DisplayTimeZoneId = null;
        }

        // Whether and when this installation updates itself is decided on this machine, like its listeners.
        document.SelfUpdate = new();
        // The backup passphrase: a package never carries the key to itself.
        document.BackupSettings = new();
        document.SelfUpdateOutcome = null;

        // What may be executed on this machine is decided on it (the Local Program allow-list).
        document.AllowedProgramPaths = [];
        // Where this instance is reached belongs to this machine, never to a restore elsewhere.
        document.PublicBaseUrl = null;
        document.LastDirectOrigin = null;
        // ...and the catalog's copy of it, the program choice of the Local Program sensor (rebuilt from the live list on load).
        document.SensorDefinitions = document.SensorDefinitions
            .Select(definition => string.Equals(definition.Key, LocalProgramSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase)
                ? definition with
                {
                    Parameters = definition.Parameters
                        .Select(parameter => parameter.Key == "program.base" ? parameter with { Options = [] } : parameter)
                        .ToList()
                }
                : definition)
            .ToList();

        if (!sections.HasFlag(WorkspaceBackupSection.BackupJobs))
        {
            document.BackupJobs = [];
        }

        // Bound to THIS instance (its DataProtection ring / its cloud identity) - never travels.
        document.CloudSettings = new();
        document.Cloud = new();
        document.LicenseToken = null;
        document.ServicePartner = null;
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
                WorkspaceBackupSection.MapAssets => (
                    document.MapAssets.Count,
                    $"{document.MapAssets.Count} map image(s)"),
                WorkspaceBackupSection.Mibs => (
                    document.Mibs.Count,
                    $"{document.Mibs.Count} MIB file(s)"),
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

    /// <summary>
    /// Where every part of the workspace goes in a backup: the section that carries it, or null with the reason it never
    /// travels (it belongs to this machine). BackupCoverageTests fails the day a property is added to the document without
    /// a line here - a backup that silently leaves something behind is found when it is needed, which is too late.
    /// </summary>
    public static IReadOnlyDictionary<string, (WorkspaceBackupSection? Section, string Why)> BackupCoverage { get; } =
        new Dictionary<string, (WorkspaceBackupSection?, string)>
        {
            ["SchemaVersion"] = (null, "the data version of the package itself - drives the migrations on restore"),
            ["RootProbe"] = (WorkspaceBackupSection.Topology, ""),
            ["Templates"] = (WorkspaceBackupSection.Templates, ""),
            ["SensorDefinitions"] = (WorkspaceBackupSection.SensorDefinitions, ""),
            ["Users"] = (WorkspaceBackupSection.Users, ""),
            ["SetupCompletedUtc"] = (null, "this installation's own setup - a restore never re-runs or skips it"),
            ["Maps"] = (WorkspaceBackupSection.Maps, ""),
            ["StatusPages"] = (WorkspaceBackupSection.Maps, ""),
            ["NotificationConfiguration"] = (WorkspaceBackupSection.Notifications, ""),
            ["SummaryReport"] = (WorkspaceBackupSection.Notifications, ""),
            ["Syslog"] = (WorkspaceBackupSection.Collectors, ""),
            ["SnmpTraps"] = (WorkspaceBackupSection.Collectors, ""),
            ["Plugins"] = (WorkspaceBackupSection.Collectors, ""),
            ["SelfUpdate"] = (null, "whether and when THIS installation updates itself"),
            ["BackupSettings"] = (null, "the backup passphrase - a package never carries the key to itself"),
            ["SelfUpdateOutcome"] = (null, "how this installation's last update ended"),
            ["AllowedProgramPaths"] = (null, "what may be executed is decided on the machine that executes it"),
            ["PublicBaseUrl"] = (null, "where this machine is reached"),
            ["LastDirectOrigin"] = (null, "where this machine was last reached"),
            ["Cloud"] = (null, "this installation's cloud link status"),
            ["CloudSettings"] = (null, "this installation's cloud identity and token"),
            ["LicenseToken"] = (null, "signed for this installation's cloud instance"),
            ["ServicePartner"] = (null, "cached from this installation's cloud link"),
            ["DisplayTimeZoneId"] = (WorkspaceBackupSection.GeneralSettings, ""),
            ["NotificationSenders"] = (WorkspaceBackupSection.Notifications, ""),
            ["NotificationReceivers"] = (WorkspaceBackupSection.Notifications, ""),
            ["NotificationRules"] = (WorkspaceBackupSection.Notifications, ""),
            ["Alerts"] = (WorkspaceBackupSection.Alerts, ""),
            ["AlertMutes"] = (WorkspaceBackupSection.Alerts, ""),
            ["MaintenanceWindows"] = (WorkspaceBackupSection.Notifications, ""),
            ["PushDevices"] = (null, "a browser subscription belongs to this instance's key pair and to that browser"),
            ["AgentEnrollments"] = (null, "short-lived install codes issued here"),
            ["ApiTokens"] = (null, "they open this instance's API"),
            ["BackupJobs"] = (WorkspaceBackupSection.BackupJobs, ""),
            ["SensorHistory"] = (WorkspaceBackupSection.SensorHistory, ""),
            ["Events"] = (WorkspaceBackupSection.Events, ""),
            ["SensorStatistics"] = (WorkspaceBackupSection.Statistics, ""),
            ["MapAssets"] = (WorkspaceBackupSection.MapAssets, ""),
            ["Mibs"] = (WorkspaceBackupSection.Mibs, ""),
            ["SyslogMessages"] = (WorkspaceBackupSection.SyslogMessages, "")
        };

    /// <summary>The syslog messages a backup carries at most (the newest) - the log keeps up to 2 M by default, and a
    /// package of that size would not even load on a small box.</summary>
    public const int MaxSyslogMessagesInBackup = 500_000;

    private void ApplyBackupSections(WorkspaceDocument target, WorkspaceDocument source, WorkspaceBackupSection sections)
    {
        if (sections.HasFlag(WorkspaceBackupSection.Topology))
        {
            var liveTokens = CollectProbeTokens(target.RootProbe);
            target.RootProbe = source.RootProbe;
            KeepLiveProbeTokens(target.RootProbe, liveTokens);
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
            target.MaintenanceWindows = source.MaintenanceWindows ?? [];
            // The scheduled summary report rides the same section - it was packed but never put back.
            target.SummaryReport = source.SummaryReport ?? new();
        }

        if (sections.HasFlag(WorkspaceBackupSection.Maps))
        {
            target.Maps = source.Maps;
            target.StatusPages = source.StatusPages ?? [];
        }

        if (sections.HasFlag(WorkspaceBackupSection.Users))
        {
            target.Users = source.Users;
        }

        if (sections.HasFlag(WorkspaceBackupSection.Alerts))
        {
            target.Alerts = source.Alerts;
            // ...and so do the mutes: packed with the alerts, they used to stay behind on a restore.
            target.AlertMutes = source.AlertMutes ?? [];
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

        if (sections.HasFlag(WorkspaceBackupSection.Collectors))
        {
            target.Syslog = source.Syslog ?? new();
            target.SnmpTraps = source.SnmpTraps ?? new();
            // A collector placed on a probe this workspace does not have (Topology not restored with it) has nowhere to run.
            var probes = EnumerateElements(target.RootProbe).OfType<ProbeElement>().Select(probe => probe.Id).ToHashSet();
            target.Plugins = (source.Plugins ?? []).Where(assignment => probes.Contains(assignment.ProbeElementId)).ToList();
        }

        if (sections.HasFlag(WorkspaceBackupSection.GeneralSettings))
        {
            target.DisplayTimeZoneId = source.DisplayTimeZoneId;
            Matmon.Core.Domain.DisplayTimeZone.SystemDefault = Matmon.Core.Domain.DisplayTimeZone.Resolve(source.DisplayTimeZoneId) ?? TimeZoneInfo.Local;
        }

        if (sections.HasFlag(WorkspaceBackupSection.SyslogMessages) && _syslog is { } syslog)
        {
            syslog.ReplaceAll(source.SyslogMessages ?? []);
        }

        // Written back under their ORIGINAL ids: the tiles restored from the Maps section reference those
        // ids, so a fresh one would orphan every floorplan in the snapshot. MapAssetStore.Restore re-checks
        // the magic bytes, so a tampered package cannot smuggle a script-bearing file into a directory that
        // is served anonymously.
        if (sections.HasFlag(WorkspaceBackupSection.MapAssets) && _mapAssets is { } assetStore)
        {
            foreach (var asset in source.MapAssets)
            {
                if (asset.Id == Guid.Empty || string.IsNullOrWhiteSpace(asset.Data))
                {
                    continue;
                }

                try
                {
                    assetStore.Restore(asset.Id, Convert.FromBase64String(asset.Data));
                }
                catch (FormatException)
                {
                    _logger.LogWarning("Map asset {AssetId} in the backup was not valid base64 and was skipped.", asset.Id);
                }
            }
        }

        // Added to what is there, like the pictures - a restore never deletes a MIB the admin uploaded since. The
        // file name is taken from the MODULE inside each file, never from the package: MibLibrary.Restore runs the
        // same checks as an upload, so a tampered package cannot choose what is written or where.
        if (sections.HasFlag(WorkspaceBackupSection.Mibs) && _mibLibrary is { } library)
        {
            var files = new List<byte[]>();
            foreach (var file in source.Mibs ?? [])
            {
                if (string.IsNullOrWhiteSpace(file.Data))
                {
                    continue;
                }

                try
                {
                    files.Add(Convert.FromBase64String(file.Data));
                }
                catch (FormatException)
                {
                    _logger.LogWarning("A MIB file in the backup was not valid base64 and was skipped.");
                }
            }

            foreach (var rejection in library.Restore(files).Rejected)
            {
                _logger.LogWarning("A MIB in the backup was not restored: {Reason}", rejection);
            }
        }
    }

    /// <summary>The token each probe of the live tree uses right now, by probe id.</summary>
    private static Dictionary<Guid, string> CollectProbeTokens(ProbeElement? root)
    {
        var tokens = new Dictionary<Guid, string>();
        if (root is null)
        {
            return tokens;
        }
        foreach (var probe in EnumerateElements(root).OfType<ProbeElement>())
        {
            if (!string.IsNullOrEmpty(probe.EnrollmentToken))
            {
                tokens[probe.Id] = probe.EnrollmentToken;
            }
        }
        return tokens;
    }

    /// <summary>
    /// Remote probes and agents reconnect after EVERY restore: a probe that is in the restored tree and still exists here keeps
    /// the token it uses today. The backup carries the token of the day it was made - a token rotated since (or a re-enrolled
    /// agent) would otherwise be locked out by restoring an older backup. A probe the live tree no longer has keeps the backup's
    /// token, which is the one that probe still knows when it was only deleted here.
    /// </summary>
    private static void KeepLiveProbeTokens(ProbeElement? root, Dictionary<Guid, string> liveTokens)
    {
        if (root is null || liveTokens.Count == 0)
        {
            return;
        }
        foreach (var probe in EnumerateElements(root).OfType<ProbeElement>())
        {
            if (probe.ParentId is not null && liveTokens.TryGetValue(probe.Id, out var token))
            {
                probe.EnrollmentToken = token;
            }
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

        // Portable secrets: when true, the document's secrets are sealed with a passphrase-derived key
        // (not this instance's DataProtection ring), so they restore onto ANY instance with the passphrase.
        public bool SecretsPortable { get; set; }

        /// <summary>Base64 PBKDF2 salt for the passphrase key (only set when <see cref="SecretsPortable"/>).</summary>
        public string? SecretsSalt { get; set; }

        /// <summary>PBKDF2 iteration count used to derive the passphrase key.</summary>
        public int SecretsIterations { get; set; }

        /// <summary>A known constant sealed with the passphrase key, so a wrong passphrase is detected on restore
        /// before anything is applied (rather than silently yielding undecryptable credentials).</summary>
        public string? SecretsVerifier { get; set; }

        public WorkspaceDocument Document { get; set; } = new();
    }

    // PBKDF2 cost for portable-backup passphrases. OWASP-ballpark for SHA-256; stored in the package so a
    // future bump still lets old backups verify with their own recorded count.
    private const int PortableSecretsPbkdf2Iterations = 210_000;
    // Upper bound accepted on restore for a package's (untrusted) iteration count - generous enough to survive a
    // future default bump, low enough that a crafted count can't turn PBKDF2 into a multi-minute CPU DoS.
    private const int MaxPortableSecretsPbkdf2Iterations = 5_000_000;
    private const string PortableSecretsVerifierPlaintext = "matmon-portable-backup-v1";
}
