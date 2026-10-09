using System.Text.Json.Serialization;

namespace Matmon.Core.Domain;

[Flags]
public enum WorkspaceBackupSection
{
    None = 0,
    Topology = 1 << 0,
    Templates = 1 << 1,
    SensorDefinitions = 1 << 2,
    Notifications = 1 << 3,
    Maps = 1 << 4,
    Users = 1 << 5,
    Alerts = 1 << 6,
    SensorHistory = 1 << 7,
    Events = 1 << 8,
    Statistics = 1 << 9,
    BackupJobs = 1 << 10,

    /// <summary>The PICTURES uploaded onto maps (floorplans etc.). Separate from <see cref="Maps"/> because
    /// they are binary and bulky: a map restores its pins and layout from Maps alone, but without this its
    /// floorplan comes back blank.</summary>
    MapAssets = 1 << 11,

    /// <summary>The SNMP MIB files an admin uploaded. The standard set ships with the image, so it belongs to the
    /// build rather than to the data. Files on disk like <see cref="MapAssets"/>, and a library of vendor MIBs
    /// can be large.</summary>
    Mibs = 1 << 12,
    All = Topology | Templates | SensorDefinitions | Notifications | Maps | Users | Alerts | SensorHistory | Events | Statistics | BackupJobs | MapAssets | Mibs
}

/// <summary>Where a scheduled backup job writes its snapshot: a local disk file (default) or a push to the
/// linked Matmon.Cloud (off-site config backup - config sections only, cloud enforces its own retention).</summary>
public enum BackupDestination
{
    Local = 0,
    Cloud = 1
}

/// <summary>Well-known section masks shared across the cloud-backup paths (Config tab, scheduler, wizard restore).</summary>
public static class WorkspaceBackupSections
{
    /// <summary>The section set pushed to / restored from the cloud: everything EXCEPT the bulky telemetry
    /// sections AND local Users. Users are excluded so a cross-instance / DR restore can never overwrite the
    /// local accounts and lock out the admin doing the restore.</summary>
    /// <para>MapAssets and Mibs are excluded too: they are UPLOADED FILES - megabytes of pictures, and a vendor MIB
    /// library can run to tens of megabytes - and a nightly cloud job would re-upload them on every run. A local
    /// backup carries them.</para>
    public const WorkspaceBackupSection CloudConfig =
        WorkspaceBackupSection.All & ~(WorkspaceBackupSection.SensorHistory | WorkspaceBackupSection.Events | WorkspaceBackupSection.Statistics | WorkspaceBackupSection.Users | WorkspaceBackupSection.MapAssets | WorkspaceBackupSection.Mibs);
}

public sealed class WorkspaceBackupJob
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    public MonitoringSchedule Schedule { get; set; } = new();

    public WorkspaceBackupSection Sections { get; set; } = WorkspaceBackupSection.All;

    /// <summary>Local disk (default) or a push to the linked Matmon.Cloud. A cloud job always sends the
    /// config-only section set (no telemetry, no local users) regardless of <see cref="Sections"/>, and the
    /// cloud keeps its own newest-N retention.</summary>
    public BackupDestination Destination { get; set; } = BackupDestination.Local;

    public int RetentionCount { get; set; } = 10;

    /// <summary>Optional passphrase for a CLOUD job so its pushed snapshot is portable (recoverable on a DIFFERENT
    /// instance). Plaintext is transient and DataProtection-encrypted at rest into <see cref="ProtectedPassphrase"/>
    /// (never written to workspace.json in the clear), mirroring the notification-secret lifecycle.</summary>
    [JsonIgnore]
    public string? Passphrase { get; set; }

    /// <summary>DataProtection ciphertext of <see cref="Passphrase"/> (the persisted form).</summary>
    public string? ProtectedPassphrase { get; set; }

    /// <summary>Set when the stored <see cref="ProtectedPassphrase"/> could not be decrypted on load (e.g. missing
    /// DP keys) so a save doesn't overwrite the ciphertext with an empty value.</summary>
    [JsonIgnore]
    public bool PassphraseHydrationFailed { get; set; }

    public DateTimeOffset? LastRunUtc { get; set; }

    public DateTimeOffset? NextRunUtc { get; set; }

    public string? LastStatus { get; set; }

    public string? LastMessage { get; set; }

    public string? LastSnapshotFileName { get; set; }

    public long? LastSnapshotBytes { get; set; }

    public WorkspaceBackupJob Clone()
    {
        return new WorkspaceBackupJob
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Enabled = Enabled,
            Schedule = Schedule.Clone(),
            Sections = Sections,
            Destination = Destination,
            RetentionCount = RetentionCount,
            Passphrase = Passphrase,
            ProtectedPassphrase = ProtectedPassphrase,
            PassphraseHydrationFailed = PassphraseHydrationFailed,
            LastRunUtc = LastRunUtc,
            NextRunUtc = NextRunUtc,
            LastStatus = LastStatus,
            LastMessage = LastMessage,
            LastSnapshotFileName = LastSnapshotFileName,
            LastSnapshotBytes = LastSnapshotBytes
        };
    }
}

