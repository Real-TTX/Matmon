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
    All = Topology | Templates | SensorDefinitions | Notifications | Maps | Users | Alerts | SensorHistory | Events | Statistics | BackupJobs
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
    public const WorkspaceBackupSection CloudConfig =
        WorkspaceBackupSection.All & ~(WorkspaceBackupSection.SensorHistory | WorkspaceBackupSection.Events | WorkspaceBackupSection.Statistics | WorkspaceBackupSection.Users);
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
    string Message)
{
    public bool Success => RestoredCount > 0;
}
