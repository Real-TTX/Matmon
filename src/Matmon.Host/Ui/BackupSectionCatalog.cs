using Matmon.Core.Domain;

namespace Matmon.Host.Ui;

public sealed class BackupSectionSelectionModel
{
    public bool Topology { get; set; } = true;

    public bool Templates { get; set; } = true;

    public bool SensorDefinitions { get; set; } = true;

    public bool Notifications { get; set; } = true;

    public bool Maps { get; set; } = true;

    public bool Users { get; set; } = true;

    public bool Alerts { get; set; } = true;

    public bool SensorHistory { get; set; } = true;

    public bool Events { get; set; } = true;

    public bool Statistics { get; set; } = true;

    public bool BackupJobs { get; set; } = true;

    public bool HasAnySelected()
    {
        return Topology
            || Templates
            || SensorDefinitions
            || Notifications
            || Maps
            || Users
            || Alerts
            || SensorHistory
            || Events
            || Statistics
            || BackupJobs;
    }

    public void ApplySections(WorkspaceBackupSection sections)
    {
        Topology = sections.HasFlag(WorkspaceBackupSection.Topology);
        Templates = sections.HasFlag(WorkspaceBackupSection.Templates);
        SensorDefinitions = sections.HasFlag(WorkspaceBackupSection.SensorDefinitions);
        Notifications = sections.HasFlag(WorkspaceBackupSection.Notifications);
        Maps = sections.HasFlag(WorkspaceBackupSection.Maps);
        Users = sections.HasFlag(WorkspaceBackupSection.Users);
        Alerts = sections.HasFlag(WorkspaceBackupSection.Alerts);
        SensorHistory = sections.HasFlag(WorkspaceBackupSection.SensorHistory);
        Events = sections.HasFlag(WorkspaceBackupSection.Events);
        Statistics = sections.HasFlag(WorkspaceBackupSection.Statistics);
        BackupJobs = sections.HasFlag(WorkspaceBackupSection.BackupJobs);
    }

    public WorkspaceBackupSection ToSections(bool defaultToAll = true)
    {
        var sections = WorkspaceBackupSection.None;
        if (Topology) sections |= WorkspaceBackupSection.Topology;
        if (Templates) sections |= WorkspaceBackupSection.Templates;
        if (SensorDefinitions) sections |= WorkspaceBackupSection.SensorDefinitions;
        if (Notifications) sections |= WorkspaceBackupSection.Notifications;
        if (Maps) sections |= WorkspaceBackupSection.Maps;
        if (Users) sections |= WorkspaceBackupSection.Users;
        if (Alerts) sections |= WorkspaceBackupSection.Alerts;
        if (SensorHistory) sections |= WorkspaceBackupSection.SensorHistory;
        if (Events) sections |= WorkspaceBackupSection.Events;
        if (Statistics) sections |= WorkspaceBackupSection.Statistics;
        if (BackupJobs) sections |= WorkspaceBackupSection.BackupJobs;

        if (sections == WorkspaceBackupSection.None && defaultToAll)
        {
            return WorkspaceBackupSection.All;
        }

        return sections;
    }
}

public sealed record BackupSectionChoice(WorkspaceBackupSection Section, string Label);

public static class BackupSectionCatalog
{
    private static readonly BackupSectionChoice[] Choices =
    [
        new(WorkspaceBackupSection.Topology, "Topology"),
        new(WorkspaceBackupSection.Templates, "Templates"),
        new(WorkspaceBackupSection.SensorDefinitions, "Sensor defs"),
        new(WorkspaceBackupSection.Notifications, "Notifications"),
        new(WorkspaceBackupSection.Maps, "Maps"),
        new(WorkspaceBackupSection.Users, "Users"),
        new(WorkspaceBackupSection.Alerts, "Alerts"),
        new(WorkspaceBackupSection.SensorHistory, "History"),
        new(WorkspaceBackupSection.Events, "Events"),
        new(WorkspaceBackupSection.Statistics, "Statistics"),
        new(WorkspaceBackupSection.BackupJobs, "Backup jobs")
    ];

    public static IReadOnlyList<BackupSectionChoice> GetChoices()
    {
        return Choices;
    }

    public static string Format(WorkspaceBackupSection sections)
    {
        if (sections == WorkspaceBackupSection.All)
        {
            return "All";
        }

        var labels = Choices
            .Where(choice => choice.Section != WorkspaceBackupSection.All && choice.Section != WorkspaceBackupSection.None && sections.HasFlag(choice.Section))
            .Select(choice => choice.Label)
            .ToArray();

        return labels.Length == 0
            ? "None"
            : string.Join(", ", labels);
    }
}