public sealed record WorkspaceBackupSnapshotInfo(
    string FileName,
    string DisplayName,
    Guid? JobId,
    string? JobName,
    string? Description,
    DateTimeOffset CreatedUtc,
    long Bytes,
    WorkspaceBackupSection Sections,
    int ProbeCount,
    int SensorCount,
    int TemplateCount,
    int UserCount,
    int AlertCount,
    int SensorHistoryCount,
    int EventCount,
    int StatisticsCount)
{
    public string SectionsLabel => Sections == WorkspaceBackupSection.All
        ? "All"
        : string.Join(", ", Enum.GetValues<WorkspaceBackupSection>()
            .Where(section => section is not WorkspaceBackupSection.None and not WorkspaceBackupSection.All && Sections.HasFlag(section))
            .Select(section => section.ToString()));
}

public sealed record WorkspaceBackupSectionPreview(
    WorkspaceBackupSection Section,
    string Label,
    string Description,
    string Summary,
    int ItemCount,
    bool Included);

public sealed record WorkspaceBackupSnapshotDetails(
    WorkspaceBackupSnapshotInfo Snapshot,
    IReadOnlyList<WorkspaceBackupSectionPreview> Sections);

public sealed record WorkspaceBackupRestoreResult(
    string FileName,
    WorkspaceBackupSection RestoredSections,
    int RestoredCount,
    string Message,
    int Probes = 0,
    int Sensors = 0,
    int Templates = 0,
    int Rules = 0,
    int Users = 0,
    IReadOnlyList<string>? DroppedSecrets = null)
{
    public bool Success => RestoredCount > 0;

    /// <summary>Credential bundles / notification secrets that could not be decrypted on restore (e.g. a
    /// cross-instance restore with no portable passphrase) and were therefore dropped - the user must re-enter them.</summary>
    public IReadOnlyList<string> DroppedSecretsOrEmpty => DroppedSecrets ?? [];
}

/// <summary>An uploaded map picture inside a backup package. Transport only: the live copy lives on disk in
/// the MapAssetStore, never in workspace.json - which is why this list is always empty outside a snapshot,
/// exactly like the telemetry sections.</summary>
public sealed class WorkspaceMapAsset
{
    public Guid Id { get; set; }

    /// <summary>Base64 image bytes. Re-validated by magic bytes on restore, so a tampered package cannot
    /// smuggle a script-bearing file into a directory that is served anonymously.</summary>
    public string? Data { get; set; }
}

/// <summary>An uploaded MIB file inside a backup package. Transport only, like <see cref="WorkspaceMapAsset"/>: the
/// live files are in data/mibs, so this list is always empty outside a snapshot. Only the BYTES travel, never a
/// file name - a restore names the file after the module inside it, exactly as an upload does, so a tampered
/// package has no path to write to.</summary>
public sealed class WorkspaceMibFile
{
    /// <summary>Base64 of the file as stored, byte for byte - a vendor MIB can be Latin-1, which would not
    /// survive being carried as JSON text.</summary>
    public string? Data { get; set; }
}
