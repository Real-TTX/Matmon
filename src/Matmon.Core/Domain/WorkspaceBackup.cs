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

public sealed class WorkspaceBackupJob
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    public MonitoringSchedule Schedule { get; set; } = new();

    public WorkspaceBackupSection Sections { get; set; } = WorkspaceBackupSection.All;

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
