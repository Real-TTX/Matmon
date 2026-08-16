using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Matmon.Core.Domain;
using Matmon.Core.Sample;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Matmon.Host.Ui;

namespace Matmon.Host.Pages;

public sealed record WorkspacePageViewModel
{
    public MonitoringWorkspaceSnapshot Snapshot { get; init; } = default!;

    public IReadOnlyList<WorkspaceNodeRow> Nodes { get; init; } = [];

    /// <summary>All distinct tags in use (elements + templates), for tag-input autocomplete.</summary>
    public IReadOnlyList<string> AllTags { get; init; } = [];

    /// <summary>The whole topology as element-picker options (tree order + depth), filtered per use.</summary>
    public IReadOnlyList<Matmon.Host.Ui.ElementPickerOption> PickerElements { get; init; } = [];

    public IReadOnlyList<WorkspaceAlertRow> Alerts { get; init; } = [];

    public IReadOnlyList<WorkspaceProbeRow> Probes { get; init; } = [];

    public IReadOnlyList<WorkspaceTemplateRow> Templates { get; init; } = [];

    public IReadOnlyList<WorkspaceNotificationRuleRow> NotificationRules { get; init; } = [];

    public IReadOnlyList<WorkspaceNotificationSenderRow> NotificationSenders { get; init; } = [];

    public IReadOnlyList<WorkspaceNotificationReceiverRow> NotificationReceivers { get; init; } = [];

    public IReadOnlyList<NotificationTemplatePlaceholderGroup> NotificationTemplateGroups { get; init; } = [];

    public string NotificationRulePreviewSummary { get; init; } = string.Empty;

    public string NotificationRulePreviewSubject { get; init; } = string.Empty;

    public string NotificationRulePreviewText { get; init; } = string.Empty;

    public string NotificationRulePreviewHtml { get; init; } = string.Empty;

    public IReadOnlyList<WorkspaceNodeRow> MonitoringTreeNodes { get; init; } = [];

    public IReadOnlyList<WorkspaceMonitoringTreeNode> MonitoringTreeRoots { get; init; } = [];

    public IReadOnlyList<WorkspaceNodeRow> MonitoringListNodes { get; init; } = [];

    public IReadOnlyList<WorkspaceNodeRow> MonitoringVisibleNodes { get; init; } = [];

    public string MonitoringViewMode { get; init; } = "tree";

    public string MonitoringKindFilter { get; init; } = "all";

    public string MonitoringStateFilter { get; init; } = "all";

    public string MonitoringSearch { get; init; } = string.Empty;

    public string MonitoringTagFilter { get; init; } = string.Empty;

    public string MonitoringSize { get; init; } = "m";

    public string MonitoringFilterSummary { get; init; } = string.Empty;

    public int MonitoringVisibleCount { get; init; }

    public IReadOnlyList<TelemetrySeriesSnapshot> TelemetrySeries { get; init; } = [];

    public string WorkspacePath { get; init; } = string.Empty;

    public string PrimaryUrl { get; init; } = string.Empty;

    public int ProbeCount { get; init; }

    public int HostCount { get; init; }

    public int SensorCount { get; init; }

    public int TemplateCount { get; init; }

    public int NotificationRuleCount { get; init; }

    public int NotificationSenderCount { get; init; }

    public int NotificationReceiverCount { get; init; }

    public int ConnectedProbeCount { get; init; }

    public int ActiveAlertCount { get; init; }

    public int AcknowledgedAlertCount { get; init; }

    public int PausedSensorCount { get; init; }

    public string? SelectedElementKind { get; init; }

    public string? SelectedTemplateKind { get; init; }

    public string? SelectedNotificationRuleKind { get; init; }

    public string? SelectedNotificationSenderKind { get; init; }

    public string? SelectedNotificationReceiverKind { get; init; }

    public List<SelectListItem> ProbeParentOptions { get; init; } = [];

    public List<SelectListItem> FolderParentOptions { get; init; } = [];

    public List<SelectListItem> HostParentOptions { get; init; } = [];

    public List<SelectListItem> SensorParentOptions { get; init; } = [];

    public List<SelectListItem> TemplateParentOptions { get; init; } = [];

