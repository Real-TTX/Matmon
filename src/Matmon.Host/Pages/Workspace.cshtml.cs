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

[Authorize]
public sealed partial class WorkspaceModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly IProbeRegistry _probeRegistry;
    private readonly IDashboardSnapshotProvider _dashboardSnapshotProvider;
    private readonly ISensorExecutionService _sensorExecutionService;
    private readonly MonitoringInheritanceResolver _resolver = new();
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly ILicenseService _licenseService;
    private readonly IServiceScopeFactory _scopeFactory;

    public WorkspaceModel(
        IMonitoringWorkspaceStore workspaceStore,
        IProbeRegistry probeRegistry,
        IDashboardSnapshotProvider dashboardSnapshotProvider,
        ISensorExecutionService sensorExecutionService,
        MatmonRuntimeOptions runtimeOptions,
        ILicenseService licenseService,
        IServiceScopeFactory scopeFactory)
    {
        _workspaceStore = workspaceStore;
        _probeRegistry = probeRegistry;
        _dashboardSnapshotProvider = dashboardSnapshotProvider;
        _sensorExecutionService = sensorExecutionService;
        _runtimeOptions = runtimeOptions;
        _licenseService = licenseService;
        _scopeFactory = scopeFactory;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? SelectedId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? SelectedTemplateId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? SelectedNotificationRuleId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? SelectedNotificationSenderId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? SelectedNotificationReceiverId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? ApplyTemplateId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? MonitoringView { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? MonitoringKind { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? MonitoringState { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? MonitoringSearch { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? MonitoringTag { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? MonitoringSize { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public CreateProbeInput NewProbe { get; set; } = new();

    [BindProperty]
    public CreateFolderInput NewFolder { get; set; } = new();

    [BindProperty]
    public CreateHostInput NewHost { get; set; } = new();

    [BindProperty]
    public CreateSensorInput NewSensor { get; set; } = new();

    [BindProperty]
    public CreateTemplateInput NewTemplate { get; set; } = new();

    [BindProperty]
    public CreateNotificationRuleInput NewNotificationRule { get; set; } = new();

    [BindProperty]
    public TemplateApplyInput TemplateApply { get; set; } = new();

    [BindProperty]
    public WorkspaceElementEditorInput ElementEditor { get; set; } = new();

    [BindProperty]
    public WorkspaceTemplateEditorInput TemplateEditor { get; set; } = new();

    [BindProperty]
    public WorkspaceNotificationRuleEditorInput NotificationRuleEditor { get; set; } = new();

    [BindProperty]
    public CreateNotificationSenderInput NewNotificationSender { get; set; } = new();

    [BindProperty]
    public CreateNotificationReceiverInput NewNotificationReceiver { get; set; } = new();

    [BindProperty]
    public WorkspaceNotificationSenderEditorInput NotificationSenderEditor { get; set; } = new();

    [BindProperty]
    public WorkspaceNotificationReceiverEditorInput NotificationReceiverEditor { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public WorkspacePageViewModel View { get; private set; } = default!;

    public IActionResult OnGet()
    {
        LoadViewState(populateEditorValues: true);
        return Page();
    }

    private void LoadViewState(bool populateEditorValues)
    {
        var dashboardSnapshot = _dashboardSnapshotProvider.CreateSnapshot();
        var dashboardNodeMap = dashboardSnapshot.Nodes.ToDictionary(node => node.Id);
        var telemetrySeriesMap = dashboardSnapshot.TelemetrySeries.ToDictionary(series => series.SensorId);

        var snapshot = _workspaceStore.Workspace;
        var templateMap = snapshot.Templates.ToDictionary(template => template.Id);
        var acknowledgedElementIds = snapshot.Alerts
            .Where(alert => alert.IsActive && alert.IsAcknowledged)
            .Select(alert => alert.ElementId)
            .ToHashSet();
        var nodes = BuildNodeRows(snapshot.RootProbe, templateMap, telemetrySeriesMap, acknowledgedElementIds).ToArray();
        var notificationSenders = BuildNotificationSenderRows(snapshot).ToArray();
        var notificationReceivers = BuildNotificationReceiverRows(snapshot).ToArray();
        var notificationRules = BuildNotificationRuleRows(snapshot, nodes).ToArray();
        var alerts = BuildAlertRows(snapshot).ToArray();
        var probeStatuses = _probeRegistry.GetAll().ToDictionary(probe => probe.ProbeId, StringComparer.OrdinalIgnoreCase);
        var latestSensorObservations = _workspaceStore.GetLatestSensorObservations();
        var now = DateTimeOffset.UtcNow;
        var monitoringViewMode = NormalizeMonitoringViewMode(MonitoringView);
        var monitoringKindFilter = NormalizeMonitoringKindFilter(MonitoringKind);
        var monitoringStateFilter = NormalizeMonitoringStateFilter(MonitoringState);
        var monitoringSearchText = NormalizeMonitoringSearch(MonitoringSearch);
        var monitoringTagFilter = (MonitoringTag ?? string.Empty).Trim();
        var monitoringSize = NormalizeMonitoringSize(MonitoringSize);
        var monitoringFilterPredicate = BuildMonitoringFilterPredicate(monitoringKindFilter, monitoringStateFilter, monitoringTagFilter, monitoringSearchText);
        var monitoringListNodes = nodes
            .Where(monitoringFilterPredicate)
            .OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var monitoringTreeNodes = FilterTreeNodes(nodes, monitoringFilterPredicate).ToArray();
        var monitoringTreeRoots = BuildMonitoringTreeNodes(
            monitoringTreeNodes,
            dashboardNodeMap,
            telemetrySeriesMap,
            latestSensorObservations);
        var monitoringVisibleNodes = monitoringViewMode == "list"
            ? monitoringListNodes
            : monitoringTreeNodes;

        View = new WorkspacePageViewModel
        {
            Snapshot = snapshot,
            Nodes = nodes,
            Alerts = alerts,
            Probes = BuildProbeRows(nodes, probeStatuses, now),
            Templates = BuildTemplateRows(snapshot),
            NotificationSenders = notificationSenders,
            NotificationReceivers = notificationReceivers,
            NotificationRules = notificationRules,
            WorkspacePath = _runtimeOptions.WorkspacePath,
            PrimaryUrl = BuildPrimaryUrl(),
            ProbeCount = nodes.Count(node => node.Kind == MonitoringElementKind.Probe),
            HostCount = nodes.Count(node => node.Kind == MonitoringElementKind.Host),
            SensorCount = nodes.Count(node => node.Kind == MonitoringElementKind.Sensor),
            TemplateCount = snapshot.Templates.Count,
            NotificationSenderCount = snapshot.NotificationSenders.Count,
            NotificationReceiverCount = snapshot.NotificationReceivers.Count,
            NotificationRuleCount = snapshot.NotificationRules.Count,
            ConnectedProbeCount = probeStatuses.Count,
            ActiveAlertCount = alerts.Count(alert => alert.IsActive && !alert.IsAcknowledged),
            AcknowledgedAlertCount = alerts.Count(alert => alert.IsActive && alert.IsAcknowledged),
            PausedSensorCount = nodes.Count(node => node.Kind == MonitoringElementKind.Sensor && node.IsPaused),
            MonitoringViewMode = monitoringViewMode,
            MonitoringKindFilter = monitoringKindFilter,
            MonitoringStateFilter = monitoringStateFilter,
            MonitoringSearch = monitoringSearchText,
            MonitoringTagFilter = monitoringTagFilter,
            MonitoringSize = monitoringSize,
            MonitoringFilterSummary = BuildMonitoringFilterSummary(monitoringKindFilter, monitoringStateFilter, monitoringSearchText, monitoringVisibleNodes.Length, nodes.Length),
            MonitoringTreeNodes = monitoringTreeNodes,
            MonitoringTreeRoots = monitoringTreeRoots,
            MonitoringListNodes = monitoringListNodes,
            MonitoringVisibleNodes = monitoringVisibleNodes,
            MonitoringVisibleCount = monitoringVisibleNodes.Length,
            TelemetrySeries = dashboardSnapshot.TelemetrySeries,
            AllTags = MonitoringTagResolver
                .Normalize(nodes.SelectMany(node => node.Tags).Concat(snapshot.Templates.SelectMany(template => template.Tags)))
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            PickerElements = Matmon.Host.Ui.ElementPickerOptions.Build(snapshot.RootProbe)
        };

        EnsureCreateDefaults(snapshot, nodes, populateEditorValues);

        if (populateEditorValues)
        {
            PopulateElementEditorValues(snapshot, nodes, templateMap, latestSensorObservations);
            PopulateTemplateEditorValues(snapshot);
            PopulateNotificationRuleEditorValues(snapshot, nodes);
            PopulateNotificationSenderEditorValues(snapshot);
            PopulateNotificationReceiverEditorValues(snapshot);
        }

        PopulateCreateOptions(snapshot, nodes, latestSensorObservations);
        PopulateEditorOptions(snapshot, nodes, templateMap, latestSensorObservations);
        PopulateTemplateApplyValues(snapshot, nodes);
    }

    private void PopulateCreateOptions(
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyList<WorkspaceNodeRow> nodes,
        IReadOnlyDictionary<Guid, SensorObservation> latestSensorObservations)
    {
        var probeParents = nodes
            .Where(node => node.Kind == MonitoringElementKind.Probe)
            .Select(node => new SelectListItem($"{node.Kind}: {node.Path}", node.Id.ToString(), node.Id == NewProbe.ParentId))
            .ToList();

        var containerParents = nodes
            .Where(node => node.Kind is MonitoringElementKind.Probe or MonitoringElementKind.Folder)
            .Select(node => new SelectListItem($"{node.Kind}: {node.Path}", node.Id.ToString(), node.Id == NewFolder.ParentId))
            .ToList();

        var hostParents = nodes
            .Where(node => node.Kind is MonitoringElementKind.Probe or MonitoringElementKind.Folder)
            .Select(node => new SelectListItem($"{node.Kind}: {node.Path}", node.Id.ToString(), node.Id == NewHost.ParentId))
            .ToList();

        var sensorParents = nodes
            .Where(node => node.Kind is MonitoringElementKind.Probe or MonitoringElementKind.Folder or MonitoringElementKind.Host)
            .Select(node => new SelectListItem($"{node.Kind}: {node.Path}", node.Id.ToString(), node.Id == NewSensor.ParentId))
            .ToList();

        var templateParents = snapshot.Templates
            .Select(template => new SelectListItem($"{template.Name} ({template.TargetKind})", template.Id.ToString(), template.Id == NewTemplate.ParentTemplateId))
            .ToList();
        var sensorTemplateOptions = BuildSensorTemplateOptions(snapshot.Templates, NewSensor.SensorTypeKey, NewSensor.TemplateId);
        NewSensor.TargetPlaceholder = BuildTargetPlaceholder(NewSensor.ParentId);

        View = View with
        {
            ProbeParentOptions = probeParents,
            FolderParentOptions = containerParents,
            HostParentOptions = hostParents,
            SensorParentOptions = sensorParents,
            TemplateParentOptions = templateParents,
            SensorTypeOptions = BuildSensorTypeOptions(snapshot.SensorDefinitions, NewSensor.SensorTypeKey)
        };
        NewSensor.TemplateOptions = sensorTemplateOptions;

        var selectedSensorTemplate = ResolveSelectedSensorTemplate(snapshot);
        var createCredentialSettings = BuildSensorInheritedSettings(NewSensor, selectedSensorTemplate);
        var sensorDefinition = FindSensorDefinition(snapshot.SensorDefinitions, NewSensor.SensorTypeKey);
        NewSensor.SelectedCredentialId ??= createCredentialSettings.SelectedCredentialId;
        NewSensor.CredentialOptions = BuildCredentialOptions(
            createCredentialSettings.Credentials,
            sensorDefinition?.CredentialKinds ?? [],
            NewSensor.SelectedCredentialId);
        NewSensor.ScheduleInheritedLabel = MonitoringDisplay.FormatScheduleSummary(createCredentialSettings, SensorScheduleDefaults.Resolve(NewSensor.SensorTypeKey));

        PopulateSensorParameterEditor(NewSensor, snapshot.SensorDefinitions);
        PopulateSensorThresholdEditor(NewSensor, snapshot, latestSensorObservations);
        NewNotificationRule.SenderOptions = BuildNotificationSenderOptions(snapshot.NotificationSenders, NewNotificationRule.SenderId);
        NewNotificationRule.ReceiverOptions = BuildNotificationReceiverOptions(snapshot.NotificationReceivers, NewNotificationRule.ReceiverId);
        NewNotificationRule.SenderId ??= snapshot.NotificationSenders.FirstOrDefault()?.Id;
        NewNotificationRule.ReceiverId ??= snapshot.NotificationReceivers.FirstOrDefault()?.Id;
        NewNotificationRule.TargetOptions = BuildNotificationTargetOptions(nodes, NewNotificationRule.TargetElementId);
        NewNotificationRule.TriggerStateOptions = BuildNotificationStateOptions(NewNotificationRule.TriggerStates);
    }

    private void PopulateEditorOptions(
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyList<WorkspaceNodeRow> nodes,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templateMap,
        IReadOnlyDictionary<Guid, SensorObservation> latestSensorObservations)
    {
        var selectedElement = GetSelectedElement(snapshot);
        var selectedTemplate = GetSelectedTemplate(snapshot);
        var selectedElementSettings = selectedElement is null
            ? new MonitoringSettings()
            : ResolveElementEffectiveSettings(selectedElement);
        var selectedTemplateSettings = selectedTemplate is null
            ? new MonitoringSettings()
            : ResolveTemplateEffectiveSettings(selectedTemplate);

        var elementEditor = ElementEditor;
        elementEditor.ParentOptions = BuildElementParentOptions(nodes, selectedElement);
        elementEditor.TargetPlaceholder = string.Equals(elementEditor.Kind, "Sensor", StringComparison.OrdinalIgnoreCase)
            ? BuildTargetPlaceholder(elementEditor.ParentId)
            : null;
        // Copy model: the origin is the selection, not the legacy live-inheritance list (which the
        // load-time migration always empties). Matches how BuildElementEditor seeds the options.
        elementEditor.TemplateOptions = snapshot.Templates
            .Select(template => new SelectListItem($"{template.Name} ({template.TargetKind})", template.Id.ToString(), elementEditor.TemplateOriginId == template.Id))
            .ToList();
        elementEditor.SensorTypeOptions = BuildSensorTypeOptions(snapshot.SensorDefinitions, elementEditor.SensorTypeKey);

        var elementCredentialKinds = selectedElement is SensorElement sensorElementForCredentials
            ? FindSensorDefinition(snapshot.SensorDefinitions, sensorElementForCredentials.SensorTypeKey)?.CredentialKinds ?? []
            : [];
        elementEditor.SelectedCredentialId ??= selectedElementSettings.SelectedCredentialId;
        elementEditor.CredentialOptions = BuildCredentialOptions(
            selectedElementSettings.Credentials,
            elementCredentialKinds,
            elementEditor.SelectedCredentialId);
        PopulateSensorParameterEditor(elementEditor, snapshot.SensorDefinitions);
        PopulateSensorThresholdEditor(elementEditor, snapshot, selectedElement as SensorElement, latestSensorObservations);

        var templateEditor = TemplateEditor;
        templateEditor.ParentOptions = BuildTemplateParentOptions(snapshot.Templates, selectedTemplate);
        templateEditor.ImpactRows = selectedTemplate is null
            ? []
            : BuildTemplateImpactRows(snapshot.RootProbe, selectedTemplate, templateMap);
        var templateCredentialKinds = templateEditor.TargetKind == MonitoringTemplateScope.Sensor
            ? FindSensorDefinition(snapshot.SensorDefinitions, templateEditor.SensorTypeKey)?.CredentialKinds ?? []
            : [];
        templateEditor.SelectedCredentialId ??= selectedTemplateSettings.SelectedCredentialId;
        templateEditor.CredentialOptions = BuildCredentialOptions(
            selectedTemplateSettings.Credentials,
            templateCredentialKinds,
            templateEditor.SelectedCredentialId);
        PopulateTemplateParameterEditor(templateEditor, snapshot);
        PopulateTemplateThresholdEditor(templateEditor, snapshot);

        var notificationRule = GetSelectedNotificationRule(snapshot);
        var notificationSender = GetSelectedNotificationSender(snapshot);
        var notificationReceiver = GetSelectedNotificationReceiver(snapshot);
        NotificationRuleEditor.SenderOptions = BuildNotificationSenderOptions(snapshot.NotificationSenders, NotificationRuleEditor.SenderId);
        NotificationRuleEditor.ReceiverOptions = BuildNotificationReceiverOptions(snapshot.NotificationReceivers, NotificationRuleEditor.ReceiverId);
        NotificationRuleEditor.TargetOptions = BuildNotificationTargetOptions(nodes, NotificationRuleEditor.TargetElementId);
        NotificationRuleEditor.TriggerStateOptions = BuildNotificationStateOptions(NotificationRuleEditor.TriggerStates);

        var notificationTemplateGroups = NotificationTemplateCatalog.GetGroups();
        var notificationPreview = BuildNotificationRulePreview(
            NotificationRuleEditor.Id != Guid.Empty ? NotificationRuleEditor.Name : NewNotificationRule.Name,
            NotificationRuleEditor.Id != Guid.Empty ? NotificationRuleEditor.SubjectTemplate : NewNotificationRule.SubjectTemplate,
            NotificationRuleEditor.Id != Guid.Empty ? NotificationRuleEditor.TextTemplate : NewNotificationRule.TextTemplate,
            NotificationRuleEditor.Id != Guid.Empty ? NotificationRuleEditor.HtmlTemplate : NewNotificationRule.HtmlTemplate,
            NotificationRuleEditor.Id != Guid.Empty ? NotificationRuleEditor.TargetElementId : NewNotificationRule.TargetElementId,
            NotificationRuleEditor.Id != Guid.Empty ? NotificationRuleEditor.IncludeDescendants : NewNotificationRule.IncludeDescendants,
            NotificationRuleEditor.Id != Guid.Empty ? NotificationRuleEditor.TriggerStates : NewNotificationRule.TriggerStates,
            snapshot,
            nodes,
            latestSensorObservations,
            DateTimeOffset.UtcNow);

        View = View with
        {
            SelectedElementKind = selectedElement?.Kind.ToString(),
            SelectedTemplateKind = selectedTemplate?.TargetKind.ToString(),
            SelectedNotificationRuleKind = notificationRule is null ? null : $"{notificationRule.Name}",
            SelectedNotificationSenderKind = notificationSender is null ? null : $"{notificationSender.Name} ({notificationSender.Kind})",
            SelectedNotificationReceiverKind = notificationReceiver is null ? null : $"{notificationReceiver.Name} ({notificationReceiver.Kind})",
            NotificationTemplateGroups = notificationTemplateGroups,
            NotificationRulePreviewSummary = notificationPreview.Summary,
            NotificationRulePreviewSubject = notificationPreview.Subject,
            NotificationRulePreviewText = notificationPreview.Text,
            NotificationRulePreviewHtml = notificationPreview.Html
        };
    }

    private void PopulateTemplateApplyValues(
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyList<WorkspaceNodeRow> nodes)
    {
        var templateId = TemplateApply.TemplateId != Guid.Empty
            ? TemplateApply.TemplateId
            : ApplyTemplateId ?? Guid.Empty;

        var template = templateId == Guid.Empty
            ? null
            : snapshot.Templates.FirstOrDefault(candidate => candidate.Id == templateId);

        TemplateApply.TemplateId = template?.Id ?? Guid.Empty;
        TemplateApply.TargetOptions = BuildTemplateApplyTargetOptions(nodes, template, TemplateApply.TargetElementId);
    }

    private void PopulateElementEditorValues(
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyList<WorkspaceNodeRow> nodes,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templateMap,
        IReadOnlyDictionary<Guid, SensorObservation> latestSensorObservations)
    {
        var selectedElement = GetSelectedElement(snapshot);
        if (selectedElement is null)
        {
            ElementEditor.Id = Guid.Empty;
            ElementEditor.Kind = string.Empty;
            return;
        }

        ElementEditor = BuildElementEditor(selectedElement, nodes, templateMap, latestSensorObservations);
    }

    private void PopulateTemplateEditorValues(MonitoringWorkspaceSnapshot snapshot)
    {
        var selectedTemplate = GetSelectedTemplate(snapshot);
        if (selectedTemplate is null)
        {
            TemplateEditor.Id = Guid.Empty;
            TemplateEditor.Name = string.Empty;
            return;
        }

        TemplateEditor = BuildTemplateEditor(selectedTemplate);
    }

    private void PopulateNotificationRuleEditorValues(MonitoringWorkspaceSnapshot snapshot, IReadOnlyList<WorkspaceNodeRow> nodes)
    {
        var selectedRule = GetSelectedNotificationRule(snapshot);
        if (selectedRule is null)
        {
            NotificationRuleEditor.Id = Guid.Empty;
            NotificationRuleEditor.Name = string.Empty;
            return;
        }

        NotificationRuleEditor = BuildNotificationRuleEditor(selectedRule, snapshot, nodes);
    }

    private void PopulateNotificationSenderEditorValues(MonitoringWorkspaceSnapshot snapshot)
    {
        var selectedSender = GetSelectedNotificationSender(snapshot);
        if (selectedSender is null)
        {
            NotificationSenderEditor.Id = Guid.Empty;
            NotificationSenderEditor.Name = string.Empty;
            return;
        }

        NotificationSenderEditor = BuildNotificationSenderEditor(selectedSender);
    }

    private void PopulateNotificationReceiverEditorValues(MonitoringWorkspaceSnapshot snapshot)
    {
        var selectedReceiver = GetSelectedNotificationReceiver(snapshot);
        if (selectedReceiver is null)
        {
            NotificationReceiverEditor.Id = Guid.Empty;
            NotificationReceiverEditor.Name = string.Empty;
            return;
        }

        NotificationReceiverEditor = BuildNotificationReceiverEditor(selectedReceiver);
    }

    private MonitoringElement? GetSelectedElement(MonitoringWorkspaceSnapshot snapshot)
    {
        if (ElementEditor.Id != Guid.Empty)
        {
            return _workspaceStore.FindElement(ElementEditor.Id);
        }

        if (SelectedId is Guid id)
        {
            return _workspaceStore.FindElement(id);
        }

        return null;
    }

    private MonitoringTemplate? GetSelectedTemplate(MonitoringWorkspaceSnapshot snapshot)
    {
        if (TemplateEditor.Id != Guid.Empty)
        {
            return _workspaceStore.FindTemplate(TemplateEditor.Id);
        }

        if (SelectedTemplateId is Guid id)
        {
            return _workspaceStore.FindTemplate(id);
        }

        return null;
    }

    private NotificationRule? GetSelectedNotificationRule(MonitoringWorkspaceSnapshot snapshot)
    {
        if (NotificationRuleEditor.Id != Guid.Empty)
        {
            return _workspaceStore.FindNotificationRule(NotificationRuleEditor.Id);
        }

        if (SelectedNotificationRuleId is Guid id)
        {
            return _workspaceStore.FindNotificationRule(id);
        }

        return null;
    }

    private NotificationSender? GetSelectedNotificationSender(MonitoringWorkspaceSnapshot snapshot)
    {
        if (NotificationSenderEditor.Id != Guid.Empty)
        {
            return _workspaceStore.FindNotificationSender(NotificationSenderEditor.Id);
        }

        if (SelectedNotificationSenderId is Guid id)
        {
            return _workspaceStore.FindNotificationSender(id);
        }

        return null;
    }

    private NotificationReceiver? GetSelectedNotificationReceiver(MonitoringWorkspaceSnapshot snapshot)
    {
        if (NotificationReceiverEditor.Id != Guid.Empty)
        {
            return _workspaceStore.FindNotificationReceiver(NotificationReceiverEditor.Id);
        }

        if (SelectedNotificationReceiverId is Guid id)
        {
            return _workspaceStore.FindNotificationReceiver(id);
        }

        return null;
    }

    private string? ResolveProbeIdForElement(Guid? elementId)
    {
        if (elementId is not Guid id)
        {
            return null;
        }

        var current = _workspaceStore.FindElement(id);
        while (current is not null)
        {
            if (current is ProbeElement probe)
            {
                return probe.ParentId is null
                    ? null
                    : probe.ProbeId;
            }

            if (current.ParentId is not Guid parentId)
            {
                break;
            }

            current = _workspaceStore.FindElement(parentId);
        }

        return null;
    }

    private static void ApplySettings(
        MonitoringSettings settings,
        string? enabledMode,
        int? pollingIntervalSeconds,
        int? timeoutSeconds,
        int? retryCount,
        bool? highlight,
        string? parametersText,
        string? thresholdsText)
    {
        settings.Enabled = enabledMode switch
        {
            "enabled" => true,
            "disabled" => false,
            _ => null
        };

        settings.PollingInterval = pollingIntervalSeconds is int pollingInterval && pollingInterval > 0
            ? TimeSpan.FromSeconds(pollingInterval)
            : null;

        settings.Timeout = timeoutSeconds is int timeout && timeout > 0
            ? TimeSpan.FromSeconds(timeout)
            : null;

        settings.RetryCount = retryCount;
        settings.Highlight = highlight;

        if (parametersText is not null)
        {
            settings.Parameters.Clear();
            foreach (var (key, value) in ParseKeyValueLines(parametersText))
            {
                settings.Parameters[key] = value;
            }
        }

        if (thresholdsText is not null)
        {
            settings.Thresholds.Clear();
            foreach (var (key, value) in ParseKeyValueLines(thresholdsText))
            {
                settings.Thresholds[key] = value;
            }
        }
    }

    private static void ApplyRetentionSettings(
        MonitoringSettings settings,
        int? eventRetentionDays,
        int? observationRetentionDays,
        int? statisticsRetentionDays,
        int? statisticsBucketMinutes)
    {
        settings.EventRetentionDays = NormalizePositiveInteger(eventRetentionDays);
        settings.ObservationRetentionDays = NormalizePositiveInteger(observationRetentionDays);
        settings.StatisticsRetentionDays = NormalizePositiveInteger(statisticsRetentionDays);
        settings.StatisticsBucketMinutes = NormalizePositiveInteger(statisticsBucketMinutes);
    }

    private static int? NormalizePositiveInteger(int? value)
    {
        return value is int integerValue && integerValue > 0 ? integerValue : null;
    }

    private MonitoringSettings ResolveElementEffectiveSettings(MonitoringElement element)
    {
        var lineage = BuildElementLineage(element);
        var templates = _workspaceStore.Workspace.Templates.ToDictionary(template => template.Id);
        var settings = _resolver.Resolve(lineage, templates);
        if (element is SensorElement sensorElement &&
            FindSensorDefinition(_workspaceStore.Workspace.SensorDefinitions, sensorElement.SensorTypeKey) is { } definition)
        {
            MonitoringSettings.ApplyCredentialValuesForKinds(settings, definition.CredentialKinds);
        }

        return settings;
    }

    private MonitoringSettings ResolveElementInheritedSettings(MonitoringElement element)
    {
        var lineage = BuildElementLineage(element);
        if (lineage.Count <= 1)
        {
            return new MonitoringSettings();
        }

        var templates = _workspaceStore.Workspace.Templates.ToDictionary(template => template.Id);
        return _resolver.Resolve(lineage.Take(lineage.Count - 1).ToList(), templates);
    }

    private MonitoringSettings BuildSensorInheritedSettings(CreateSensorInput editor, MonitoringTemplate? template)
    {
        var sensor = new SensorElement(
            string.IsNullOrWhiteSpace(editor.Name) ? "Sensor" : editor.Name.Trim(),
            editor.SensorTypeKey,
            editor.Target)
        {
            ParentId = editor.ParentId
        };

        sensor.Settings.SelectedCredentialId = editor.SelectedCredentialId;

        if (template is not null)
        {
            sensor.AppliedTemplateIds.Add(template.Id);
        }

        return ResolveElementEffectiveSettings(sensor);
    }

    private MonitoringSettings BuildTransientSensorExecutionSettings(CreateSensorInput editor)
    {
        var selectedTemplate = ResolveSelectedSensorTemplate();
        var sensorTypeKey = string.IsNullOrWhiteSpace(editor.SensorTypeKey)
            ? PingSensorExecutor.Definition.Key
            : editor.SensorTypeKey.Trim();

        var sensor = new SensorElement(
            string.IsNullOrWhiteSpace(editor.Name) ? "Sensor" : editor.Name.Trim(),
            sensorTypeKey,
            editor.Target)
        {
            ParentId = editor.ParentId
        };

        sensor.Settings.SelectedCredentialId = editor.SelectedCredentialId;

        if (selectedTemplate is not null)
        {
            sensor.AppliedTemplateIds.Add(selectedTemplate.Id);
        }

        var effectiveSettings = ResolveElementEffectiveSettings(sensor);
        var overrideSettings = BuildSensorExecutionSettings(editor);
        ApplyOverrideSettings(effectiveSettings, overrideSettings);
        return effectiveSettings;
    }

    private MonitoringSettings BuildTransientSensorExecutionSettings(WorkspaceElementEditorInput editor)
    {
        var sensorTypeKey = string.IsNullOrWhiteSpace(editor.SensorTypeKey)
            ? editor.Kind == "Sensor" ? editor.SensorTypeKey ?? PingSensorExecutor.Definition.Key : PingSensorExecutor.Definition.Key
            : editor.SensorTypeKey.Trim();

        var sensor = new SensorElement(
            string.IsNullOrWhiteSpace(editor.Name) ? "Sensor" : editor.Name.Trim(),
            sensorTypeKey,
            editor.Target ?? string.Empty)
        {
            ParentId = editor.ParentId
        };

        sensor.Settings.SelectedCredentialId = editor.SelectedCredentialId;

        foreach (var templateId in editor.AppliedTemplateIds.Distinct())
        {
            sensor.AppliedTemplateIds.Add(templateId);
        }

        var effectiveSettings = ResolveElementEffectiveSettings(sensor);
        var overrideSettings = BuildSensorExecutionSettings(editor);
        ApplyOverrideSettings(effectiveSettings, overrideSettings);
        return effectiveSettings;
    }

    private string ResolveEffectiveSensorTarget(CreateSensorInput editor)
    {
        var sensorTypeKey = string.IsNullOrWhiteSpace(editor.SensorTypeKey)
            ? PingSensorExecutor.Definition.Key
            : editor.SensorTypeKey.Trim();
        var sensor = new SensorElement(
            string.IsNullOrWhiteSpace(editor.Name) ? "Sensor" : editor.Name.Trim(),
            sensorTypeKey,
            editor.Target)
        {
            ParentId = editor.ParentId
        };

        return SensorTargetResolver.Resolve(sensor, BuildElementLineage(sensor));
    }

    private string ResolveEffectiveSensorTarget(WorkspaceElementEditorInput editor, SensorElement existingSensor)
    {
        var sensorTypeKey = string.IsNullOrWhiteSpace(editor.SensorTypeKey)
            ? existingSensor.SensorTypeKey
            : editor.SensorTypeKey.Trim();
        var sensor = new SensorElement(
            string.IsNullOrWhiteSpace(editor.Name) ? existingSensor.Name : editor.Name.Trim(),
            sensorTypeKey,
            editor.Target ?? existingSensor.Target)
        {
            ParentId = editor.ParentId ?? existingSensor.ParentId
        };

        return SensorTargetResolver.Resolve(sensor, BuildElementLineage(sensor));
    }

    private string ResolveEffectiveSensorTarget(SensorElement sensor)
    {
        return SensorTargetResolver.Resolve(sensor, BuildElementLineage(sensor));
    }

    private string BuildTargetPlaceholder(Guid? parentId)
    {
        if (parentId is null)
        {
            return "Target";
        }

        var sensor = new SensorElement("Target preview", PingSensorExecutor.Definition.Key, string.Empty)
        {
            ParentId = parentId
        };
        var inheritedTarget = SensorTargetResolver.ResolveInheritedHostAddress(BuildElementLineage(sensor));
        return string.IsNullOrWhiteSpace(inheritedTarget)
            ? "Target"
            : $"inherit host: {inheritedTarget}";
    }

    private IReadOnlyList<MonitoringElement> BuildElementLineage(MonitoringElement element)
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

            current = _workspaceStore.FindElement(parentId)
                ?? throw new InvalidOperationException($"Parent element '{parentId}' could not be found.");
        }

        lineage.Reverse();
        return lineage;
    }

    private MonitoringSettings BuildSensorExecutionSettings(CreateSensorInput editor)
    {
        var settings = new MonitoringSettings();

        settings.Highlight = editor.Highlight;
        ApplyScheduleSettings(
            settings,
            editor.SchedulePreset,
            editor.ScheduleEveryValue,
            editor.ScheduleEveryUnit,
            editor.ScheduleDaysOfWeek,
            editor.ScheduleDayOfMonth,
            editor.ScheduleTime);
        var definition = RequireSensorDefinition(editor.SensorTypeKey);
        ApplySensorParameters(settings, definition, editor.SensorParameterFields, editor.SensorAdvancedParametersText);
        ApplySensorChannelThresholds(settings, editor.SensorChannelThresholdFields);
        return settings;
    }

    private MonitoringSettings BuildSensorExecutionSettings(WorkspaceElementEditorInput editor)
    {
        var settings = new MonitoringSettings();
        if (string.Equals(editor.Kind, "Sensor", StringComparison.OrdinalIgnoreCase))
        {
            ApplySettings(settings, editor.EnabledMode, editor.PollingIntervalSeconds, editor.TimeoutSeconds, editor.RetryCount, editor.Highlight, null, null);
        }
        else
        {
            ApplySettings(settings, editor.EnabledMode, editor.PollingIntervalSeconds, editor.TimeoutSeconds, editor.RetryCount, null, null, editor.ThresholdsText);
        }

        ApplyScheduleSettings(
            settings,
            editor.SchedulePreset,
            editor.ScheduleEveryValue,
            editor.ScheduleEveryUnit,
            editor.ScheduleDaysOfWeek,
            editor.ScheduleDayOfMonth,
            editor.ScheduleTime);

        ApplyRetentionSettings(
            settings,
            editor.EventRetentionDays,
            editor.ObservationRetentionDays,
            editor.StatisticsRetentionDays,
            editor.StatisticsBucketMinutes);

        var definition = RequireSensorDefinition(editor.SensorTypeKey);
        var existingSensorValues = editor.Id != Guid.Empty && _workspaceStore.FindElement(editor.Id) is SensorElement existingSensor
            ? ResolveElementEffectiveSettings(existingSensor).Parameters
            : null;
        ApplySensorParameters(
            settings,
            definition,
            editor.SensorParameterFields,
            editor.SensorAdvancedParametersText,
            existingSensorValues);
        ApplySensorChannelThresholds(settings, editor.SensorChannelThresholdFields);
        return settings;
    }

    private static string FormatInheritedEnabledLabel(bool? value)
    {
        return value switch
        {
            true => "enabled",
            false => "disabled",
            _ => "inherit"
        };
    }

    private static string FormatInheritedBooleanLabel(bool? value)
    {
        return value switch
        {
            true => "on",
            false => "off",
            _ => "inherit"
        };
    }

    private static string? FormatSecondsPlaceholder(TimeSpan? value)
    {
        return value is TimeSpan duration
            ? ((int)Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string? FormatNullableIntPlaceholder(int? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatSensorStateLabel(SensorState state)
    {
        return state switch
        {
            SensorState.Healthy => "OK",
            SensorState.Warning => "Warning",
            SensorState.Critical => "Error",
            SensorState.Unknown => "No data",
            SensorState.Disabled => "Disabled",
            SensorState.Paused => "Paused",
            _ => state.ToString()
        };
    }

    private string BuildProbeBootstrapSnippet(string? probeId, string probeName, string? probeToken)
    {
        if (string.IsNullOrWhiteSpace(probeId))
        {
            return "Probe ID fehlt.";
        }

        var primaryUrl = BuildPrimaryUrl();
        return $"""
Matmon__Mode=Secondary
Matmon__ProbeId={probeId}
Matmon__ProbeName={probeName}
Matmon__ProbeToken={probeToken ?? "token-here"}
Matmon__PrimaryUrl={primaryUrl}
Matmon__HeartbeatIntervalSeconds={_runtimeOptions.HeartbeatIntervalSeconds}
Matmon__WorkspacePath={_runtimeOptions.WorkspacePath}
""";
    }

    private string BuildPrimaryUrl()
    {
        if (!string.IsNullOrWhiteSpace(_runtimeOptions.PrimaryUrl))
        {
            return _runtimeOptions.PrimaryUrl;
        }

        if (Request is not null)
        {
            return $"{Request.Scheme}://{Request.Host}";
        }

        return "http://localhost:8099";
    }

    private static string ToLines(IDictionary<string, string> values)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            values.Select(pair => $"{pair.Key}={EscapeKeyValuePart(pair.Value)}"));
    }

    private static IReadOnlyDictionary<string, string> ParseKeyValueLines(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return values;
        }

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith('#'))
            {
                continue;
            }

            var separator = rawLine.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = rawLine[..separator].Trim();
            var value = UnescapeKeyValuePart(rawLine[(separator + 1)..].Trim());
            if (!string.IsNullOrWhiteSpace(key))
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static string EscapeKeyValuePart(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string UnescapeKeyValuePart(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                switch (next)
                {
                    case 'n':
                        builder.Append('\n');
                        i++;
                        continue;
                    case 'r':
                        builder.Append('\r');
                        i++;
                        continue;
                    case '\\':
                        builder.Append('\\');
                        i++;
                        continue;
                }
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private IActionResult RedirectAfterAction(string? returnUrl, string fallbackPage, object? routeValues = null)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToPage(fallbackPage, routeValues);
    }

    public string GetSafeReturnUrl(string fallbackPage)
    {
        return !string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? ReturnUrl
            : fallbackPage;
    }

    /// <summary>
    /// The element-picker options restricted to the ids in <paramref name="validOptions"/>
    /// (the legacy SelectListItem set), preserving tree order/depth. Lets the create/edit
    /// pages swap a parent/target &lt;select&gt; for the searchable picker without re-deriving
    /// the per-context valid set.
    /// </summary>
    public IReadOnlyList<Matmon.Host.Ui.ElementPickerOption> PickerOptionsFor(IEnumerable<SelectListItem> validOptions)
    {
        var ids = validOptions
            .Select(option => option.Value)
            .Where(value => Guid.TryParse(value, out _))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return View.PickerElements.Where(option => ids.Contains(option.Id.ToString())).ToList();
    }

    private static Guid RequireParentId(Guid? parentId, string label)
    {
        return parentId ?? throw new InvalidOperationException($"{label} parent is required.");
    }

    private SensorDefinition RequireSensorDefinition(string? sensorTypeKey)
    {
        return FindSensorDefinition(_workspaceStore.Workspace.SensorDefinitions, sensorTypeKey)
            ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(sensorTypeKey)
                ? "Sensor type is required."
                : $"Unknown sensor type '{sensorTypeKey}'.");
    }

    private static SensorDefinition? FindSensorDefinition(
        IReadOnlyList<SensorDefinition> sensorDefinitions,
        string? sensorTypeKey)
    {
        if (string.IsNullOrWhiteSpace(sensorTypeKey))
        {
            return null;
        }

        return sensorDefinitions.FirstOrDefault(definition =>
            string.Equals(definition.Key, sensorTypeKey, StringComparison.OrdinalIgnoreCase));
    }

    private SensorChannelMode GetSensorChannelMode(string? sensorTypeKey)
    {
        return FindSensorDefinition(_workspaceStore.Workspace.SensorDefinitions, sensorTypeKey)?.ChannelMode
            ?? SensorChannelMode.Dynamic;
    }

    public IReadOnlyList<WorkspaceNodeRow> GetDirectSensorChildren(IReadOnlyList<WorkspaceNodeRow> nodes, int parentIndex)
    {
        if (parentIndex < 0 || parentIndex >= nodes.Count)
        {
            return [];
        }

        var parentDepth = nodes[parentIndex].Depth;
        var directSensors = new List<WorkspaceNodeRow>();

        for (var index = parentIndex + 1; index < nodes.Count; index++)
        {
            var candidate = nodes[index];

            if (candidate.Depth <= parentDepth)
            {
                break;
            }

            if (candidate.Depth == parentDepth + 1 && candidate.Kind == MonitoringElementKind.Sensor)
            {
                directSensors.Add(candidate);
            }
        }

        return directSensors;
    }
}