    public List<SelectListItem> SensorTypeOptions { get; init; } = [];
}

public sealed record WorkspaceNodeRow(
    Guid Id,
    MonitoringElementKind Kind,
    string KindIconKey,
    string Name,
    int Depth,
    Guid? ParentId,
    string Path,
    string Details,
    string SettingsSummary,
    string TemplateSummary,
    string? ProbeId,
    string? EnrollmentToken,
    string? Address,
    string? SensorTypeKey,
    string? Target,
    bool IsHighlighted,
    bool IsPaused,
    string? StateKey,
    string? StateLabel,
    string? StateMessage,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> OwnTags,
    bool IsAcknowledged);

public sealed class WorkspaceMonitoringTreeNode
{
    public Guid Id { get; init; }

    public MonitoringElementKind Kind { get; init; }

    public string KindIconKey { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public int Depth { get; init; }

    public string Path { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    public string SettingsSummary { get; init; } = string.Empty;

    public string TemplateSummary { get; init; } = string.Empty;

    public string? ProbeId { get; init; }

    public string? EnrollmentToken { get; init; }

    public string? Address { get; init; }

    public string? SensorTypeKey { get; init; }

    public string? Target { get; init; }

    public bool IsHighlighted { get; init; }

    public bool IsPaused { get; init; }

    public bool IsAcknowledged { get; init; }

    public string StateKey { get; init; } = string.Empty;

    public string StateLabel { get; init; } = string.Empty;

    public string StateColor { get; init; } = string.Empty;

    public string? StateMessage { get; init; }

    public double? CurrentValue { get; init; }

    public string? Unit { get; init; }

    public string? LastCheck { get; init; }

    public int SensorCount { get; init; }

    public int WarningCount { get; init; }

    public int ErrorCount { get; init; }

    public int ChildCount { get; init; }

    public string? SeriesKey { get; init; }

    public string? SeriesLineColor { get; init; }

    public int SeriesPointCount { get; init; }

    public string? SensorTypeLabel { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<WorkspaceMonitoringTreeNode> Children { get; init; } = [];
}

public sealed record WorkspaceAlertRow(
    Guid Id,
    Guid ElementId,
    MonitoringElementKind ElementKind,
    string ElementName,
    string ElementPath,
    SensorState State,
    string StateKey,
    string StateLabel,
    string Message,
    string FirstSeen,
    string LastSeen,
    bool IsActive,
    bool IsAcknowledged,
    bool IsRecovered,
    string? AcknowledgedAt,
    string? AcknowledgedBy,
    string? RecoveredAt,
    string? ResolvedAt,
    long FirstSeenSortKey,
    long LastSeenSortKey);

public sealed record WorkspaceProbeRow(
    Guid Id,
    string Name,
    string ProbeId,
    string EnrollmentToken,
    string Status,
    string LastSeen,
    string Message,
    string BootstrapSnippet);

public sealed record WorkspaceTemplateRow(
    Guid Id,
    string Name,
    string Scope,
    string ScopeKey,
    string Summary,
    string? ParentName,
    string? SensorTypeKey,
    string? SensorTypeLabel,
    bool IsSensorTemplate,
    int ImpactCount,
    int DirectImpactCount,
    int InheritedImpactCount,
    int ParameterCount,
    int ThresholdCount,
    int CredentialCount);

public sealed record WorkspaceNotificationRuleRow(
    Guid Id,
    string Name,
    bool Enabled,
    string Sender,
    string Receiver,
    string Target,
    string Triggers,
    string Cooldown,
    string Channel);

public sealed record WorkspaceNotificationSenderRow(
    Guid Id,
    string Name,
    bool Enabled,
    string Kind,
    string Summary);

public sealed record WorkspaceNotificationReceiverRow(
    Guid Id,
    string Name,
    bool Enabled,
    string Kind,
    string Target,
    string Summary,
    bool IsBuiltIn = false);

public sealed record TemplateImpactRow(
    Guid SensorId,
    string SensorName,
    string SensorPath,
    string SensorTypeKey,
    MonitoringElementKind AppliedOnKind,
    string AppliedOnName,
    string AppliedOnPath,
    string ImpactKind);
