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

namespace Matmon.Host.Pages;

[Authorize]
public sealed class WorkspaceModel : PageModel
{
    /// <summary>
    /// Threshold comparison operators offered in channel/threshold dropdowns.
    /// Values are the symbols the backend parser expects (see TryParseThresholdComparison).
    /// </summary>
    public static IReadOnlyList<SelectListItem> ThresholdComparisonOptions { get; } =
    [
        new SelectListItem("above ( > )", ">"),
        new SelectListItem("at or above ( ≥ )", ">="),
        new SelectListItem("below ( < )", "<"),
        new SelectListItem("at or below ( ≤ )", "<="),
        new SelectListItem("equal ( = )", "="),
        new SelectListItem("not equal ( ≠ )", "<>")
    ];

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly IProbeRegistry _probeRegistry;
    private readonly IDashboardSnapshotProvider _dashboardSnapshotProvider;
    private readonly ISensorExecutionService _sensorExecutionService;
    private readonly MonitoringInheritanceResolver _resolver = new();
    private readonly MatmonRuntimeOptions _runtimeOptions;

    public WorkspaceModel(
        IMonitoringWorkspaceStore workspaceStore,
        IProbeRegistry probeRegistry,
        IDashboardSnapshotProvider dashboardSnapshotProvider,
        ISensorExecutionService sensorExecutionService,
        MatmonRuntimeOptions runtimeOptions)
    {
        _workspaceStore = workspaceStore;
        _probeRegistry = probeRegistry;
        _dashboardSnapshotProvider = dashboardSnapshotProvider;
        _sensorExecutionService = sensorExecutionService;
        _runtimeOptions = runtimeOptions;
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

    public IActionResult OnPostCreateProbe()
    {
        try
        {
            var probe = _workspaceStore.CreateProbe(NewProbe.ParentId, NewProbe.Name, NewProbe.Description);
            StatusMessage = $"Probe '{probe.Name}' angelegt. Install script is ready.";
            var backUrl = GetSafeReturnUrl("/Config?tab=probes");
            return RedirectToPage("/ProbeInstall", new { probeId = probe.Id, returnUrl = backUrl });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostCreateFolder()
    {
        try
        {
            var folder = _workspaceStore.CreateFolder(RequireParentId(NewFolder.ParentId, "Folder"), NewFolder.Name, NewFolder.Description);
            StatusMessage = $"Folder '{folder.Name}' angelegt.";
            return RedirectAfterAction(ReturnUrl, "/Monitoring");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostCreateHost()
    {
        try
        {
            var host = _workspaceStore.CreateHost(RequireParentId(NewHost.ParentId, "Host"), NewHost.Name, NewHost.Address, NewHost.Description);
            StatusMessage = $"Host '{host.Name}' angelegt.";
            return RedirectAfterAction(ReturnUrl, "/Monitoring");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostCreateSensor()
    {
        try
        {
            NormalizeHeartbeatSensorTarget(NewSensor);

            ApplySnmpWalkSelectionsToParameters(NewSensor);
            var selectedTemplate = ResolveSelectedSensorTemplate();
            var inheritedSettings = BuildSensorInheritedSettings(NewSensor, selectedTemplate);
            var createSettings = BuildTransientSensorExecutionSettings(NewSensor);
            MonitoringSettings.StripInheritedValues(createSettings, inheritedSettings);

            var sensor = _workspaceStore.CreateSensor(
                RequireParentId(NewSensor.ParentId, "Sensor"),
                NewSensor.Name,
                NewSensor.SensorTypeKey,
                NewSensor.Target,
                NewSensor.Description,
                createSettings);

            sensor.Tags = MonitoringTagResolver.Parse(NewSensor.TagsText);

            if (selectedTemplate is not null)
            {
                // Copy the template's values into the new sensor (the form values the user saw/edited
                // win) and remember the origin so it can be restored later — no live link. Template
                // tags merge into the user's tags inside ApplyTemplateCopy.
                ApplyTemplateCopy(sensor, selectedTemplate, elementWins: true);
            }

            _workspaceStore.Save();

            StatusMessage = $"Sensor '{sensor.Name}' angelegt.";
            return RedirectAfterAction(ReturnUrl, "/Monitoring");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostPreviewSensorFields()
    {
        try
        {
            LoadViewState(populateEditorValues: false);
            StatusMessage = null;
            ErrorMessage = null;
            // The form is redisplayed with values the server just recomputed (suggested
            // name, applied template defaults, rebuilt channel/parameter fields). Tag
            // helpers prefer the posted ModelState over the model on redisplay, which
            // would mask those changes (e.g. the name would stay "Ping 3" after switching
            // the type to HTTP), so drop ModelState and render straight from the model.
            ModelState.Clear();
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            ModelState.Clear();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDiscoverSnmpAsync()
    {
        try
        {
            if (!IsSnmpWalkSensorType(NewSensor.SensorTypeKey))
            {
                throw new InvalidOperationException("SNMP discovery is only available for SNMP and Synology sensors.");
            }

            var rootOid = string.IsNullOrWhiteSpace(NewSensor.SnmpWalkRootOid)
                ? "1.3.6.1.2.1"
                : NewSensor.SnmpWalkRootOid.Trim();
            var previousSelections = GetSelectedSnmpWalkOids(NewSensor.SnmpWalkItems);
            var discoverySettings = BuildTransientSensorExecutionSettings(NewSensor);
            var discoveryTarget = ResolveEffectiveSensorTarget(NewSensor);
            var discovered = await SnmpSensorExecutor.DiscoverAsync(
                discoveryTarget,
                discoverySettings,
                rootOid,
                TimeSpan.FromSeconds(5),
                HttpContext.RequestAborted);

            NewSensor.SnmpWalkItems = discovered
                .Select(item => new WorkspaceSnmpWalkItemInput
                {
                    Selected = item.SelectedByDefault || previousSelections.Contains(item.Oid.Trim().TrimStart('.')),
                    Oid = item.Oid,
                    Syntax = item.Syntax,
                    Value = item.Value,
                    IsNumeric = item.IsNumeric
                })
                .ToList();

            ApplySnmpWalkSelectionsToParameters(NewSensor);

            StatusMessage = discovered.Count == 0
                ? "No OIDs discovered."
                : $"Discovered {discovered.Count} OIDs.";

            LoadViewState(populateEditorValues: false);
            // Render the rebuilt walk items / applied parameters from the model, not the
            // stale posted ModelState (see OnPostPreviewSensorFields).
            ModelState.Clear();
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            ModelState.Clear();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRunSensorNowAsync()
    {
        try
        {
            SensorExecutionResult result;

            if (ElementEditor.Id != Guid.Empty)
            {
                if (_workspaceStore.FindElement(ElementEditor.Id) is not SensorElement sensor)
                {
                    throw new InvalidOperationException("Selected element is not a sensor.");
                }

                var executionSettings = BuildTransientSensorExecutionSettings(ElementEditor);
                var executionTarget = ResolveEffectiveSensorTarget(ElementEditor, sensor);
                result = await _sensorExecutionService.ExecuteTransientAsync(ElementEditor.SensorTypeKey ?? sensor.SensorTypeKey, executionTarget, executionSettings, HttpContext.RequestAborted);

                var previewState = BuildSensorThresholdEditorState(
                    ElementEditor.SensorTypeKey ?? sensor.SensorTypeKey,
                    GetSensorChannelMode(ElementEditor.SensorTypeKey ?? sensor.SensorTypeKey),
                    executionSettings,
                    ElementEditor.SensorChannelThresholdFields,
                    result.Channels);
                ElementEditor.SensorChannelThresholdFields = previewState.Fields;
                ElementEditor.SensorChannelThresholdVisibleCount = previewState.VisibleCount;
            }
            else
            {
                NormalizeHeartbeatSensorTarget(NewSensor);
                ApplySnmpWalkSelectionsToParameters(NewSensor);
                var executionSettings = BuildTransientSensorExecutionSettings(NewSensor);
                var executionTarget = ResolveEffectiveSensorTarget(NewSensor);
                result = await _sensorExecutionService.ExecuteTransientAsync(NewSensor.SensorTypeKey, executionTarget, executionSettings, HttpContext.RequestAborted);

                var previewState = BuildSensorThresholdEditorState(
                    NewSensor.SensorTypeKey,
                    GetSensorChannelMode(NewSensor.SensorTypeKey),
                    executionSettings,
                    NewSensor.SensorChannelThresholdFields,
                    result.Channels);
                NewSensor.SensorChannelThresholdFields = previewState.Fields;
                NewSensor.SensorChannelThresholdVisibleCount = previewState.VisibleCount;
            }

            StatusMessage = $"Test: {FormatSensorStateLabel(result.State)} - check {result.Duration.TotalMilliseconds:0.#} ms"
                + (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $" - {result.Message}");

            LoadViewState(populateEditorValues: false);
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostToggleSensorPause(Guid sensorId, string? returnUrl)
    {
        try
        {
            var sensor = _workspaceStore.FindElement(sensorId) as SensorElement
                ?? throw new InvalidOperationException("Selected element is not a sensor.");

            var paused = !sensor.IsPaused;
            if (!_workspaceStore.SetSensorPaused(sensor.Id, paused))
            {
                throw new InvalidOperationException("Sensor could not be updated.");
            }

            StatusMessage = paused ? $"Sensor '{sensor.Name}' paused." : $"Sensor '{sensor.Name}' resumed.";
            return RedirectAfterAction(returnUrl, "/ElementEditor", new { selectedId = sensor.Id });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostPauseElement(Guid elementId, bool paused, string? returnUrl)
    {
        try
        {
            var element = _workspaceStore.FindElement(elementId)
                ?? throw new InvalidOperationException("Element not found.");

            var count = _workspaceStore.SetElementPaused(elementId, paused);
            var noun = count == 1 ? "sensor" : "sensors";
            StatusMessage = paused
                ? $"Paused {count} {noun} under '{element.Name}'."
                : $"Resumed {count} {noun} under '{element.Name}'.";
            return RedirectAfterAction(returnUrl, "/ElementEditor", new { selectedId = elementId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostMoveElement(Guid elementId, Guid parentId, string? returnUrl)
    {
        try
        {
            if (!_workspaceStore.MoveElement(elementId, parentId))
            {
                throw new InvalidOperationException("Element could not be moved.");
            }

            var element = _workspaceStore.FindElement(elementId)
                ?? throw new InvalidOperationException("Element not found after move.");

            var credentialIssueCount = RecordSensorCredentialConfigurationIssues(element);
            StatusMessage = credentialIssueCount == 0
                ? $"Element '{element.Name}' moved."
                : $"Element '{element.Name}' moved. {credentialIssueCount} sensor credential issue{(credentialIssueCount == 1 ? string.Empty : "s")} found.";
            return RedirectAfterAction(returnUrl, "/Monitoring");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostMoveElementBefore(Guid elementId, Guid siblingId, string? returnUrl)
    {
        return MoveElementRelative(elementId, siblingId, before: true, returnUrl);
    }

    public IActionResult OnPostMoveElementAfter(Guid elementId, Guid siblingId, string? returnUrl)
    {
        return MoveElementRelative(elementId, siblingId, before: false, returnUrl);
    }

    private int RecordSensorCredentialConfigurationIssues(MonitoringElement element)
    {
        var issueCount = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var sensor in EnumerateSubtree(element).OfType<SensorElement>())
        {
            if (sensor.IsPaused)
            {
                continue;
            }

            var effectiveSettings = ResolveElementEffectiveSettings(sensor);
            var definition = FindSensorDefinition(_workspaceStore.Workspace.SensorDefinitions, sensor.SensorTypeKey);
            if (definition is null)
            {
                continue;
            }

            if (!TryBuildCredentialIssueMessage(sensor, definition, effectiveSettings, out var message))
            {
                continue;
            }

            _workspaceStore.RecordSensorObservation(
                sensor.Id,
                SensorExecutionResult.Critical(TimeSpan.Zero, message),
                now,
                effectiveSettings);
            issueCount++;
        }

        return issueCount;
    }

    private IActionResult MoveElementRelative(Guid elementId, Guid siblingId, bool before, string? returnUrl)
    {
        try
        {
            var moved = before
                ? _workspaceStore.MoveElementBefore(elementId, siblingId)
                : _workspaceStore.MoveElementAfter(elementId, siblingId);

            if (!moved)
            {
                throw new InvalidOperationException("Element could not be reordered.");
            }

            var element = _workspaceStore.FindElement(elementId)
                ?? throw new InvalidOperationException("Element not found after reorder.");

            var credentialIssueCount = RecordSensorCredentialConfigurationIssues(element);
            var direction = before ? "before" : "after";
            StatusMessage = credentialIssueCount == 0
                ? $"Element '{element.Name}' moved {direction}."
                : $"Element '{element.Name}' moved {direction}. {credentialIssueCount} sensor credential issue{(credentialIssueCount == 1 ? string.Empty : "s")} found.";
            return RedirectAfterAction(returnUrl, "/Monitoring");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    private static bool TryBuildCredentialIssueMessage(
        SensorElement sensor,
        SensorDefinition definition,
        MonitoringSettings settings,
        out string message)
    {
        var issues = new List<string>();

        foreach (var parameter in definition.Parameters.Where(parameter => parameter.IsCredential && parameter.Required))
        {
            if (!MonitoringSettings.TryReadParameter(settings, parameter.Key, out _))
            {
                issues.Add($"{parameter.Label} is missing");
            }
        }

        AddConditionalCredentialIssues(sensor, settings, issues);

        if (issues.Count == 0)
        {
            message = string.Empty;
            return false;
        }

        message = $"Credential check after move failed: {string.Join("; ", issues.Distinct(StringComparer.OrdinalIgnoreCase))}";
        return true;
    }

    private static void AddConditionalCredentialIssues(
        SensorElement sensor,
        MonitoringSettings settings,
        List<string> issues)
    {
        if ((string.Equals(sensor.SensorTypeKey, SnmpSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(sensor.SensorTypeKey, SynologyNasSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase)) &&
            MonitoringSettings.TryReadParameter(settings, "snmp.version", out var snmpVersion) &&
            string.Equals(snmpVersion, "v3", StringComparison.OrdinalIgnoreCase))
        {
            if (!MonitoringSettings.TryReadParameter(settings, "snmp.v3.username", out _))
            {
                issues.Add("SNMPv3 username is missing");
            }

            var authProtocol = MonitoringSettings.TryReadParameter(settings, "snmp.v3.authProtocol", out var configuredAuthProtocol)
                ? configuredAuthProtocol
                : "none";
            if (!string.Equals(authProtocol, "none", StringComparison.OrdinalIgnoreCase) &&
                !MonitoringSettings.TryReadParameter(settings, "snmp.v3.authPassword", out _))
            {
                issues.Add("SNMPv3 auth password is missing");
            }

            var privacyProtocol = MonitoringSettings.TryReadParameter(settings, "snmp.v3.privProtocol", out var configuredPrivacyProtocol)
                ? configuredPrivacyProtocol
                : "none";
            if (!string.Equals(privacyProtocol, "none", StringComparison.OrdinalIgnoreCase) &&
                !MonitoringSettings.TryReadParameter(settings, "snmp.v3.privPassword", out _))
            {
                issues.Add("SNMPv3 privacy password is missing");
            }
        }
    }

    private static IEnumerable<MonitoringElement> EnumerateSubtree(MonitoringElement element)
    {
        yield return element;

        if (element is not MonitoringContainerElement container)
        {
            yield break;
        }

        foreach (var child in container.Children)
        {
            foreach (var descendant in EnumerateSubtree(child))
            {
                yield return descendant;
            }
        }
    }

    public IActionResult OnPostCreateTemplate()
    {
        try
        {
            var template = _workspaceStore.CreateTemplate(
                NewTemplate.Name,
                NewTemplate.TargetKind,
                NewTemplate.ParentTemplateId);

            template.SensorTypeKey = NewTemplate.TargetKind == MonitoringTemplateScope.Sensor
                ? RequireSensorDefinition(NewTemplate.SensorTypeKey).Key
                : null;

            _workspaceStore.Save();

            StatusMessage = $"Template '{template.Name}' angelegt.";
            return RedirectToPage("/TemplateEditor", new { selectedTemplateId = template.Id, returnUrl = ReturnUrl });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostCreateNotificationRule()
    {
        NotificationRule? createdRule = null;

        try
        {
            createdRule = _workspaceStore.CreateNotificationRule(NewNotificationRule.Name);
            ApplyNotificationRuleEditor(createdRule, NewNotificationRule);
            SynchronizeNotificationRuleLegacyFields(createdRule);
            _workspaceStore.Save();
            StatusMessage = $"Notification rule '{createdRule.Name}' angelegt.";
            return RedirectAfterAction(ReturnUrl, "/Notifications");
        }
        catch (Exception ex)
        {
            if (createdRule is not null)
            {
                try
                {
                    _workspaceStore.DeleteNotificationRule(createdRule.Id);
                }
                catch
                {
                    // Ignore cleanup failures and surface the original error.
                }
            }

            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostSaveElement()
    {
        try
        {
            if (ElementEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Kein Element ausgewählt.");
            }

            var element = _workspaceStore.FindElement(ElementEditor.Id)
                ?? throw new InvalidOperationException("Element nicht gefunden.");

            var credentialIssueCount = ApplyElementEditor(element, ElementEditor);

            _workspaceStore.Save();
            StatusMessage = credentialIssueCount == 0
                ? $"{element.Kind} '{element.Name}' gespeichert."
                : $"{element.Kind} '{element.Name}' gespeichert. {credentialIssueCount} credential issue{(credentialIssueCount == 1 ? string.Empty : "s")} found.";
            return RedirectToPage(new { selectedId = element.Id, selectedTemplateId = SelectedTemplateId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostReapplyElementTemplate()
    {
        try
        {
            var element = _workspaceStore.FindElement(ElementEditor.Id)
                ?? throw new InvalidOperationException("Element nicht gefunden.");

            if (element.TemplateOriginId is not Guid originId)
            {
                throw new InvalidOperationException("Dieses Element hat kein Herkunfts-Template.");
            }

            var template = _workspaceStore.FindTemplate(originId)
                ?? throw new InvalidOperationException("Das Herkunfts-Template existiert nicht mehr.");

            ApplyTemplateCopy(element, template, elementWins: false);
            _workspaceStore.Save();
            StatusMessage = $"'{element.Name}' aus Template '{template.Name}' wiederhergestellt.";
            return RedirectToPage(new { selectedId = element.Id, selectedTemplateId = SelectedTemplateId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostDetachElementTemplate()
    {
        try
        {
            var element = _workspaceStore.FindElement(ElementEditor.Id)
                ?? throw new InvalidOperationException("Element nicht gefunden.");

            element.TemplateOriginId = null;
            _workspaceStore.Save();
            StatusMessage = $"Template-Herkunft von '{element.Name}' gelöst.";
            return RedirectToPage(new { selectedId = element.Id, selectedTemplateId = SelectedTemplateId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostRotateToken()
    {
        try
        {
            if (ElementEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Kein Probe-Element ausgewählt.");
            }

            var element = _workspaceStore.FindElement(ElementEditor.Id)
                ?? throw new InvalidOperationException("Element nicht gefunden.");

            if (element is not ProbeElement probe)
            {
                throw new InvalidOperationException("Token kann nur bei Probes rotiert werden.");
            }

            _workspaceStore.RotateProbeToken(probe.Id);
            StatusMessage = $"Token für '{probe.Name}' rotiert.";
            return RedirectToPage(new { selectedId = probe.Id, selectedTemplateId = SelectedTemplateId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostDeleteElement()
    {
        try
        {
            if (ElementEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Kein Element ausgewählt.");
            }

            var element = _workspaceStore.FindElement(ElementEditor.Id)
                ?? throw new InvalidOperationException("Element nicht gefunden.");

            if (!_workspaceStore.DeleteElement(element.Id))
            {
                throw new InvalidOperationException("Element konnte nicht gelöscht werden.");
            }

            StatusMessage = $"Element '{element.Name}' gelöscht.";
            if (element is MonitoringElement monitoringElement)
            {
                return RedirectToPage("/Monitoring", new
                {
                    selectedId = monitoringElement.ParentId ?? _workspaceStore.Workspace.RootProbe.Id,
                    monitoringView = MonitoringView,
                    monitoringKind = MonitoringKind,
                    monitoringState = MonitoringState,
                    monitoringSearch = MonitoringSearch,
                    monitoringTag = MonitoringTag,
                    monitoringSize = MonitoringSize
                });
            }

            return RedirectToPage(new { selectedTemplateId = SelectedTemplateId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostSaveTemplate()
    {
        try
        {
            if (TemplateEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Kein Template ausgewählt.");
            }

            var template = _workspaceStore.FindTemplate(TemplateEditor.Id)
                ?? throw new InvalidOperationException("Template nicht gefunden.");
            var templateMap = _workspaceStore.Workspace.Templates.ToDictionary(candidate => candidate.Id);
            var impactedSensors = BuildTemplateImpactRows(_workspaceStore.Workspace.RootProbe, template, templateMap).Count;

            ApplyTemplateEditor(template, TemplateEditor);

            _workspaceStore.Save();
            StatusMessage = impactedSensors == 0
                ? $"Template '{template.Name}' gespeichert. Keine Sensoren betroffen."
                : $"Template '{template.Name}' gespeichert. {impactedSensors} Sensor{(impactedSensors == 1 ? string.Empty : "en")} betroffen.";
            return RedirectToPage(new { selectedId = SelectedId, selectedTemplateId = template.Id });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostDeleteTemplate()
    {
        try
        {
            if (TemplateEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Kein Template ausgewählt.");
            }

            var template = _workspaceStore.FindTemplate(TemplateEditor.Id)
                ?? throw new InvalidOperationException("Template nicht gefunden.");

            if (!_workspaceStore.DeleteTemplate(template.Id))
            {
                throw new InvalidOperationException("Template konnte nicht gelöscht werden.");
            }

            StatusMessage = $"Template '{template.Name}' gelöscht.";
            return RedirectToPage(new { selectedId = SelectedId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostSaveNotificationRule()
    {
        try
        {
            if (NotificationRuleEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Kein Notification Rule ausgewählt.");
            }

            var rule = _workspaceStore.FindNotificationRule(NotificationRuleEditor.Id)
                ?? throw new InvalidOperationException("Notification Rule nicht gefunden.");

            ApplyNotificationRuleEditor(rule, NotificationRuleEditor);
            SynchronizeNotificationRuleLegacyFields(rule);
            _workspaceStore.Save();
            StatusMessage = $"Notification Rule '{rule.Name}' gespeichert.";
            return RedirectToPage(new { selectedNotificationRuleId = rule.Id, selectedId = SelectedId, selectedTemplateId = SelectedTemplateId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostDeleteNotificationRule()
    {
        try
        {
            if (NotificationRuleEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Kein Notification Rule ausgewählt.");
            }

            var rule = _workspaceStore.FindNotificationRule(NotificationRuleEditor.Id)
                ?? throw new InvalidOperationException("Notification Rule nicht gefunden.");

            if (!_workspaceStore.DeleteNotificationRule(rule.Id))
            {
                throw new InvalidOperationException("Notification Rule konnte nicht gelöscht werden.");
            }

            StatusMessage = $"Notification Rule '{rule.Name}' gelöscht.";
            return RedirectToPage(new { selectedId = SelectedId, selectedTemplateId = SelectedTemplateId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostAcknowledgeAlert(Guid alertId, string? returnUrl)
    {
        try
        {
            if (!_workspaceStore.AcknowledgeAlert(alertId, User.Identity?.Name))
            {
                throw new InvalidOperationException("Alert not found.");
            }

            StatusMessage = "Alert confirmed.";
            return RedirectAfterAction(returnUrl, "/Alerts");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostAcknowledgeAlerts(Guid[] alertIds, string? returnUrl)
    {
        try
        {
            var ids = (alertIds ?? []).Distinct().ToArray();
            if (ids.Length == 0)
            {
                throw new InvalidOperationException("No alerts selected.");
            }

            var acknowledged = ids.Count(id => _workspaceStore.AcknowledgeAlert(id, User.Identity?.Name));

            StatusMessage = acknowledged == 1
                ? "Alert confirmed."
                : $"{acknowledged} alerts confirmed.";
            return RedirectAfterAction(returnUrl, "/Alerts");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostCreateNotificationSender()
    {
        NotificationSender? createdSender = null;

        try
        {
            createdSender = _workspaceStore.CreateNotificationSender(NewNotificationSender.Name);
            ApplyNotificationSenderEditor(createdSender, NewNotificationSender);
            _workspaceStore.Save();
            StatusMessage = $"Notification sender '{createdSender.Name}' angelegt.";
            return RedirectAfterAction(ReturnUrl, "/NotificationSettings");
        }
        catch (Exception ex)
        {
            if (createdSender is not null)
            {
                try
                {
                    _workspaceStore.DeleteNotificationSender(createdSender.Id);
                }
                catch
                {
                    // Ignore cleanup failures and surface the original error.
                }
            }

            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostSaveNotificationSender()
    {
        try
        {
            if (NotificationSenderEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Kein Notification Sender ausgewählt.");
            }

            var sender = _workspaceStore.FindNotificationSender(NotificationSenderEditor.Id)
                ?? throw new InvalidOperationException("Notification Sender nicht gefunden.");

            ApplyNotificationSenderEditor(sender, NotificationSenderEditor);
            _workspaceStore.Save();
            StatusMessage = $"Notification Sender '{sender.Name}' gespeichert.";
            return RedirectToPage(new { selectedNotificationSenderId = sender.Id, selectedId = SelectedId, selectedTemplateId = SelectedTemplateId, selectedNotificationRuleId = SelectedNotificationRuleId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostDeleteNotificationSender()
    {
        try
        {
            if (NotificationSenderEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Kein Notification Sender ausgewählt.");
            }

            var sender = _workspaceStore.FindNotificationSender(NotificationSenderEditor.Id)
                ?? throw new InvalidOperationException("Notification Sender nicht gefunden.");

            if (!_workspaceStore.DeleteNotificationSender(sender.Id))
            {
                throw new InvalidOperationException("Notification Sender konnte nicht gelöscht werden.");
            }

            StatusMessage = $"Notification Sender '{sender.Name}' gelöscht.";
            return RedirectToPage(new { selectedId = SelectedId, selectedTemplateId = SelectedTemplateId, selectedNotificationRuleId = SelectedNotificationRuleId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostCreateNotificationReceiver()
    {
        NotificationReceiver? createdReceiver = null;

        try
        {
            createdReceiver = _workspaceStore.CreateNotificationReceiver(NewNotificationReceiver.Name);
            ApplyNotificationReceiverEditor(createdReceiver, NewNotificationReceiver);
            _workspaceStore.Save();
            StatusMessage = $"Notification receiver '{createdReceiver.Name}' angelegt.";
            return RedirectAfterAction(ReturnUrl, "/NotificationReceivers");
        }
        catch (Exception ex)
        {
            if (createdReceiver is not null)
            {
                try
                {
                    _workspaceStore.DeleteNotificationReceiver(createdReceiver.Id);
                }
                catch
                {
                    // Ignore cleanup failures and surface the original error.
                }
            }

            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostSaveNotificationReceiver()
    {
        try
        {
            if (NotificationReceiverEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Kein Notification Receiver ausgewählt.");
            }

            var receiver = _workspaceStore.FindNotificationReceiver(NotificationReceiverEditor.Id)
                ?? throw new InvalidOperationException("Notification Receiver nicht gefunden.");

            ApplyNotificationReceiverEditor(receiver, NotificationReceiverEditor);
            _workspaceStore.Save();
            StatusMessage = $"Notification Receiver '{receiver.Name}' gespeichert.";
            return RedirectToPage(new { selectedNotificationReceiverId = receiver.Id, selectedId = SelectedId, selectedTemplateId = SelectedTemplateId, selectedNotificationRuleId = SelectedNotificationRuleId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostDeleteNotificationReceiver()
    {
        try
        {
            if (NotificationReceiverEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Kein Notification Receiver ausgewählt.");
            }

            var receiver = _workspaceStore.FindNotificationReceiver(NotificationReceiverEditor.Id)
                ?? throw new InvalidOperationException("Notification Receiver nicht gefunden.");

            if (!_workspaceStore.DeleteNotificationReceiver(receiver.Id))
            {
                throw new InvalidOperationException("Notification Receiver konnte nicht gelöscht werden.");
            }

            StatusMessage = $"Notification Receiver '{receiver.Name}' gelöscht.";
            return RedirectToPage(new { selectedId = SelectedId, selectedTemplateId = SelectedTemplateId, selectedNotificationRuleId = SelectedNotificationRuleId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
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

    private void EnsureCreateDefaults(MonitoringWorkspaceSnapshot snapshot, IReadOnlyList<WorkspaceNodeRow> nodes, bool populateEditorValues)
    {
        if (NewProbe.ParentId is null)
        {
            NewProbe.ParentId = snapshot.RootProbe.Id;
        }

        if (NewFolder.ParentId is null)
        {
            NewFolder.ParentId = snapshot.RootProbe.Id;
        }

        if (NewHost.ParentId is null)
        {
            NewHost.ParentId = snapshot.RootProbe.Id;
        }

        if (NewSensor.ParentId is null)
        {
            NewSensor.ParentId = snapshot.RootProbe.Id;
        }

        var isGetRequest = string.Equals(Request?.Method, "GET", StringComparison.OrdinalIgnoreCase);

        // On the initial GET the node the user clicked "Add …" on arrives as SelectedId — pre-select
        // it as the parent, clamped to the kinds each create form allows. Previously only Sensor did
        // this, so Folder/Host always fell back to the root probe (the wrong parent).
        if (isGetRequest && SelectedId is Guid selectedParentId)
        {
            if (IsSensorCreationPath())
            {
                var selectedParent = nodes.FirstOrDefault(node =>
                    node.Id == selectedParentId &&
                    node.Kind is MonitoringElementKind.Probe or MonitoringElementKind.Folder or MonitoringElementKind.Host);
                if (selectedParent is not null)
                {
                    NewSensor.ParentId = selectedParent.Id;
                }
            }
            else if (IsFolderCreationPath())
            {
                var selectedParent = nodes.FirstOrDefault(node =>
                    node.Id == selectedParentId &&
                    node.Kind is MonitoringElementKind.Probe or MonitoringElementKind.Folder);
                if (selectedParent is not null)
                {
                    NewFolder.ParentId = selectedParent.Id;
                }
            }
            else if (IsHostCreationPath())
            {
                var selectedParent = nodes.FirstOrDefault(node =>
                    node.Id == selectedParentId &&
                    node.Kind is MonitoringElementKind.Probe or MonitoringElementKind.Folder);
                if (selectedParent is not null)
                {
                    NewHost.ParentId = selectedParent.Id;
                }
            }
        }

        if (isGetRequest &&
            IsSensorCreationPath() &&
            NewSensor.TemplateId is null &&
            SelectedTemplateId is Guid selectedTemplateId)
        {
            var selectedTemplate = snapshot.Templates.FirstOrDefault(candidate =>
                candidate.Id == selectedTemplateId &&
                candidate.TargetKind == MonitoringTemplateScope.Sensor);

            if (selectedTemplate is not null)
            {
                NewSensor.TemplateId = selectedTemplate.Id;
            }
        }

        if (!isGetRequest)
        {
            NormalizeSelectedSensorTemplateForSensorType(snapshot);
        }

        if (string.Equals(NewSensor.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            var heartbeatParent = nodes.FirstOrDefault(node =>
                node.Kind == MonitoringElementKind.Probe &&
                !string.Equals(node.ProbeId, snapshot.RootProbe.ProbeId, StringComparison.OrdinalIgnoreCase));

            if (heartbeatParent is not null && NewSensor.ParentId == snapshot.RootProbe.Id)
            {
                NewSensor.ParentId = heartbeatParent.Id;
            }
        }

        if (IsSnmpWalkSensorType(NewSensor.SensorTypeKey))
        {
            var defaultRootOid = GetDefaultSnmpWalkRootOid(NewSensor.SensorTypeKey);
            if (string.IsNullOrWhiteSpace(NewSensor.SnmpWalkRootOid) ||
                string.Equals(NewSensor.SnmpWalkRootOid.Trim(), "1.3.6.1.2.1", StringComparison.OrdinalIgnoreCase))
            {
                NewSensor.SnmpWalkRootOid = defaultRootOid;
            }
        }

        NewSensor.SensorChannelMode = GetSensorChannelMode(NewSensor.SensorTypeKey);
        NormalizeHeartbeatSensorTarget(NewSensor);
        ApplySelectedSensorTemplateDefaults(snapshot, populateEditorValues);
        NewSensor.SensorChannelMode = GetSensorChannelMode(NewSensor.SensorTypeKey);
        NormalizeHeartbeatSensorTarget(NewSensor);
        EnsureSuggestedSensorName(snapshot);

        if (populateEditorValues && string.IsNullOrWhiteSpace(NewNotificationRule.Name))
        {
            NewNotificationRule.Name = "Notification rule";
        }

        if (populateEditorValues && NewNotificationRule.TriggerStates.Count == 0)
        {
            NewNotificationRule.TriggerStates.Add(SensorState.Warning);
            NewNotificationRule.TriggerStates.Add(SensorState.Critical);
        }

        if (populateEditorValues && NewNotificationRule.SenderId is null)
        {
            NewNotificationRule.SenderId = snapshot.NotificationSenders.FirstOrDefault()?.Id;
        }

        if (populateEditorValues && NewNotificationRule.ReceiverId is null)
        {
            NewNotificationRule.ReceiverId = snapshot.NotificationReceivers.FirstOrDefault()?.Id;
        }

        if (string.IsNullOrWhiteSpace(NewNotificationSender.Name))
        {
            NewNotificationSender.Name = "Email sender";
        }

        if (string.IsNullOrWhiteSpace(NewNotificationReceiver.Name))
        {
            NewNotificationReceiver.Name = "Email receiver";
        }
    }

    private bool IsSensorCreationPath()
    {
        var path = Request?.Path.Value ?? string.Empty;
        return path.EndsWith("/monitoring/sensor/new", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/monitoring/sensor/assistant", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsFolderCreationPath() =>
        (Request?.Path.Value ?? string.Empty).EndsWith("/monitoring/folder/new", StringComparison.OrdinalIgnoreCase);

    private bool IsHostCreationPath() =>
        (Request?.Path.Value ?? string.Empty).EndsWith("/monitoring/host/new", StringComparison.OrdinalIgnoreCase);

    private void EnsureSuggestedSensorName(MonitoringWorkspaceSnapshot snapshot)
    {
        if (!IsSensorCreationPath())
        {
            return;
        }

        if (!NewSensor.NameAutoGenerated && !string.IsNullOrWhiteSpace(NewSensor.Name))
        {
            return;
        }

        // No trailing counter — the type name (plus template) is the suggestion; duplicates are fine.
        NewSensor.Name = ResolveSuggestedSensorNameBase(snapshot);
        NewSensor.NameAutoGenerated = true;
    }

    private string ResolveSuggestedSensorNameBase(MonitoringWorkspaceSnapshot snapshot)
    {
        var definition = FindSensorDefinition(snapshot.SensorDefinitions, NewSensor.SensorTypeKey);
        var typeName = !string.IsNullOrWhiteSpace(definition?.DisplayName)
            ? definition.DisplayName.Trim()
            : string.IsNullOrWhiteSpace(NewSensor.SensorTypeKey)
                ? "Sensor"
                : HumanizeIdentifier(NewSensor.SensorTypeKey);

        // With a template, append its name: "HTTP – Webserver Check". Without one, just the type.
        var template = ResolveSelectedSensorTemplate(snapshot, requireSensorTypeMatch: true);
        if (!string.IsNullOrWhiteSpace(template?.Name))
        {
            return $"{typeName} – {template.Name.Trim()}";
        }

        return typeName;
    }

    private static string HumanizeIdentifier(string value)
    {
        var normalized = value.Trim()
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Replace('.', ' ')
            .Replace('/', ' ');
        normalized = Regex.Replace(normalized, "\\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Sensor";
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
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
        NewSensor.ScheduleInheritedLabel = FormatScheduleSummary(createCredentialSettings);

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
        elementEditor.TemplateOptions = snapshot.Templates
            .Select(template => new SelectListItem($"{template.Name} ({template.TargetKind})", template.Id.ToString(), elementEditor.AppliedTemplateIds.Contains(template.Id)))
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

    private WorkspaceElementEditorInput BuildElementEditor(
        MonitoringElement element,
        IReadOnlyList<WorkspaceNodeRow> nodes,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templateMap,
        IReadOnlyDictionary<Guid, SensorObservation> latestSensorObservations)
    {
        var availableTemplates = _workspaceStore.GetAllTemplates();
        var appliedTemplateIds = element.AppliedTemplateIds.ToList();
        var probeElement = element as ProbeElement;
        var parentOptions = BuildElementParentOptions(nodes, element);
        var localSettings = element.Settings;
        var effectiveSettings = ResolveElementEffectiveSettings(element);
        var sensorDefinition = element is SensorElement sensorElement
            ? FindSensorDefinition(_workspaceStore.Workspace.SensorDefinitions, sensorElement.SensorTypeKey)
            : null;
        var latestObservation = latestSensorObservations.TryGetValue(element.Id, out var observation)
            ? observation
            : null;
        var sensorCurrentThresholdFields = element is SensorElement
            ? BuildCurrentThresholdFieldsFromSettings(localSettings)
            : [];
        var sensorThresholdState = element is SensorElement sensorForThresholds
            ? BuildSensorThresholdEditorState(
                sensorForThresholds.SensorTypeKey,
                GetSensorChannelMode(sensorForThresholds.SensorTypeKey),
                effectiveSettings,
                sensorCurrentThresholdFields,
                latestObservation?.Channels)
            : new SensorThresholdEditorState([], 0);
        var sensorParameterFields = sensorDefinition is null
            ? []
            : BuildSensorParameterFields(sensorDefinition, localSettings.Parameters, effectiveSettings.Parameters);
        var sensorAdvancedParametersText = sensorDefinition is null
            ? BuildSensorAdvancedParametersText(localSettings.Parameters, [])
            : BuildSensorAdvancedParametersText(localSettings.Parameters, sensorDefinition.Parameters.Select(parameter => parameter.Key));
        var credentialBundleState = BuildCredentialBundleEditorState(localSettings.Credentials);
        var scheduleState = BuildScheduleEditorState(localSettings, effectiveSettings);
        var telemetryProfile = element is SensorElement sensorForProfile
            ? Matmon.Core.Telemetry.SensorTelemetryProfiles.Resolve(sensorForProfile.SensorTypeKey)
            : null;

        return new WorkspaceElementEditorInput
        {
            Id = element.Id,
            Kind = element.Kind.ToString(),
            Name = element.Name,
            Description = element.Description,
            TagsText = string.Join(", ", element.Tags),
            ParentId = element.ParentId,
            ProbeId = probeElement?.ProbeId,
            EnrollmentToken = probeElement?.EnrollmentToken,
            ProbeSubnetsText = probeElement is null ? null : string.Join("\n", probeElement.Subnets),
            Address = (element as HostElement)?.Address,
            SensorTypeKey = (element as SensorElement)?.SensorTypeKey,
            Target = (element as SensorElement)?.Target,
            TargetPlaceholder = element is SensorElement ? BuildTargetPlaceholder(element.ParentId) : null,
            Highlight = localSettings.Highlight,
            HighlightInheritedLabel = FormatInheritedBooleanLabel(effectiveSettings.Highlight),
            IsPaused = (element as SensorElement)?.IsPaused ?? false,
            SchedulePreset = scheduleState.Preset,
            ScheduleEveryValue = scheduleState.EveryValue,
            ScheduleEveryUnit = scheduleState.EveryUnit,
            ScheduleDayOfWeek = scheduleState.DayOfWeek,
            ScheduleDaysOfWeek = scheduleState.DaysOfWeek,
            ScheduleDayOfMonth = scheduleState.DayOfMonth,
            ScheduleTime = scheduleState.Time,
            ScheduleInheritedLabel = scheduleState.InheritedLabel,
            EnabledMode = effectiveSettings.Enabled switch
            {
                true => "enabled",
                false => "disabled",
                null => "inherit"
            },
            EnabledInheritedLabel = FormatInheritedEnabledLabel(effectiveSettings.Enabled),
            PollingIntervalSeconds = localSettings.PollingInterval is TimeSpan pollingInterval ? (int?)Math.Round(pollingInterval.TotalSeconds) : null,
            PollingIntervalSecondsPlaceholder = FormatSecondsPlaceholder(effectiveSettings.PollingInterval),
            TimeoutSeconds = localSettings.Timeout is TimeSpan timeout ? (int?)Math.Round(timeout.TotalSeconds) : null,
            TimeoutSecondsPlaceholder = FormatSecondsPlaceholder(effectiveSettings.Timeout),
            RetryCount = localSettings.RetryCount,
            RetryCountPlaceholder = FormatNullableIntPlaceholder(effectiveSettings.RetryCount),
            EventRetentionDays = localSettings.EventRetentionDays,
            EventRetentionDaysPlaceholder = FormatNullableIntPlaceholder(effectiveSettings.EventRetentionDays ?? telemetryProfile?.EventRetentionDays),
            ObservationRetentionDays = localSettings.ObservationRetentionDays,
            ObservationRetentionDaysPlaceholder = FormatNullableIntPlaceholder(effectiveSettings.ObservationRetentionDays ?? telemetryProfile?.RawObservationDays),
            StatisticsRetentionDays = localSettings.StatisticsRetentionDays,
            StatisticsRetentionDaysPlaceholder = FormatNullableIntPlaceholder(effectiveSettings.StatisticsRetentionDays ?? telemetryProfile?.StatisticsRetentionDays),
            StatisticsBucketMinutes = localSettings.StatisticsBucketMinutes,
            StatisticsBucketMinutesPlaceholder = FormatNullableIntPlaceholder(effectiveSettings.StatisticsBucketMinutes ?? telemetryProfile?.StatisticsBucketMinutes),
            GraphMinValue = localSettings.GraphMinValue,
            GraphMaxValue = localSettings.GraphMaxValue,
            TelemetryProfileSummary = BuildTelemetryProfileSummary(telemetryProfile),
            ParametersText = ToLines(localSettings.Parameters),
            ParametersTextPlaceholder = !string.IsNullOrWhiteSpace(ToLines(effectiveSettings.Parameters))
                ? ToLines(effectiveSettings.Parameters)
                : "key=value per line",
            SensorAdvancedParametersText = sensorAdvancedParametersText,
            ThresholdsText = ToLines(localSettings.Thresholds),
            ThresholdsTextPlaceholder = !string.IsNullOrWhiteSpace(ToLines(effectiveSettings.Thresholds))
                ? ToLines(effectiveSettings.Thresholds)
                : "key=value per line",
            SensorChannelThresholdFields = sensorThresholdState.Fields,
            SensorChannelThresholdVisibleCount = sensorThresholdState.VisibleCount,
            SensorChannelMode = sensorDefinition?.ChannelMode ?? SensorChannelMode.Dynamic,
            SensorParameterFields = sensorParameterFields,
            SelectedCredentialId = localSettings.SelectedCredentialId,
            CredentialBundles = credentialBundleState.Bundles,
            CredentialBundleVisibleCount = credentialBundleState.VisibleCount,
            AppliedTemplateIds = appliedTemplateIds,
            TemplateOriginId = element.TemplateOriginId,
            TemplateOriginName = element.TemplateOriginId is Guid originId && templateMap.TryGetValue(originId, out var originTemplate)
                ? originTemplate.Name
                : null,
            ParentOptions = parentOptions,
            TemplateOptions = availableTemplates
                .Where(template => IsTemplateApplicableToElement(template.TargetKind, element.Kind))
                .Where(template => element is not SensorElement templateSensor || SensorTemplateMatchesType(template, templateSensor.SensorTypeKey))
                .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                .Select(template => new SelectListItem($"{template.Name} ({template.TargetKind})", template.Id.ToString(), element.TemplateOriginId == template.Id))
                .ToList(),
            SensorTypeOptions = BuildSensorTypeOptions(_workspaceStore.Workspace.SensorDefinitions, (element as SensorElement)?.SensorTypeKey),
            BootstrapSnippet = probeElement is null
                ? null
                : BuildProbeBootstrapSnippet(probeElement.ProbeId, probeElement.Name, probeElement.EnrollmentToken)
        };
    }

    private WorkspaceTemplateEditorInput BuildTemplateEditor(MonitoringTemplate template)
    {
        var localSettings = template.Settings;
        var effectiveSettings = ResolveTemplateEffectiveSettings(template);
        var thresholdState = template.TargetKind == MonitoringTemplateScope.Sensor
            ? BuildSensorThresholdEditorState(
                template.SensorTypeKey,
                GetSensorChannelMode(template.SensorTypeKey),
                effectiveSettings,
                BuildCurrentThresholdFieldsFromSettings(localSettings),
                null)
            : new SensorThresholdEditorState([], 0);
        var parameterPlaceholder = !string.IsNullOrWhiteSpace(ToLines(effectiveSettings.Parameters))
            ? ToLines(effectiveSettings.Parameters)
            : "key=value per line";
        var templateDefinition = template.TargetKind == MonitoringTemplateScope.Sensor
            ? FindSensorDefinition(_workspaceStore.Workspace.SensorDefinitions, template.SensorTypeKey)
            : null;
        var templateParameterFields = templateDefinition is null
            ? new List<WorkspaceSensorParameterFieldInput>()
            // Sensor-scope templates render only the general params (no per-field credential
            // editor), so drop the credential params here. Otherwise they'd sit at indices 0..n
            // unrendered and leave a gap — and ASP.NET's sequential collection binding stops at
            // the first missing index, silently dropping every posted param (e.g. "Script is
            // required" even though the script was typed).
            : BuildSensorParameterFields(templateDefinition, localSettings.Parameters, effectiveSettings.Parameters)
                .Where(field => !field.IsCredential)
                .ToList();
        var templateAdvancedParametersText = templateDefinition is null
            ? BuildSensorAdvancedParametersText(localSettings.Parameters, [])
            : BuildSensorAdvancedParametersText(localSettings.Parameters, templateDefinition.Parameters.Select(parameter => parameter.Key));
        var credentialBundleState = BuildCredentialBundleEditorState(localSettings.Credentials);
        var scheduleState = BuildScheduleEditorState(localSettings, effectiveSettings);

        return new WorkspaceTemplateEditorInput
        {
            Id = template.Id,
            Name = template.Name,
            TagsText = string.Join(", ", template.Tags),
            TargetKind = template.TargetKind,
            SensorTypeKey = string.IsNullOrWhiteSpace(template.SensorTypeKey)
                ? PingSensorExecutor.Definition.Key
                : template.SensorTypeKey,
            ParentTemplateId = template.ParentTemplateId,
            Highlight = localSettings.Highlight,
            HighlightInheritedLabel = FormatInheritedBooleanLabel(effectiveSettings.Highlight),
            SchedulePreset = scheduleState.Preset,
            ScheduleEveryValue = scheduleState.EveryValue,
            ScheduleEveryUnit = scheduleState.EveryUnit,
            ScheduleDayOfWeek = scheduleState.DayOfWeek,
            ScheduleDaysOfWeek = scheduleState.DaysOfWeek,
            ScheduleDayOfMonth = scheduleState.DayOfMonth,
            ScheduleTime = scheduleState.Time,
            ScheduleInheritedLabel = scheduleState.InheritedLabel,
            EnabledMode = effectiveSettings.Enabled switch
            {
                true => "enabled",
                false => "disabled",
                null => "inherit"
            },
            EnabledInheritedLabel = FormatInheritedEnabledLabel(effectiveSettings.Enabled),
            PollingIntervalSeconds = localSettings.PollingInterval is TimeSpan pollingInterval ? (int?)Math.Round(pollingInterval.TotalSeconds) : null,
            PollingIntervalSecondsPlaceholder = FormatSecondsPlaceholder(effectiveSettings.PollingInterval),
            TimeoutSeconds = localSettings.Timeout is TimeSpan timeout ? (int?)Math.Round(timeout.TotalSeconds) : null,
            TimeoutSecondsPlaceholder = FormatSecondsPlaceholder(effectiveSettings.Timeout),
            RetryCount = localSettings.RetryCount,
            RetryCountPlaceholder = FormatNullableIntPlaceholder(effectiveSettings.RetryCount),
            EventRetentionDays = localSettings.EventRetentionDays,
            EventRetentionDaysPlaceholder = FormatNullableIntPlaceholder(effectiveSettings.EventRetentionDays),
            ObservationRetentionDays = localSettings.ObservationRetentionDays,
            ObservationRetentionDaysPlaceholder = FormatNullableIntPlaceholder(effectiveSettings.ObservationRetentionDays),
            StatisticsRetentionDays = localSettings.StatisticsRetentionDays,
            StatisticsRetentionDaysPlaceholder = FormatNullableIntPlaceholder(effectiveSettings.StatisticsRetentionDays),
            StatisticsBucketMinutes = localSettings.StatisticsBucketMinutes,
            StatisticsBucketMinutesPlaceholder = FormatNullableIntPlaceholder(effectiveSettings.StatisticsBucketMinutes),
            ParametersText = ToLines(localSettings.Parameters),
            ParametersTextPlaceholder = parameterPlaceholder,
            ThresholdsText = ToLines(localSettings.Thresholds),
            ThresholdsTextPlaceholder = !string.IsNullOrWhiteSpace(ToLines(effectiveSettings.Thresholds))
                ? ToLines(effectiveSettings.Thresholds)
                : "key=value per line",
            SensorChannelThresholdFields = thresholdState.Fields,
            SensorChannelThresholdVisibleCount = thresholdState.VisibleCount,
            SensorChannelMode = FindSensorDefinition(_workspaceStore.Workspace.SensorDefinitions, template.SensorTypeKey)?.ChannelMode ?? SensorChannelMode.Dynamic,
            SensorParameterFields = templateParameterFields,
            SensorAdvancedParametersText = templateAdvancedParametersText,
            SelectedCredentialId = localSettings.SelectedCredentialId,
            CredentialBundles = credentialBundleState.Bundles,
            CredentialBundleVisibleCount = credentialBundleState.VisibleCount
        };
    }

    private WorkspaceNotificationRuleEditorInput BuildNotificationRuleEditor(
        NotificationRule rule,
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyList<WorkspaceNodeRow> nodes)
    {
        return new WorkspaceNotificationRuleEditorInput
        {
            Id = rule.Id,
            Name = rule.Name,
            Enabled = rule.Enabled,
            SenderId = rule.SenderId,
            ReceiverId = rule.ReceiverId,
            TargetElementId = rule.TargetElementId,
            IncludeDescendants = rule.IncludeDescendants,
            TriggerStates = rule.TriggerStates.ToList(),
            CooldownMinutes = rule.CooldownMinutes,
            SubjectTemplate = string.IsNullOrWhiteSpace(rule.SubjectTemplate) ? NotificationTemplateCatalog.DefaultSubjectTemplate : rule.SubjectTemplate,
            TextTemplate = string.IsNullOrWhiteSpace(rule.TextTemplate) ? NotificationTemplateCatalog.DefaultTextTemplate : rule.TextTemplate,
            HtmlTemplate = string.IsNullOrWhiteSpace(rule.HtmlTemplate) ? NotificationTemplateCatalog.DefaultHtmlTemplate : rule.HtmlTemplate,
            SenderOptions = BuildNotificationSenderOptions(snapshot.NotificationSenders, rule.SenderId),
            ReceiverOptions = BuildNotificationReceiverOptions(snapshot.NotificationReceivers, rule.ReceiverId),
            TargetOptions = BuildNotificationTargetOptions(nodes, rule.TargetElementId),
            TriggerStateOptions = BuildNotificationStateOptions(rule.TriggerStates)
        };
    }

    private WorkspaceNotificationSenderEditorInput BuildNotificationSenderEditor(NotificationSender sender)
    {
        return new WorkspaceNotificationSenderEditorInput
        {
            Id = sender.Id,
            Name = sender.Name,
            Enabled = sender.Enabled,
            Kind = sender.Kind,
            SenderName = sender.Email.SenderName,
            SenderEmail = sender.Email.SenderEmail,
            SmtpHost = sender.Email.SmtpHost,
            SmtpPort = sender.Email.SmtpPort,
            UseSsl = sender.Email.UseSsl,
            Username = sender.Email.Username,
            Password = sender.Email.Password,
            EndpointUrl = sender.Webhook.EndpointUrl,
            Secret = sender.Webhook.Secret,
            TimeoutSeconds = sender.Webhook.TimeoutSeconds
        };
    }

    private WorkspaceNotificationReceiverEditorInput BuildNotificationReceiverEditor(NotificationReceiver receiver)
    {
        return new WorkspaceNotificationReceiverEditorInput
        {
            Id = receiver.Id,
            Name = receiver.Name,
            Enabled = receiver.Enabled,
            Kind = receiver.Kind,
            Target = receiver.Target,
            Secret = receiver.Secret,
            TimeoutSeconds = receiver.TimeoutSeconds
        };
    }

    private static void ApplyNotificationRuleEditor(NotificationRule rule, CreateNotificationRuleInput editor)
    {
        ApplyNotificationRuleValues(
            rule,
            editor.Name,
            editor.Enabled,
            editor.SenderId,
            editor.ReceiverId,
            editor.TargetElementId,
            editor.IncludeDescendants,
            editor.TriggerStates,
            editor.CooldownMinutes,
            editor.SubjectTemplate,
            editor.TextTemplate,
            editor.HtmlTemplate);
    }

    private static void ApplyNotificationSenderEditor(NotificationSender sender, CreateNotificationSenderInput editor)
    {
        sender.Name = string.IsNullOrWhiteSpace(editor.Name) ? "Notification sender" : editor.Name.Trim();
        sender.Enabled = editor.Enabled;
        sender.Kind = editor.Kind;

        if (!string.IsNullOrWhiteSpace(editor.SenderName))
        {
            sender.Email.SenderName = editor.SenderName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(editor.SenderEmail))
        {
            sender.Email.SenderEmail = editor.SenderEmail.Trim();
        }

        if (!string.IsNullOrWhiteSpace(editor.SmtpHost))
        {
            sender.Email.SmtpHost = editor.SmtpHost.Trim();
        }

        if (editor.SmtpPort is int smtpPort && smtpPort > 0)
        {
            sender.Email.SmtpPort = smtpPort;
        }

        sender.Email.UseSsl = editor.UseSsl;

        if (!string.IsNullOrWhiteSpace(editor.Username))
        {
            sender.Email.Username = editor.Username.Trim();
        }

        if (!string.IsNullOrWhiteSpace(editor.Password))
        {
            sender.Email.Password = editor.Password;
        }

        if (!string.IsNullOrWhiteSpace(editor.EndpointUrl))
        {
            sender.Webhook.EndpointUrl = editor.EndpointUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(editor.Secret))
        {
            sender.Webhook.Secret = editor.Secret.Trim();
        }

        if (editor.TimeoutSeconds is int timeout && timeout > 0)
        {
            sender.Webhook.TimeoutSeconds = timeout;
        }
    }

    private static void ApplyNotificationReceiverEditor(NotificationReceiver receiver, CreateNotificationReceiverInput editor)
    {
        receiver.Name = string.IsNullOrWhiteSpace(editor.Name) ? "Notification receiver" : editor.Name.Trim();
        receiver.Enabled = editor.Enabled;
        receiver.Kind = editor.Kind;
        receiver.Target = string.IsNullOrWhiteSpace(editor.Target) ? receiver.Target : editor.Target.Trim();

        if (!string.IsNullOrWhiteSpace(editor.Secret))
        {
            receiver.Secret = editor.Secret.Trim();
        }

        if (editor.TimeoutSeconds is int timeout && timeout > 0)
        {
            receiver.TimeoutSeconds = timeout;
        }
    }

    private static void ApplyNotificationRuleEditor(NotificationRule rule, WorkspaceNotificationRuleEditorInput editor)
    {
        ApplyNotificationRuleValues(
            rule,
            editor.Name,
            editor.Enabled,
            editor.SenderId,
            editor.ReceiverId,
            editor.TargetElementId,
            editor.IncludeDescendants,
            editor.TriggerStates,
            editor.CooldownMinutes,
            editor.SubjectTemplate,
            editor.TextTemplate,
            editor.HtmlTemplate);
    }

    private static void ApplyNotificationRuleValues(
        NotificationRule rule,
        string name,
        bool enabled,
        Guid? senderId,
        Guid? receiverId,
        Guid? targetElementId,
        bool includeDescendants,
        IEnumerable<SensorState>? triggerStates,
        int? cooldownMinutes,
        string? subjectTemplate,
        string? textTemplate,
        string? htmlTemplate)
    {
        var triggerStateList = (triggerStates ?? Enumerable.Empty<SensorState>()).Distinct().ToList();
        if (triggerStateList.Count == 0)
        {
            throw new InvalidOperationException("Mindestens ein Trigger-State muss ausgewählt werden.");
        }

        rule.Name = string.IsNullOrWhiteSpace(name) ? "Notification rule" : name.Trim();
        rule.Enabled = enabled;
        rule.SenderId = senderId;
        rule.ReceiverId = receiverId;
        rule.TargetElementId = targetElementId;
        rule.IncludeDescendants = includeDescendants;
        rule.CooldownMinutes = cooldownMinutes is int cooldown && cooldown > 0 ? cooldown : null;
        rule.SubjectTemplate = string.IsNullOrWhiteSpace(subjectTemplate) ? string.Empty : subjectTemplate;
        rule.TextTemplate = string.IsNullOrWhiteSpace(textTemplate) ? string.Empty : textTemplate;
        rule.HtmlTemplate = string.IsNullOrWhiteSpace(htmlTemplate) ? string.Empty : htmlTemplate;
        rule.TriggerStates.Clear();

        foreach (var state in triggerStateList)
        {
            rule.TriggerStates.Add(state);
        }
    }

    private void SynchronizeNotificationRuleLegacyFields(NotificationRule rule)
    {
        if (rule.SenderId is Guid senderId)
        {
            var sender = _workspaceStore.FindNotificationSender(senderId);
            if (sender is not null)
            {
                rule.ChannelKind = sender.Kind == NotificationEndpointKind.Webhook
                    ? NotificationChannelKind.Webhook
                    : NotificationChannelKind.Email;
            }
        }

        if (rule.ReceiverId is Guid receiverId)
        {
            var receiver = _workspaceStore.FindNotificationReceiver(receiverId);
            if (receiver is not null)
            {
                rule.Recipient = receiver.Target;
            }
        }
    }

    private static void ApplyEmailSettings(EmailNotificationSettings settings, EmailNotificationSettingsInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.SenderName))
        {
            settings.SenderName = input.SenderName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(input.SenderEmail))
        {
            settings.SenderEmail = input.SenderEmail.Trim();
        }

        if (!string.IsNullOrWhiteSpace(input.SmtpHost))
        {
            settings.SmtpHost = input.SmtpHost.Trim();
        }

        if (input.SmtpPort is int smtpPort && smtpPort > 0)
        {
            settings.SmtpPort = smtpPort;
        }

        settings.UseSsl = input.UseSsl;

        if (!string.IsNullOrWhiteSpace(input.Username))
        {
            settings.Username = input.Username.Trim();
        }

        if (!string.IsNullOrWhiteSpace(input.Password))
        {
            settings.Password = input.Password;
        }
    }

    private static void ApplyWebhookSettings(WebhookNotificationSettings settings, WebhookNotificationSettingsInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.EndpointUrl))
        {
            settings.EndpointUrl = input.EndpointUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(input.Secret))
        {
            settings.Secret = input.Secret.Trim();
        }

        if (input.TimeoutSeconds is int timeout && timeout > 0)
        {
            settings.TimeoutSeconds = timeout;
        }
    }

    private void PopulateSensorParameterEditor(CreateSensorInput editor, IReadOnlyList<SensorDefinition> sensorDefinitions)
    {
        var selectedTemplate = ResolveSelectedSensorTemplate(_workspaceStore.Workspace);
        var inheritedValues = BuildSensorInheritedSettings(editor, selectedTemplate).Parameters;
        var state = BuildSensorParameterEditorState(
            editor.SensorTypeKey,
            editor.SensorParameterFields,
            editor.SensorAdvancedParametersText,
            sensorDefinitions,
            inheritedValues);

        editor.SensorParameterFields = state.Fields;
        editor.SensorAdvancedParametersText = state.AdvancedText;
    }

    private void PopulateSensorParameterEditor(WorkspaceElementEditorInput editor, IReadOnlyList<SensorDefinition> sensorDefinitions)
    {
        var inheritedValues = TryBuildSensorEditorInheritedParameters(editor);
        var state = BuildSensorParameterEditorState(
            editor.SensorTypeKey,
            editor.SensorParameterFields,
            editor.SensorAdvancedParametersText,
            sensorDefinitions,
            inheritedValues);

        editor.SensorParameterFields = state.Fields;
        editor.SensorAdvancedParametersText = state.AdvancedText;
    }

    private IReadOnlyDictionary<string, string>? TryBuildSensorEditorInheritedParameters(WorkspaceElementEditorInput editor)
    {
        if (!string.Equals(editor.Kind, "Sensor", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return BuildTransientSensorExecutionSettings(editor).Parameters;
        }
        catch (InvalidOperationException)
        {
            // Opening the editor must stay possible even when required sensor values
            // are missing. Save/test paths still validate and surface the error.
            return editor.Id != Guid.Empty && _workspaceStore.FindElement(editor.Id) is SensorElement existingSensor
                ? ResolveElementEffectiveSettings(existingSensor).Parameters
                : null;
        }
    }

    private void PopulateSensorThresholdEditor(
        CreateSensorInput editor,
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyDictionary<Guid, SensorObservation> latestSensorObservations)
    {
        var selectedTemplate = ResolveSelectedSensorTemplate(snapshot);
        var templateSettings = BuildSensorInheritedSettings(editor, selectedTemplate);
        editor.SensorChannelMode = GetSensorChannelMode(editor.SensorTypeKey);
        var state = BuildSensorThresholdEditorState(
            editor.SensorTypeKey,
            editor.SensorChannelMode,
            templateSettings,
            editor.SensorChannelThresholdFields,
            null);

        editor.SensorChannelThresholdFields = state.Fields;
        editor.SensorChannelThresholdVisibleCount = state.VisibleCount;
    }

    private void PopulateSensorThresholdEditor(
        WorkspaceElementEditorInput editor,
        MonitoringWorkspaceSnapshot snapshot,
        SensorElement? sensorElement,
        IReadOnlyDictionary<Guid, SensorObservation> latestSensorObservations)
    {
        if (sensorElement is null)
        {
            editor.SensorChannelThresholdFields = [];
            editor.SensorChannelThresholdVisibleCount = 0;
            return;
        }

        var latestObservation = latestSensorObservations.TryGetValue(sensorElement.Id, out var observation)
            ? observation
            : null;
        editor.SensorChannelMode = GetSensorChannelMode(sensorElement.SensorTypeKey);
        var state = BuildSensorThresholdEditorState(
            sensorElement.SensorTypeKey,
            editor.SensorChannelMode,
            ResolveElementEffectiveSettings(sensorElement),
            editor.SensorChannelThresholdFields,
            latestObservation?.Channels);

        editor.SensorChannelThresholdFields = state.Fields;
        editor.SensorChannelThresholdVisibleCount = state.VisibleCount;
    }

    private void PopulateTemplateParameterEditor(
        WorkspaceTemplateEditorInput editor,
        MonitoringWorkspaceSnapshot snapshot)
    {
        if (editor.TargetKind != MonitoringTemplateScope.Sensor)
        {
            editor.SensorParameterFields = [];
            editor.SensorAdvancedParametersText = string.Empty;
            return;
        }

        var template = _workspaceStore.FindTemplate(editor.Id);
        var inheritedValues = template is null ? null : ResolveTemplateEffectiveSettings(template).Parameters;
        var state = BuildSensorParameterEditorState(
            editor.SensorTypeKey,
            editor.SensorParameterFields,
            editor.SensorAdvancedParametersText,
            snapshot.SensorDefinitions,
            inheritedValues);
        // Sensor-scope templates don't render the per-field credential editor; keeping the
        // credential params here would leave index gaps (0,1) in the posted list and ASP.NET's
        // sequential collection binding would then silently drop every param (e.g. "Script is
        // required" even when set). Drop them so the rendered/posted indices stay contiguous.
        editor.SensorParameterFields = state.Fields.Where(field => !field.IsCredential).ToList();
        editor.SensorAdvancedParametersText = state.AdvancedText;
    }

    private void PopulateTemplateThresholdEditor(
        WorkspaceTemplateEditorInput editor,
        MonitoringWorkspaceSnapshot snapshot)
    {
        if (editor.TargetKind != MonitoringTemplateScope.Sensor)
        {
            editor.SensorChannelThresholdFields = [];
            editor.SensorChannelThresholdVisibleCount = 0;
            return;
        }

        var template = _workspaceStore.FindTemplate(editor.Id);
        var effectiveSettings = template is null
            ? new MonitoringSettings()
            : ResolveTemplateEffectiveSettings(template);
        editor.SensorChannelMode = GetSensorChannelMode(editor.SensorTypeKey);
        var state = BuildSensorThresholdEditorState(
            editor.SensorTypeKey,
            editor.SensorChannelMode,
            effectiveSettings,
            editor.SensorChannelThresholdFields,
            null);
        editor.SensorChannelThresholdFields = state.Fields;
        editor.SensorChannelThresholdVisibleCount = state.VisibleCount;
    }

    private static SensorParameterEditorState BuildSensorParameterEditorState(
        string? sensorTypeKey,
        IReadOnlyList<WorkspaceSensorParameterFieldInput> currentFields,
        string? advancedText,
        IReadOnlyList<SensorDefinition> sensorDefinitions,
        IReadOnlyDictionary<string, string>? inheritedValues = null)
    {
        var collectedValues = CollectSensorParameterValues(currentFields, advancedText);
        var definition = FindSensorDefinition(sensorDefinitions, sensorTypeKey);
        if (definition is null)
        {
            return new SensorParameterEditorState(
                [],
                BuildSensorAdvancedParametersText(collectedValues, []));
        }

        var fields = BuildSensorParameterFields(definition, collectedValues, inheritedValues);
        var advanced = BuildSensorAdvancedParametersText(collectedValues, definition.Parameters.Select(parameter => parameter.Key));
        return new SensorParameterEditorState(fields, advanced);
    }

    private static List<WorkspaceSensorParameterFieldInput> BuildSensorParameterFields(
        SensorDefinition definition,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? inheritedValues = null)
    {
        var parameterDefinitions = definition.Parameters.ToDictionary(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase);

        return definition.Parameters
            .Select(parameter =>
            {
                var currentValue = values.TryGetValue(parameter.Key, out var value)
                    ? value
                    : string.Empty;

                var inheritedValue = inheritedValues is not null && inheritedValues.TryGetValue(parameter.Key, out var fallbackValue)
                    ? fallbackValue
                    : parameter.DefaultValue;
                // Never surface an inherited secret (password/token) as a visible placeholder —
                // show a masked hint instead so it can't be read off the form.
                var displayPlaceholder = !string.IsNullOrWhiteSpace(inheritedValue)
                    ? (parameter.Kind == SensorParameterKind.Secret ? "••••••" : inheritedValue)
                    : parameter.Placeholder;
                var effectiveValue = !string.IsNullOrWhiteSpace(currentValue)
                    ? currentValue
                    : inheritedValue;

                if (string.IsNullOrWhiteSpace(currentValue) && parameter.Required)
                {
                    currentValue = string.Empty;
                }

                return new WorkspaceSensorParameterFieldInput
                {
                    Key = parameter.Key,
                    Label = parameter.Label,
                    Group = parameter.Group,
                    Kind = parameter.Kind,
                    Description = parameter.Description,
                    Required = parameter.Required,
                    Placeholder = parameter.Placeholder,
                    DisplayPlaceholder = displayPlaceholder,
                    InheritedValue = inheritedValue,
                    EffectiveValue = effectiveValue,
                    Min = parameter.Min,
                    Max = parameter.Max,
                    Step = parameter.Step,
                    CredentialKind = parameter.CredentialKind,
                    VisibleWhenParameterKey = parameter.VisibleWhenParameterKey,
                    VisibleWhenValuesText = string.Join("|", parameter.VisibleWhenValues),
                    IsVisible = IsSensorParameterVisible(parameter, parameterDefinitions, values, inheritedValues),
                    Value = currentValue,
                    Options = parameter.Kind switch
                    {
                        SensorParameterKind.Boolean => BuildBooleanOptions(parameter, currentValue, displayPlaceholder),
                        SensorParameterKind.ValueList => BuildValueListOptions(parameter, currentValue, displayPlaceholder),
                        _ => []
                    }
                };
            })
            .ToList();
    }

    private static bool IsSensorParameterVisible(
        SensorParameterDefinition parameter,
        IReadOnlyDictionary<string, SensorParameterDefinition> parameterDefinitions,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? inheritedValues)
    {
        if (string.IsNullOrWhiteSpace(parameter.VisibleWhenParameterKey) ||
            parameter.VisibleWhenValues.Count == 0)
        {
            return true;
        }

        var driverValue = ResolveSensorParameterEffectiveValue(
            parameter.VisibleWhenParameterKey,
            parameterDefinitions,
            values,
            inheritedValues);

        return !string.IsNullOrWhiteSpace(driverValue) &&
            parameter.VisibleWhenValues.Any(visibleValue =>
                string.Equals(visibleValue, driverValue, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveSensorParameterEffectiveValue(
        string key,
        IReadOnlyDictionary<string, SensorParameterDefinition> parameterDefinitions,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? inheritedValues)
    {
        if (values.TryGetValue(key, out var localValue) &&
            !string.IsNullOrWhiteSpace(localValue))
        {
            return localValue;
        }

        if (inheritedValues is not null &&
            inheritedValues.TryGetValue(key, out var inheritedValue) &&
            !string.IsNullOrWhiteSpace(inheritedValue))
        {
            return inheritedValue;
        }

        return parameterDefinitions.TryGetValue(key, out var definition)
            ? definition.DefaultValue
            : null;
    }

    private static IReadOnlyDictionary<string, string> BuildSensorParameterValues(
        SensorDefinition definition,
        IReadOnlyList<WorkspaceSensorParameterFieldInput> fields,
        string? advancedText,
        IReadOnlyDictionary<string, string>? existingValues = null)
    {
        var collectedValues = CollectSensorParameterValues(fields, advancedText);
        var fieldMap = fields
            .Where(field => !string.IsNullOrWhiteSpace(field.Key))
            .GroupBy(field => field.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in definition.Parameters)
        {
            var hasPostedValue = collectedValues.TryGetValue(parameter.Key, out var postedValue);
            var rawValue = hasPostedValue ? postedValue : null;
            fieldMap.TryGetValue(parameter.Key, out var field);

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                if (parameter.Kind == SensorParameterKind.Secret &&
                    existingValues is not null &&
                    existingValues.TryGetValue(parameter.Key, out var existingValue) &&
                    !string.IsNullOrWhiteSpace(existingValue))
                {
                    values[parameter.Key] = existingValue;
                    continue;
                }

                var inheritedValue = field?.InheritedValue;
                if (parameter.Required)
                {
                    if (!string.IsNullOrWhiteSpace(inheritedValue))
                    {
                        continue;
                    }

                    // Credential fields (username/password/token) are resolved at execution time
                    // from the selected or inherited credential bundle — which is NOT reflected in
                    // the posted/inherited param value here — so don't block the save on a blank
                    // one. A genuinely missing credential surfaces as a runtime sensor error.
                    if (parameter.IsCredential)
                    {
                        continue;
                    }

                    throw new InvalidOperationException($"{parameter.Label} is required.");
                }

                continue;
            }

            values[parameter.Key] = NormalizeSensorParameterValue(parameter, rawValue);
        }

        foreach (var pair in collectedValues)
        {
            if (definition.Parameters.Any(parameter => string.Equals(parameter.Key, pair.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            values[pair.Key] = pair.Value;
        }

        return values;
    }

    private static void ApplySensorParameters(
        MonitoringSettings settings,
        SensorDefinition definition,
        IReadOnlyList<WorkspaceSensorParameterFieldInput> fields,
        string? advancedText,
        IReadOnlyDictionary<string, string>? existingValues = null)
    {
        var values = BuildSensorParameterValues(definition, fields, advancedText, existingValues);
        settings.Parameters.Clear();
        foreach (var pair in values)
        {
            settings.Parameters[pair.Key] = pair.Value;
        }
    }

    private static string? TryReadSensorParameter(CreateSensorInput editor, string key)
    {
        var field = editor.SensorParameterFields.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));

        return field?.Value;
    }

    private static int? TryReadSensorParameterInt(CreateSensorInput editor, string key)
    {
        var value = TryReadSensorParameter(editor, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static void ApplySnmpWalkSelectionsToParameters(CreateSensorInput editor)
    {
        if (!IsSnmpWalkSensorType(editor.SensorTypeKey) ||
            editor.SnmpWalkItems.Count == 0)
        {
            return;
        }

        var selectedOids = editor.SnmpWalkItems
            .Where(item => item.Selected && !string.IsNullOrWhiteSpace(item.Oid))
            .Select(item => item.Oid.Trim().TrimStart('.'))
            .Where(oid => !string.IsNullOrWhiteSpace(oid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var parameterField = editor.SensorParameterFields.FirstOrDefault(field =>
            string.Equals(field.Key, "snmp.oids", StringComparison.OrdinalIgnoreCase));

        if (parameterField is null)
        {
            editor.SensorParameterFields.Add(new WorkspaceSensorParameterFieldInput
            {
                Key = "snmp.oids",
                Label = "Selected OIDs",
                Kind = SensorParameterKind.Multiline,
                Value = string.Join(Environment.NewLine, selectedOids)
            });
            return;
        }

        var discoveredOids = editor.SnmpWalkItems
            .Select(item => item.Oid.Trim().TrimStart('.'))
            .Where(oid => !string.IsNullOrWhiteSpace(oid))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manualOids = ParseSnmpOidLines(parameterField.Value)
            .Where(oid => !discoveredOids.Contains(oid))
            .ToList();

        var mergedOids = new List<string>(manualOids.Count + selectedOids.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var oid in manualOids)
        {
            if (seen.Add(oid))
            {
                mergedOids.Add(oid);
            }
        }

        foreach (var oid in selectedOids)
        {
            if (seen.Add(oid))
            {
                mergedOids.Add(oid);
            }
        }

        parameterField.Value = string.Join(Environment.NewLine, mergedOids);
    }

    private static bool IsSnmpWalkSensorType(string? sensorTypeKey)
    {
        return string.Equals(sensorTypeKey, SnmpSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sensorTypeKey, SynologyNasSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDefaultSnmpWalkRootOid(string? sensorTypeKey)
    {
        return string.Equals(sensorTypeKey, SynologyNasSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase)
            ? "1.3.6.1.4.1.6574"
            : "1.3.6.1.2.1";
    }

    private static List<string> ParseSnmpOidLines(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var lines = value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<string>(lines.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var candidate = line.Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            var separatorIndex = candidate.IndexOf('|');
            if (separatorIndex < 0)
            {
                separatorIndex = candidate.IndexOf('=');
            }

            var oid = separatorIndex >= 0 ? candidate[..separatorIndex].Trim() : candidate;
            oid = oid.Trim().TrimStart('.');
            if (oid.Length == 0 || !seen.Add(oid))
            {
                continue;
            }

            results.Add(oid);
        }

        return results;
    }

    private static HashSet<string> GetSelectedSnmpWalkOids(IReadOnlyList<WorkspaceSnmpWalkItemInput> items)
    {
        return items
            .Where(item => item.Selected && !string.IsNullOrWhiteSpace(item.Oid))
            .Select(item => item.Oid.Trim().TrimStart('.'))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> CollectSensorParameterValues(
        IReadOnlyList<WorkspaceSensorParameterFieldInput> fields,
        string? advancedText)
    {
        var values = new Dictionary<string, string>(ParseKeyValueLines(advancedText), StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key))
            {
                continue;
            }

            var key = field.Key.Trim();
            var value = field.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static string BuildSensorAdvancedParametersText(
        IReadOnlyDictionary<string, string> values,
        IEnumerable<string> consumedKeys)
    {
        var consumed = consumedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = values
            .Where(pair => !consumed.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}");

        return string.Join(Environment.NewLine, lines);
    }

    private static SensorThresholdEditorState BuildSensorThresholdEditorState(
        string? sensorTypeKey,
        SensorChannelMode channelMode,
        MonitoringSettings settings,
        IReadOnlyList<WorkspaceSensorChannelThresholdFieldInput> currentFields,
        IReadOnlyList<SensorChannelValue>? observedChannels)
    {
        const int minimumRows = 4;
        const int maximumRows = 10;

        var rows = new List<WorkspaceSensorChannelThresholdFieldInput>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentFieldMap = currentFields
            .Where(field => !string.IsNullOrWhiteSpace(field.ChannelKey))
            .GroupBy(field => field.ChannelKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var hint in BuildSensorChannelHints(sensorTypeKey, settings, observedChannels))
        {
            if (!usedKeys.Add(hint.Key))
            {
                continue;
            }

            rows.Add(BuildSensorThresholdField(sensorTypeKey, settings, hint.Key, hint.Label, hint.Unit, hint.IsDefault, hint.LogByDefault, hint.IsVirtual, currentFieldMap));
        }

        foreach (var channelKey in EnumerateManagedThresholdChannelKeys(settings))
        {
            if (!usedKeys.Add(channelKey))
            {
                continue;
            }

            rows.Add(BuildSensorThresholdField(sensorTypeKey, settings, channelKey, null, null, false, true, false, currentFieldMap));
        }

        foreach (var field in currentFields)
        {
            if (string.IsNullOrWhiteSpace(field.ChannelKey) || !usedKeys.Add(field.ChannelKey.Trim()))
            {
                continue;
            }

            rows.Add(BuildSensorThresholdField(sensorTypeKey, settings, field.ChannelKey.Trim(), field.ChannelLabel, field.Unit, field.IsDefault, field.LogByDefault, field.IsVirtual, currentFieldMap));
        }

        var configuredCount = rows.Count(row => HasThresholdValues(row));
        var visibleCount = channelMode == SensorChannelMode.Fixed
            ? rows.Count
            : Math.Min(maximumRows, Math.Max(minimumRows, configuredCount + 1));

        if (channelMode == SensorChannelMode.Dynamic)
        {
            while (rows.Count < maximumRows)
            {
                rows.Add(new WorkspaceSensorChannelThresholdFieldInput());
            }
        }

        return new SensorThresholdEditorState(rows, visibleCount);
    }

    private static IEnumerable<SensorChannelValue> BuildSensorChannelHints(
        string? sensorTypeKey,
        MonitoringSettings settings,
        IReadOnlyList<SensorChannelValue>? observedChannels)
    {
        var defaultChannelKey = settings.DefaultChannelKey;

        if (observedChannels is { Count: > 0 })
        {
            var ordered = observedChannels
                .OrderBy(channel => channel.IsVirtual) // real channels first, virtual (sensorState) last
                .ThenByDescending(channel => !string.IsNullOrWhiteSpace(defaultChannelKey) &&
                    string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(channel => channel.IsDefault)
                .ThenBy(channel => string.IsNullOrWhiteSpace(channel.Label) ? channel.Key : channel.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return string.IsNullOrWhiteSpace(defaultChannelKey)
                ? ordered
                : ordered.Select(channel => channel with
                {
                    IsDefault = string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase)
                }).ToArray();
        }

        List<SensorChannelValue> hints;

        if (string.Equals(sensorTypeKey, PingSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            hints =
            [
                new SensorChannelValue
                {
                    Key = "latency",
                    Label = "Latency",
                    Unit = "ms",
                    IsDefault = true
                }
            ];
        }
        else if (string.Equals(sensorTypeKey, HttpSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            hints =
            [
                new SensorChannelValue
                {
                    Key = "latency",
                    Label = "Latency",
                    Unit = "ms",
                    IsDefault = true
                },
                new SensorChannelValue
                {
                    Key = "statusCode",
                    Label = "Status code"
                }
            ];
        }
        else if (string.Equals(sensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            hints =
            [
                new SensorChannelValue
                {
                    Key = "ageSeconds",
                    Label = "Age",
                    Unit = "s",
                    IsDefault = true
                }
            ];
        }
        else if (string.Equals(sensorTypeKey, SslCertificateSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            hints =
            [
                new SensorChannelValue
                {
                    Key = "remainingDays",
                    Label = "Remaining days",
                    Unit = "d",
                    IsDefault = true
                },
                new SensorChannelValue
                {
                    Key = "valid",
                    Label = "Valid",
                    LogByDefault = false
                }
            ];
        }
        else if (string.Equals(sensorTypeKey, TcpPortSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            hints =
            [
                new SensorChannelValue
                {
                    Key = "connectMs",
                    Label = "Connect",
                    Unit = "ms",
                    IsDefault = true
                },
                new SensorChannelValue
                {
                    Key = "open",
                    Label = "Open",
                    LogByDefault = false
                }
            ];
        }
        else if (MonitoringSettings.TryReadParameter(settings, "defaultChannelKey", out var configuredDefaultChannelKey) &&
            !string.IsNullOrWhiteSpace(configuredDefaultChannelKey))
        {
            hints =
            [
                new SensorChannelValue
                {
                    Key = configuredDefaultChannelKey.Trim(),
                    Label = HumanizeChannelKey(configuredDefaultChannelKey),
                    IsDefault = true
                }
            ];
        }
        else
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(defaultChannelKey))
        {
            return hints;
        }

        var selectedHint = hints.FirstOrDefault(channel =>
            string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase));
        if (selectedHint is null)
        {
            return hints;
        }

        return hints
            .Select(channel => channel with
            {
                IsDefault = string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase)
            })
            .ToArray();
    }

    private static IEnumerable<string> EnumerateManagedThresholdChannelKeys(MonitoringSettings settings)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var threshold in settings.Thresholds)
        {
            if (TryParseManagedChannelThresholdKey(threshold.Key, out var channelKey, out _))
            {
                keys.Add(channelKey);
                continue;
            }

            if (IsLegacyLatencyThresholdKey(threshold.Key))
            {
                keys.Add("latency");
            }
        }

        if (settings.Parameters.ContainsKey("warningAgeSeconds") || settings.Parameters.ContainsKey("criticalAgeSeconds"))
        {
            keys.Add("ageSeconds");
        }

        return keys;
    }

    private static WorkspaceSensorChannelThresholdFieldInput BuildSensorThresholdField(
        string? sensorTypeKey,
        MonitoringSettings settings,
        string channelKey,
        string? channelLabel,
        string? unit,
        bool isDefault,
        bool logByDefault,
        bool isVirtual,
        IReadOnlyDictionary<string, WorkspaceSensorChannelThresholdFieldInput> currentFields)
    {
        currentFields.TryGetValue(channelKey, out var currentField);
        var field = new WorkspaceSensorChannelThresholdFieldInput
        {
            ChannelKey = channelKey,
            ChannelLabel = string.IsNullOrWhiteSpace(channelLabel) ? HumanizeChannelKey(channelKey) : channelLabel,
            Unit = unit,
            IsVirtual = isVirtual || (currentField?.IsVirtual ?? false),
            IsDefault = currentField?.IsDefault
                ?? (((!string.IsNullOrWhiteSpace(settings.DefaultChannelKey) &&
                    string.Equals(channelKey, settings.DefaultChannelKey, StringComparison.OrdinalIgnoreCase))
                || isDefault)),
            LogByDefault = logByDefault,
            Logged = MonitoringSettings.GetChannelLogged(settings, channelKey) ?? logByDefault,
            IsDeleted = currentField?.IsDeleted ?? false
        };

        var warningPlaceholder = string.Empty;
        var criticalPlaceholder = string.Empty;

        if (MonitoringSettings.TryReadChannelThreshold(settings, channelKey, "warning", out var warningRule))
        {
            field.WarningComparison = ToThresholdComparisonText(warningRule.Direction);
            warningPlaceholder = FormatThresholdValue(warningRule.Value);
        }
        else if (TryGetDefaultThresholdRule(sensorTypeKey, channelKey, "warning", out warningRule))
        {
            field.WarningComparison = ToThresholdComparisonText(warningRule.Direction);
            warningPlaceholder = FormatThresholdValue(warningRule.Value);
        }

        if (MonitoringSettings.TryReadChannelThreshold(settings, channelKey, "critical", out var criticalRule))
        {
            field.CriticalComparison = ToThresholdComparisonText(criticalRule.Direction);
            criticalPlaceholder = FormatThresholdValue(criticalRule.Value);
        }
        else if (TryGetDefaultThresholdRule(sensorTypeKey, channelKey, "critical", out criticalRule))
        {
            field.CriticalComparison = ToThresholdComparisonText(criticalRule.Direction);
            criticalPlaceholder = FormatThresholdValue(criticalRule.Value);
        }

        if (currentField is not null)
        {
            if (!string.IsNullOrWhiteSpace(currentField.ChannelLabel))
            {
                field.ChannelLabel = currentField.ChannelLabel;
            }

            if (!string.IsNullOrWhiteSpace(currentField.Unit))
            {
                field.Unit = currentField.Unit;
            }

            field.IsDefault |= currentField.IsDefault;

            if (!string.IsNullOrWhiteSpace(currentField.WarningComparison))
            {
                field.WarningComparison = currentField.WarningComparison;
            }

            if (!string.IsNullOrWhiteSpace(currentField.WarningValue))
            {
                field.WarningValue = currentField.WarningValue;
            }
            else if (!string.IsNullOrWhiteSpace(currentField.WarningValuePlaceholder))
            {
                warningPlaceholder = currentField.WarningValuePlaceholder;
            }

            if (!string.IsNullOrWhiteSpace(currentField.CriticalComparison))
            {
                field.CriticalComparison = currentField.CriticalComparison;
            }

            if (!string.IsNullOrWhiteSpace(currentField.CriticalValue))
            {
                field.CriticalValue = currentField.CriticalValue;
            }
            else if (!string.IsNullOrWhiteSpace(currentField.CriticalValuePlaceholder))
            {
                criticalPlaceholder = currentField.CriticalValuePlaceholder;
            }
        }

        field.WarningValuePlaceholder = warningPlaceholder;
        field.CriticalValuePlaceholder = criticalPlaceholder;

        return field;
    }

    private static List<WorkspaceSensorChannelThresholdFieldInput> BuildCurrentThresholdFieldsFromSettings(MonitoringSettings settings)
    {
        var rows = new Dictionary<string, WorkspaceSensorChannelThresholdFieldInput>(StringComparer.OrdinalIgnoreCase);

        foreach (var threshold in settings.Thresholds)
        {
            if (TryParseManagedChannelThresholdKey(threshold.Key, out var channelKey, out var severity))
            {
                var row = GetOrCreateThresholdRow(rows, channelKey);
                if (MonitoringSettings.TryParseThresholdRule(threshold.Value, out var rule))
                {
                    ApplyThresholdRuleToField(row, severity, rule);
                }

                continue;
            }

            if (IsLegacyLatencyThresholdKey(threshold.Key))
            {
                var row = GetOrCreateThresholdRow(rows, "latency");
                var legacySeverity = threshold.Key.StartsWith("warning", StringComparison.OrdinalIgnoreCase) ? "warning" : "critical";
                if (MonitoringSettings.TryParseThresholdRule(threshold.Value, out var rule))
                {
                    ApplyThresholdRuleToField(row, legacySeverity, rule);
                }
            }
        }

        foreach (var row in rows.Values)
        {
            row.Visual = MonitoringSettings.GetChannelVisual(settings, row.ChannelKey);
        }

        return rows.Values
            .OrderBy(row => row.IsDefault ? 0 : 1)
            .ThenBy(row => row.ChannelKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static WorkspaceSensorChannelThresholdFieldInput GetOrCreateThresholdRow(
        IDictionary<string, WorkspaceSensorChannelThresholdFieldInput> rows,
        string channelKey)
    {
        if (rows.TryGetValue(channelKey, out var row))
        {
            return row;
        }

        row = new WorkspaceSensorChannelThresholdFieldInput
        {
            ChannelKey = channelKey,
            ChannelLabel = HumanizeChannelKey(channelKey)
        };
        rows[channelKey] = row;
        return row;
    }

    private static void ApplyThresholdRuleToField(
        WorkspaceSensorChannelThresholdFieldInput field,
        string severity,
        ThresholdRule rule)
    {
        var comparison = ToThresholdComparisonText(rule.Direction);
        var value = FormatThresholdValue(rule.Value);

        if (string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase))
        {
            field.WarningComparison = comparison;
            field.WarningValue = value;
        }
        else
        {
            field.CriticalComparison = comparison;
            field.CriticalValue = value;
        }
    }

    private static bool HasThresholdValues(WorkspaceSensorChannelThresholdFieldInput field)
    {
        return !string.IsNullOrWhiteSpace(field.ChannelKey) &&
            !field.IsDeleted &&
            (!string.IsNullOrWhiteSpace(field.WarningValue) || !string.IsNullOrWhiteSpace(field.CriticalValue));
    }

    private static bool TryGetDefaultThresholdRule(
        string? sensorTypeKey,
        string channelKey,
        string severity,
        out ThresholdRule rule)
    {
        // The central table (SensorThresholdDefaults) is the single source for per-type/channel
        // default conditions; it also seeds them onto a new sensor's settings via Apply().
        if (SensorThresholdDefaults.TryResolve(sensorTypeKey, channelKey, severity, out rule))
        {
            return true;
        }

        // SSL keeps its own defaults (it also migrates the legacy ssl.warningDays/criticalDays params).
        if (string.Equals(sensorTypeKey, SslCertificateSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(channelKey, SslCertificateSensorExecutor.RemainingDaysChannelKey, StringComparison.OrdinalIgnoreCase))
        {
            rule = new ThresholdRule(
                ThresholdDirection.BelowOrEqual,
                string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase)
                    ? SslCertificateSensorExecutor.DefaultCriticalDays
                    : SslCertificateSensorExecutor.DefaultWarningDays);
            return true;
        }

        rule = default;
        return false;
    }

    private static bool IsLegacyLatencyThresholdKey(string key)
    {
        return string.Equals(key, "warningLatencyMs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "criticalLatencyMs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseManagedChannelThresholdKey(
        string key,
        out string channelKey,
        out string severity)
    {
        if (!key.StartsWith("channel:", StringComparison.OrdinalIgnoreCase))
        {
            channelKey = string.Empty;
            severity = string.Empty;
            return false;
        }

        var lastSeparator = key.LastIndexOf(':');
        if (lastSeparator <= "channel:".Length)
        {
            channelKey = string.Empty;
            severity = string.Empty;
            return false;
        }

        var escapedChannelKey = key["channel:".Length..lastSeparator];
        channelKey = Uri.UnescapeDataString(escapedChannelKey);
        severity = key[(lastSeparator + 1)..].Trim().ToLowerInvariant();
        return severity is "warning" or "critical";
    }

    private static void ApplySensorChannelThresholds(
        MonitoringSettings settings,
        IReadOnlyList<WorkspaceSensorChannelThresholdFieldInput> fields)
    {
        var preservedThresholds = settings.Thresholds
            .Where(threshold => !IsManagedThresholdKey(threshold.Key))
            .ToDictionary(threshold => threshold.Key, threshold => threshold.Value, StringComparer.OrdinalIgnoreCase);

        settings.Thresholds.Clear();
        foreach (var threshold in preservedThresholds)
        {
            settings.Thresholds[threshold.Key] = threshold.Value;
        }

        settings.ChannelVisuals.Clear();
        settings.ChannelLogging.Clear();

        foreach (var field in fields)
        {
            if (field.IsDeleted || string.IsNullOrWhiteSpace(field.ChannelKey))
            {
                continue;
            }

            var channelKey = field.ChannelKey.Trim();
            if (TryBuildThresholdRule(field.WarningComparison, field.WarningValue, out var warningRule))
            {
                MonitoringSettings.SetChannelThreshold(settings, channelKey, "warning", warningRule);
            }

            if (TryBuildThresholdRule(field.CriticalComparison, field.CriticalValue, out var criticalRule))
            {
                MonitoringSettings.SetChannelThreshold(settings, channelKey, "critical", criticalRule);
            }

            MonitoringSettings.SetChannelVisual(settings, channelKey, field.Visual);

            // Only persist a logging override when it differs from the channel's default.
            MonitoringSettings.SetChannelLogged(
                settings,
                channelKey,
                field.Logged == field.LogByDefault ? null : field.Logged);
        }

        var activeChannelKeys = fields
            .Where(field => !field.IsDeleted && !string.IsNullOrWhiteSpace(field.ChannelKey))
            .Select(field => field.ChannelKey.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (activeChannelKeys.Length == 0)
        {
            settings.DefaultChannelKey = null;
            return;
        }

        var selectedDefault = fields.FirstOrDefault(field =>
            !field.IsDeleted &&
            field.IsDefault &&
            !string.IsNullOrWhiteSpace(field.ChannelKey));

        if (selectedDefault is not null)
        {
            settings.DefaultChannelKey = selectedDefault.ChannelKey.Trim();
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultChannelKey) ||
            !activeChannelKeys.Any(channelKey => string.Equals(channelKey, settings.DefaultChannelKey, StringComparison.OrdinalIgnoreCase)))
        {
            settings.DefaultChannelKey = activeChannelKeys[0];
        }
    }

    private static bool IsManagedThresholdKey(string key)
    {
        return TryParseManagedChannelThresholdKey(key, out _, out _) || IsLegacyLatencyThresholdKey(key);
    }

    private static bool TryBuildThresholdRule(
        string? comparison,
        string? value,
        out ThresholdRule rule)
    {
        rule = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!double.TryParse(value.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var numericValue))
        {
            throw new InvalidOperationException("Threshold values must be numeric.");
        }

        var normalizedComparison = comparison?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedComparison))
        {
            rule = new ThresholdRule(ThresholdDirection.Above, numericValue);
            return true;
        }

        if (!TryParseThresholdComparison(normalizedComparison, out var direction))
        {
            throw new InvalidOperationException("Threshold comparison must be one of >, >=, <, <=, =, <>.");
        }

        rule = new ThresholdRule(direction, numericValue);
        return true;
    }

    private static string ToThresholdComparisonText(ThresholdDirection direction)
    {
        return direction switch
        {
            ThresholdDirection.Above => ">",
            ThresholdDirection.AboveOrEqual => ">=",
            ThresholdDirection.Below => "<",
            ThresholdDirection.BelowOrEqual => "<=",
            ThresholdDirection.Equal => "=",
            ThresholdDirection.NotEqual => "<>",
            _ => ">"
        };
    }

    private static bool TryParseThresholdComparison(string comparison, out ThresholdDirection direction)
    {
        var normalized = comparison.Trim().ToLowerInvariant();
        if (normalized is ">")
        {
            direction = ThresholdDirection.Above;
            return true;
        }

        if (normalized is ">=")
        {
            direction = ThresholdDirection.AboveOrEqual;
            return true;
        }

        if (normalized is "<")
        {
            direction = ThresholdDirection.Below;
            return true;
        }

        if (normalized is "<=")
        {
            direction = ThresholdDirection.BelowOrEqual;
            return true;
        }

        if (normalized is "=" or "==")
        {
            direction = ThresholdDirection.Equal;
            return true;
        }

        if (normalized is "<>" or "!=")
        {
            direction = ThresholdDirection.NotEqual;
            return true;
        }

        direction = default;
        return false;
    }

    private static string FormatThresholdValue(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string HumanizeChannelKey(string channelKey)
    {
        if (string.IsNullOrWhiteSpace(channelKey))
        {
            return "Channel";
        }

        var normalized = channelKey.Trim()
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Replace('.', ' ')
            .Replace('/', ' ')
            .Replace(':', ' ');

        var builder = new List<char>(normalized.Length + 8);
        for (var index = 0; index < normalized.Length; index++)
        {
            var current = normalized[index];
            if (index > 0 &&
                char.IsLower(normalized[index - 1]) &&
                char.IsUpper(current))
            {
                builder.Add(' ');
            }

            builder.Add(current);
        }

        var text = new string(builder.ToArray()).Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }

    private static string NormalizeSensorParameterValue(SensorParameterDefinition parameter, string rawValue)
    {
        return parameter.Kind switch
        {
            SensorParameterKind.Integer =>
                int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerValue)
                    ? integerValue.ToString(CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException($"Parameter '{parameter.Label}' must be an integer."),
            SensorParameterKind.Decimal =>
                decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue)
                    ? decimalValue.ToString(CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException($"Parameter '{parameter.Label}' must be a decimal."),
            SensorParameterKind.Boolean =>
                TryNormalizeBooleanValue(rawValue, out var booleanValue)
                    ? booleanValue
                    : throw new InvalidOperationException($"Parameter '{parameter.Label}' must be true or false."),
            SensorParameterKind.ValueList => rawValue.Trim(),
            _ => rawValue
        };
    }

    private static List<SelectListItem> BuildValueListOptions(SensorParameterDefinition parameter, string? currentValue, string? displayPlaceholder)
    {
        var options = new List<SelectListItem>();
        var selectedValue = currentValue ?? string.Empty;
        var hasKnownSelection = false;

        var blankLabel = !string.IsNullOrWhiteSpace(displayPlaceholder)
            ? $"-- inherit ({displayPlaceholder}) --"
            : "-- not set --";
        options.Add(new SelectListItem(blankLabel, string.Empty, string.IsNullOrWhiteSpace(selectedValue)));

        foreach (var option in parameter.Options)
        {
            var selected = string.Equals(option.Value, selectedValue, StringComparison.OrdinalIgnoreCase);
            hasKnownSelection |= selected;
            options.Add(new SelectListItem(option.Label, option.Value, selected));
        }

        if (!string.IsNullOrWhiteSpace(selectedValue) && !hasKnownSelection)
        {
            options.Add(new SelectListItem($"Custom: {selectedValue}", selectedValue, true));
        }

        return options;
    }

    private static List<SelectListItem> BuildBooleanOptions(SensorParameterDefinition parameter, string? currentValue, string? displayPlaceholder)
    {
        var options = new List<SelectListItem>();
        var selectedValue = currentValue ?? string.Empty;

        var blankLabel = !string.IsNullOrWhiteSpace(displayPlaceholder)
            ? $"-- inherit ({displayPlaceholder}) --"
            : "-- not set --";
        options.Add(new SelectListItem(blankLabel, string.Empty, string.IsNullOrWhiteSpace(selectedValue)));

        if (TryNormalizeBooleanValue(selectedValue, out var normalizedValue))
        {
            options.Add(new SelectListItem("True", "true", string.Equals(normalizedValue, "true", StringComparison.OrdinalIgnoreCase)));
            options.Add(new SelectListItem("False", "false", string.Equals(normalizedValue, "false", StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(selectedValue))
            {
                options.Add(new SelectListItem($"Custom: {selectedValue}", selectedValue, true));
            }

            options.Add(new SelectListItem("True", "true", false));
            options.Add(new SelectListItem("False", "false", false));
        }

        return options;
    }

    private static bool TryNormalizeBooleanValue(string rawValue, out string normalizedValue)
    {
        var normalized = rawValue.Trim().ToLowerInvariant();
        if (normalized is "true" or "1" or "yes" or "on")
        {
            normalizedValue = "true";
            return true;
        }

        if (normalized is "false" or "0" or "no" or "off")
        {
            normalizedValue = "false";
            return true;
        }

        normalizedValue = string.Empty;
        return false;
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

    private int ApplyElementEditor(MonitoringElement element, WorkspaceElementEditorInput editor)
    {
        element.Name = editor.Name.Trim();
        element.Description = string.IsNullOrWhiteSpace(editor.Description) ? null : editor.Description.Trim();
        element.Tags = MonitoringTagResolver.Parse(editor.TagsText);
        var parentChanged = false;

        SensorDefinition? sensorDefinition = null;
        IReadOnlyDictionary<string, string>? sensorParameters = null;
        if (element is ProbeElement probe)
        {
            probe.ProbeId = string.IsNullOrWhiteSpace(editor.ProbeId) ? probe.ProbeId : editor.ProbeId.Trim();
            probe.EnrollmentToken = string.IsNullOrWhiteSpace(editor.EnrollmentToken) ? probe.EnrollmentToken : editor.EnrollmentToken.Trim();
            probe.Subnets = (editor.ProbeSubnetsText ?? string.Empty)
                .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (element is HostElement host)
        {
            host.Address = editor.Address?.Trim() ?? string.Empty;
        }

        if (element is SensorElement sensorElement)
        {
            var sensorTypeKey = string.IsNullOrWhiteSpace(editor.SensorTypeKey) ? sensorElement.SensorTypeKey : editor.SensorTypeKey.Trim();

            // The sensor type is immutable after creation: history, statistics and the
            // channel set are all keyed to it, so switching it would orphan that data.
            // The UI disables the field; this guards against a tampered / stale post.
            if (!string.Equals(sensorTypeKey, sensorElement.SensorTypeKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The sensor type cannot be changed after creation — its history, channels and statistics depend on it. Create a new sensor to use a different type.");
            }

            sensorDefinition = RequireSensorDefinition(sensorTypeKey);
            var existingSensorValues = ResolveElementEffectiveSettings(sensorElement).Parameters;
            sensorParameters = BuildSensorParameterValues(
                sensorDefinition,
                editor.SensorParameterFields,
                editor.SensorAdvancedParametersText,
                existingSensorValues);
            sensorElement.SensorTypeKey = sensorTypeKey;
            sensorElement.Target = editor.Target?.Trim() ?? string.Empty;
        }

        var selectedParentId = editor.ParentId;
        if (element.ParentId != selectedParentId && selectedParentId.HasValue)
        {
            if (!_workspaceStore.MoveElement(element.Id, selectedParentId.Value))
            {
                throw new InvalidOperationException("Parent konnte nicht geaendert werden.");
            }

            parentChanged = true;
        }

        if (element is SensorElement heartbeatSensor &&
            string.Equals(heartbeatSensor.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            heartbeatSensor.Target = ResolveProbeIdForElement(element.ParentId) ?? heartbeatSensor.Target;

            if (string.IsNullOrWhiteSpace(heartbeatSensor.Target))
            {
                throw new InvalidOperationException("Heartbeat sensors must be attached to a non-root probe.");
            }
        }

        if (element is SensorElement)
        {
            ApplySettings(element.Settings, editor.EnabledMode, editor.PollingIntervalSeconds, editor.TimeoutSeconds, editor.RetryCount, editor.Highlight, null, null);
        }
        else
        {
            ApplySettings(element.Settings, editor.EnabledMode, editor.PollingIntervalSeconds, editor.TimeoutSeconds, editor.RetryCount, null, editor.ParametersText, editor.ThresholdsText);
        }

        ApplyScheduleSettings(
            element.Settings,
            editor.SchedulePreset,
            editor.ScheduleEveryValue,
            editor.ScheduleEveryUnit,
            editor.ScheduleDaysOfWeek,
            editor.ScheduleDayOfMonth,
            editor.ScheduleTime);

        ApplyRetentionSettings(
            element.Settings,
            editor.EventRetentionDays,
            editor.ObservationRetentionDays,
            editor.StatisticsRetentionDays,
            editor.StatisticsBucketMinutes);

        if (element is SensorElement)
        {
            element.Settings.GraphMinValue = editor.GraphMinValue;
            element.Settings.GraphMaxValue = editor.GraphMaxValue;
        }

        element.Settings.SelectedCredentialId = editor.SelectedCredentialId;

        if (element is not SensorElement)
        {
            ApplyCredentialBundles(element.Settings, editor.CredentialBundles);
        }

        if (element is SensorElement sensorForParameters && sensorDefinition is not null && sensorParameters is not null)
        {
            sensorForParameters.Settings.Parameters.Clear();
            foreach (var pair in sensorParameters)
            {
                sensorForParameters.Settings.Parameters[pair.Key] = pair.Value;
            }

            ApplySensorChannelThresholds(sensorForParameters.Settings, editor.SensorChannelThresholdFields);
        }

        var inheritedSettings = ResolveElementInheritedSettings(element);
        MonitoringSettings.StripInheritedValues(element.Settings, inheritedSettings);
        return parentChanged || element is SensorElement or MonitoringContainerElement
            ? RecordSensorCredentialConfigurationIssues(element)
            : 0;
    }

    private void ApplyTemplateEditor(MonitoringTemplate template, WorkspaceTemplateEditorInput editor)
    {
        template.Name = editor.Name.Trim();
        template.Tags = MonitoringTagResolver.Parse(editor.TagsText);
        template.TargetKind = editor.TargetKind;
        template.SensorTypeKey = editor.TargetKind == MonitoringTemplateScope.Sensor
            ? RequireSensorDefinition(editor.SensorTypeKey).Key
            : null;
        template.ParentTemplateId = editor.ParentTemplateId;

        if (editor.TargetKind == MonitoringTemplateScope.Sensor)
        {
            // Parameters come from the structured per-field inputs (same as the sensor
            // editor), so don't let the raw ParametersText clear them here.
            var existingParameters = new Dictionary<string, string>(template.Settings.Parameters, StringComparer.OrdinalIgnoreCase);
            ApplySettings(template.Settings, editor.EnabledMode, editor.PollingIntervalSeconds, editor.TimeoutSeconds, editor.RetryCount, editor.Highlight, parametersText: null, thresholdsText: null);
            var definition = FindSensorDefinition(_workspaceStore.Workspace.SensorDefinitions, editor.SensorTypeKey);
            if (definition is not null)
            {
                ApplySensorParameters(template.Settings, definition, editor.SensorParameterFields, editor.SensorAdvancedParametersText, existingParameters);
            }
            ApplySensorChannelThresholds(template.Settings, editor.SensorChannelThresholdFields);
        }
        else
        {
            ApplySettings(template.Settings, editor.EnabledMode, editor.PollingIntervalSeconds, editor.TimeoutSeconds, editor.RetryCount, null, editor.ParametersText, editor.ThresholdsText);
        }

        ApplyScheduleSettings(
            template.Settings,
            editor.SchedulePreset,
            editor.ScheduleEveryValue,
            editor.ScheduleEveryUnit,
            editor.ScheduleDaysOfWeek,
            editor.ScheduleDayOfMonth,
            editor.ScheduleTime);

        ApplyRetentionSettings(
            template.Settings,
            editor.EventRetentionDays,
            editor.ObservationRetentionDays,
            editor.StatisticsRetentionDays,
            editor.StatisticsBucketMinutes);

        ApplyCredentialBundles(template.Settings, editor.CredentialBundles);
        template.Settings.SelectedCredentialId = editor.SelectedCredentialId;

        var inheritedSettings = ResolveTemplateInheritedSettings(template);
        MonitoringSettings.StripInheritedValues(template.Settings, inheritedSettings);
    }

    private void NormalizeHeartbeatSensorTarget(CreateSensorInput editor)
    {
        if (!string.Equals(editor.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(editor.Target))
        {
            return;
        }

        editor.Target = ResolveProbeIdForElement(editor.ParentId) ?? string.Empty;
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

    private static List<DayOfWeek> NormalizeScheduleDays(IReadOnlyList<DayOfWeek>? days)
    {
        var list = (days ?? []).Distinct().OrderBy(day => (int)day).ToList();
        return list.Count > 0 ? list : [DayOfWeek.Monday];
    }

    private static void ApplyScheduleSettings(
        MonitoringSettings settings,
        string? scheduleMode,
        int? scheduleEveryValue,
        string? scheduleEveryUnit,
        IReadOnlyList<DayOfWeek>? scheduleDaysOfWeek,
        int? scheduleDayOfMonth,
        string? scheduleTime)
    {
        settings.PollingInterval = null;
        settings.PollingSchedule = null;

        var mode = string.IsNullOrWhiteSpace(scheduleMode)
            ? "inherit"
            : scheduleMode.Trim().ToLowerInvariant();

        if (mode == "inherit")
        {
            return;
        }

        var timeOfDay = ParseScheduleTime(scheduleTime);
        settings.PollingSchedule = mode switch
        {
            "every" or "custom" => new MonitoringSchedule
            {
                Mode = MonitoringScheduleMode.Every,
                EverySeconds = ResolveEverySeconds(scheduleEveryValue, scheduleEveryUnit)
            },
            "daily" => new MonitoringSchedule { Mode = MonitoringScheduleMode.Daily, TimeOfDay = timeOfDay },
            "weekly" => new MonitoringSchedule
            {
                Mode = MonitoringScheduleMode.Weekly,
                DaysOfWeek = NormalizeScheduleDays(scheduleDaysOfWeek),
                TimeOfDay = timeOfDay
            },
            "monthly" => new MonitoringSchedule
            {
                Mode = MonitoringScheduleMode.Monthly,
                DayOfMonth = Math.Clamp(scheduleDayOfMonth ?? 1, 1, 31),
                TimeOfDay = timeOfDay
            },
            // Backward-compat with older fixed presets that may still be posted.
            "every-30s" => new MonitoringSchedule { Mode = MonitoringScheduleMode.Every, EverySeconds = 30 },
            "every-5m" => new MonitoringSchedule { Mode = MonitoringScheduleMode.Every, EverySeconds = 300 },
            "hourly" => new MonitoringSchedule { Mode = MonitoringScheduleMode.Every, EverySeconds = 3600 },
            "every-2h" => new MonitoringSchedule { Mode = MonitoringScheduleMode.Every, EverySeconds = 7200 },
            _ => null
        };
    }

    private static int ResolveEverySeconds(int? value, string? unit)
    {
        var safeValue = Math.Max(value ?? 1, 1);
        var factor = (unit?.Trim().ToLowerInvariant()) switch
        {
            "second" or "seconds" or "s" => 1,
            "hour" or "hours" or "h" => 3600,
            "day" or "days" or "d" => 86400,
            _ => 60 // minutes
        };

        // Enforce a sane minimum so a misconfigured value cannot hammer a target.
        return Math.Max(safeValue * factor, 5);
    }

    private static TimeSpan ParseScheduleTime(string? scheduleTime)
    {
        if (TimeSpan.TryParse(scheduleTime, CultureInfo.InvariantCulture, out var time) &&
            time >= TimeSpan.Zero &&
            time < TimeSpan.FromDays(1))
        {
            return time;
        }

        return TimeSpan.Zero;
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

    private static void ApplyCredentialBundles(MonitoringSettings settings, IReadOnlyList<WorkspaceCredentialBundleInput> bundles)
    {
        // Snapshot the existing decrypted values per bundle BEFORE clearing, so a Secret field
        // the form rendered blank (password inputs never echo their value) preserves the stored
        // secret instead of wiping it on an unrelated save.
        var existingValuesById = settings.Credentials
            .Where(credential => credential.Id != Guid.Empty)
            .GroupBy(credential => credential.Id)
            .ToDictionary(group => group.Key, group => group.First().Values, EqualityComparer<Guid>.Default);

        settings.Credentials.Clear();

        foreach (var bundle in bundles)
        {
            if (bundle.IsDeleted)
            {
                continue;
            }

            existingValuesById.TryGetValue(bundle.Id, out var existing);
            // Preserve any already-stored values for this bundle (raw key=value editing was
            // removed app-wide); the explicit per-kind fields below overlay them.
            var values = existing is not null
                ? new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            switch (bundle.Kind)
            {
                case MonitoringCredentialKind.Windows:
                    ApplyCredentialBundleField(values, "winrm.username", bundle.WinrmUsername);
                    ApplyCredentialSecretField(values, "winrm.password", bundle.WinrmPassword, existing);
                    break;
                case MonitoringCredentialKind.Linux:
                case MonitoringCredentialKind.Ssh:
                    ApplyCredentialBundleField(values, "ssh.username", bundle.SshUsername);
                    ApplyCredentialSecretField(values, "ssh.password", bundle.SshPassword, existing);
                    ApplyCredentialBundleField(values, "ssh.privateKeyPath", bundle.SshPrivateKeyPath);
                    break;
                case MonitoringCredentialKind.Proxmox:
                    // pve.user is no longer an editable field (the Token ID carries user@realm!name,
                    // matching the Proxmox UI) — leave any stored value untouched for back-compat.
                    ApplyCredentialBundleField(values, "pve.tokenId", bundle.PveTokenId);
                    ApplyCredentialSecretField(values, "pve.tokenSecret", bundle.PveTokenSecret, existing);
                    break;
                case MonitoringCredentialKind.SqlServer:
                    ApplyCredentialBundleField(values, "mssql.username", bundle.MssqlUsername);
                    ApplyCredentialSecretField(values, "mssql.password", bundle.MssqlPassword, existing);
                    break;
                case MonitoringCredentialKind.Snmp:
                    ApplyCredentialBundleField(values, "snmp.community", bundle.SnmpCommunity);
                    ApplyCredentialBundleField(values, "snmp.v3.username", bundle.SnmpV3Username);
                    ApplyCredentialBundleField(values, "snmp.v3.authProtocol", bundle.SnmpV3AuthProtocol);
                    ApplyCredentialSecretField(values, "snmp.v3.authPassword", bundle.SnmpV3AuthPassword, existing);
                    ApplyCredentialBundleField(values, "snmp.v3.privProtocol", bundle.SnmpV3PrivacyProtocol);
                    ApplyCredentialSecretField(values, "snmp.v3.privPassword", bundle.SnmpV3PrivacyPassword, existing);
                    ApplyCredentialBundleField(values, "snmp.v3.contextName", bundle.SnmpV3ContextName);
                    break;
                case MonitoringCredentialKind.Unifi:
                    ApplyCredentialSecretField(values, "unifi.apiKey", bundle.UnifiApiKey, existing);
                    break;
                case MonitoringCredentialKind.Generic:
                    ApplyCredentialBundleField(values, "generic.username", bundle.GenericUsername);
                    ApplyCredentialSecretField(values, "generic.password", bundle.GenericPassword, existing);
                    ApplyCredentialSecretField(values, "generic.token", bundle.GenericToken, existing);
                    break;
            }

            if (string.IsNullOrWhiteSpace(bundle.Name) && values.Count == 0)
            {
                continue;
            }

            var credential = new MonitoringCredentialBundle
            {
                Id = bundle.Id == Guid.Empty ? Guid.NewGuid() : bundle.Id,
                Name = string.IsNullOrWhiteSpace(bundle.Name) ? "Credential" : bundle.Name.Trim(),
                Kind = bundle.Kind,
                Description = string.IsNullOrWhiteSpace(bundle.Description) ? null : bundle.Description.Trim(),
                Values = values
            };

            settings.Credentials.Add(credential);
        }
    }

    private static void ApplyCredentialBundleField(Dictionary<string, string> values, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            values.Remove(key);
            return;
        }

        values[key] = value.Trim();
    }

    /// <summary>
    /// Like <see cref="ApplyCredentialBundleField"/> but for a Secret field: a password input
    /// never echoes its value, so a blank posted value means "unchanged" — keep the existing
    /// stored secret rather than wiping it. (Clearing a secret requires deleting the bundle.)
    /// </summary>
    private static void ApplyCredentialSecretField(
        Dictionary<string, string> values,
        string key,
        string? value,
        IReadOnlyDictionary<string, string>? existing)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
            return;
        }

        if (existing is not null && existing.TryGetValue(key, out var existingValue) && !string.IsNullOrWhiteSpace(existingValue))
        {
            values[key] = existingValue;
            return;
        }

        values.Remove(key);
    }

    private static List<SelectListItem> BuildCredentialKindOptions(MonitoringCredentialKind? selectedKind = null)
    {
        return Enum.GetValues<MonitoringCredentialKind>()
            .Select(kind => new SelectListItem(
                kind.ToString(),
                kind.ToString(),
                selectedKind == kind))
            .ToList();
    }

    private static List<SelectListItem> BuildCredentialOptions(
        IEnumerable<MonitoringCredentialBundle> credentials,
        IEnumerable<MonitoringCredentialKind> allowedKinds,
        Guid? selectedCredentialId)
    {
        var allowed = allowedKinds is ICollection<MonitoringCredentialKind> collection
            ? collection.ToHashSet()
            : allowedKinds.ToHashSet();
        var allowedLabel = allowed.Count == 0
            ? "credential"
            : string.Join(" / ", allowed.OrderBy(kind => kind.ToString()).Select(kind => kind.ToString()));
        var credentialList = credentials.ToList();
        var automaticCredential = allowed.Count == 0
            ? null
            : credentialList.FirstOrDefault(credential => allowed.Contains(credential.Kind));
        var autoLabel = automaticCredential is null
            ? $"Auto / inherit ({allowedLabel}: none available)"
            : $"Auto / inherit ({automaticCredential.Kind}: {automaticCredential.Name})";

        var options = new List<SelectListItem>
        {
            new(autoLabel, string.Empty, selectedCredentialId is null)
        };

        options.AddRange(credentialList
            .Where(credential => allowed.Count == 0 || allowed.Contains(credential.Kind))
            .Select(credential => new SelectListItem(
                $"{credential.Kind}: {credential.Name}",
                credential.Id.ToString(),
                credential.Id == selectedCredentialId)));

        if (selectedCredentialId is Guid selectedId &&
            !options.Any(option => string.Equals(option.Value, selectedId.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            var selectedCredential = credentialList.FirstOrDefault(credential => credential.Id == selectedId);
            var label = selectedCredential is null
                ? $"Missing credential ({selectedId})"
                : $"Unavailable for this sensor: {selectedCredential.Name} ({selectedCredential.Kind})";
            options.Add(new SelectListItem(label, selectedId.ToString(), true));
        }

        return options;
    }

    private static List<WorkspaceCredentialBundleInput> BuildCredentialBundleInputs(IEnumerable<MonitoringCredentialBundle> credentials)
    {
        return credentials
            .Select(credential => new WorkspaceCredentialBundleInput
            {
                Id = credential.Id,
                Name = credential.Name,
                Kind = credential.Kind,
                Description = credential.Description,
                WinrmUsername = ReadCredentialField(credential.Values, "winrm.username"),
                WinrmPassword = ReadCredentialField(credential.Values, "winrm.password"),
                SshUsername = ReadCredentialField(credential.Values, "ssh.username"),
                SshPassword = ReadCredentialField(credential.Values, "ssh.password"),
                SshPrivateKeyPath = ReadCredentialField(credential.Values, "ssh.privateKeyPath"),
                PveUser = ReadCredentialField(credential.Values, "pve.user"),
                PveTokenId = ReadCredentialField(credential.Values, "pve.tokenId"),
                PveTokenSecret = ReadCredentialField(credential.Values, "pve.tokenSecret"),
                MssqlUsername = ReadCredentialField(credential.Values, "mssql.username"),
                MssqlPassword = ReadCredentialField(credential.Values, "mssql.password"),
                SnmpCommunity = ReadCredentialField(credential.Values, "snmp.community"),
                SnmpV3Username = ReadCredentialField(credential.Values, "snmp.v3.username"),
                SnmpV3AuthProtocol = ReadCredentialField(credential.Values, "snmp.v3.authProtocol"),
                SnmpV3AuthPassword = ReadCredentialField(credential.Values, "snmp.v3.authPassword"),
                SnmpV3PrivacyProtocol = ReadCredentialField(credential.Values, "snmp.v3.privProtocol"),
                SnmpV3PrivacyPassword = ReadCredentialField(credential.Values, "snmp.v3.privPassword"),
                SnmpV3ContextName = ReadCredentialField(credential.Values, "snmp.v3.contextName"),
                UnifiApiKey = ReadCredentialField(credential.Values, "unifi.apiKey"),
                GenericUsername = ReadCredentialField(credential.Values, "generic.username"),
                GenericPassword = ReadCredentialField(credential.Values, "generic.password"),
                GenericToken = ReadCredentialField(credential.Values, "generic.token"),
                ValuesText = string.Join(
                    Environment.NewLine,
                    credential.Values
                        .Where(pair => !IsCredentialBundleCoreKey(pair.Key))
                        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(pair => $"{pair.Key}={EscapeKeyValuePart(pair.Value)}"))
            })
            .ToList();
    }

    private static bool IsCredentialBundleCoreKey(string key)
    {
        return key.Equals("winrm.username", StringComparison.OrdinalIgnoreCase)
            || key.Equals("winrm.password", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ssh.username", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ssh.password", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ssh.privateKeyPath", StringComparison.OrdinalIgnoreCase)
            || key.Equals("pve.user", StringComparison.OrdinalIgnoreCase)
            || key.Equals("pve.tokenId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("pve.tokenSecret", StringComparison.OrdinalIgnoreCase)
            || key.Equals("mssql.username", StringComparison.OrdinalIgnoreCase)
            || key.Equals("mssql.password", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.community", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.username", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.authProtocol", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.authPassword", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.privProtocol", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.privPassword", StringComparison.OrdinalIgnoreCase)
            || key.Equals("snmp.v3.contextName", StringComparison.OrdinalIgnoreCase)
            || key.Equals("unifi.apiKey", StringComparison.OrdinalIgnoreCase)
            || key.Equals("generic.username", StringComparison.OrdinalIgnoreCase)
            || key.Equals("generic.password", StringComparison.OrdinalIgnoreCase)
            || key.Equals("generic.token", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadCredentialField(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static (List<WorkspaceCredentialBundleInput> Bundles, int VisibleCount) BuildCredentialBundleEditorState(
        IEnumerable<MonitoringCredentialBundle> credentials)
    {
        const int maximumRows = 8;

        var bundles = BuildCredentialBundleInputs(credentials).ToList();
        // Only the real bundles are shown as list rows; the remaining slots are hidden and
        // revealed one at a time by the "Add credential" button (which opens its dialog).
        var visibleCount = bundles.Count;

        while (bundles.Count < maximumRows)
        {
            bundles.Add(new WorkspaceCredentialBundleInput());
        }

        return (bundles, visibleCount);
    }

    private static int? NormalizePositiveInteger(int? value)
    {
        return value is int integerValue && integerValue > 0 ? integerValue : null;
    }

    private void ApplySelectedSensorTemplateDefaults(MonitoringWorkspaceSnapshot snapshot, bool populateEditorValues)
    {
        var template = ResolveSelectedSensorTemplate(snapshot, requireSensorTypeMatch: false);
        if (template is null)
        {
            return;
        }

        ApplySelectedSensorTemplateDefaults(template, populateEditorValues);
    }

    private void ApplySelectedSensorTemplateDefaults(MonitoringTemplate template, bool populateEditorValues)
    {
        NewSensor.TemplateId = template.Id;

        if (!string.IsNullOrWhiteSpace(template.SensorTypeKey))
        {
            NewSensor.SensorTypeKey = template.SensorTypeKey.Trim();
        }

        NewSensor.SensorChannelMode = GetSensorChannelMode(NewSensor.SensorTypeKey);

        var effectiveTemplateSettings = ResolveTemplateEffectiveSettings(template);
        NewSensor.HighlightInheritedLabel = FormatInheritedBooleanLabel(effectiveTemplateSettings.Highlight);
    }

    private void NormalizeSelectedSensorTemplateForSensorType(MonitoringWorkspaceSnapshot snapshot)
    {
        if (NewSensor.TemplateId is not Guid templateId)
        {
            return;
        }

        var template = snapshot.Templates.FirstOrDefault(candidate =>
            candidate.Id == templateId &&
            candidate.TargetKind == MonitoringTemplateScope.Sensor);

        if (template is null || !SensorTemplateMatchesType(template, NewSensor.SensorTypeKey))
        {
            NewSensor.TemplateId = null;
        }
    }

    private MonitoringTemplate? ResolveSelectedSensorTemplate()
    {
        return ResolveSelectedSensorTemplate(_workspaceStore.Workspace, requireSensorTypeMatch: true);
    }

    private MonitoringTemplate? ResolveSelectedSensorTemplate(MonitoringWorkspaceSnapshot snapshot, bool requireSensorTypeMatch = true)
    {
        if (NewSensor.TemplateId is not Guid id)
        {
            return null;
        }

        var template = snapshot.Templates.FirstOrDefault(candidate =>
            candidate.Id == id &&
            candidate.TargetKind == MonitoringTemplateScope.Sensor &&
            (!requireSensorTypeMatch || SensorTemplateMatchesType(candidate, NewSensor.SensorTypeKey)));

        return template;
    }

    private static bool SensorTemplateMatchesType(MonitoringTemplate template, string sensorTypeKey)
    {
        return string.IsNullOrWhiteSpace(template.SensorTypeKey) ||
            string.Equals(template.SensorTypeKey.Trim(), sensorTypeKey?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static List<SelectListItem> BuildSensorTemplateOptions(
        IReadOnlyList<MonitoringTemplate> templates,
        string sensorTypeKey,
        Guid? selectedTemplateId)
    {
        var options = new List<SelectListItem>
        {
            new("No template", string.Empty, selectedTemplateId is null)
        };

        options.AddRange(templates
            .Where(template => template.TargetKind == MonitoringTemplateScope.Sensor)
            .Where(template => SensorTemplateMatchesType(template, sensorTypeKey))
            .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .Select(template => new SelectListItem(template.Name, template.Id.ToString(), template.Id == selectedTemplateId)));

        return options;
    }

    private static void ApplyTemplateDefaults(MonitoringSettings target, MonitoringSettings source)
    {
        target.Enabled ??= source.Enabled;
        if (target.PollingSchedule is null && target.PollingInterval is null)
        {
            if (source.PollingSchedule is not null)
            {
                target.PollingSchedule = source.PollingSchedule.Clone();
            }
            else
            {
                target.PollingInterval = source.PollingInterval;
            }
        }

        target.Timeout ??= source.Timeout;
        target.RetryCount ??= source.RetryCount;

        foreach (var threshold in source.Thresholds)
        {
            if (!target.Thresholds.ContainsKey(threshold.Key))
            {
                target.Thresholds[threshold.Key] = threshold.Value;
            }
        }

        foreach (var parameter in source.Parameters)
        {
            if (!target.Parameters.ContainsKey(parameter.Key))
            {
                target.Parameters[parameter.Key] = parameter.Value;
            }
        }
    }

    private static void ApplyOverrideSettings(MonitoringSettings target, MonitoringSettings source)
    {
        target.Enabled = source.Enabled ?? target.Enabled;
        target.Highlight = source.Highlight ?? target.Highlight;
        if (source.PollingInterval is not null)
        {
            target.PollingInterval = source.PollingInterval;
            target.PollingSchedule = null;
        }

        if (source.PollingSchedule is not null)
        {
            target.PollingSchedule = source.PollingSchedule.Clone();
            target.PollingInterval = null;
        }

        target.Timeout = source.Timeout ?? target.Timeout;
        target.RetryCount = source.RetryCount ?? target.RetryCount;
        target.EventRetentionDays = source.EventRetentionDays ?? target.EventRetentionDays;
        target.ObservationRetentionDays = source.ObservationRetentionDays ?? target.ObservationRetentionDays;
        target.StatisticsRetentionDays = source.StatisticsRetentionDays ?? target.StatisticsRetentionDays;
        target.StatisticsBucketMinutes = source.StatisticsBucketMinutes ?? target.StatisticsBucketMinutes;
        target.DefaultChannelKey = source.DefaultChannelKey ?? target.DefaultChannelKey;

        foreach (var threshold in source.Thresholds)
        {
            target.Thresholds[threshold.Key] = threshold.Value;
        }

        foreach (var parameter in source.Parameters)
        {
            target.Parameters[parameter.Key] = parameter.Value;
        }
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

    private MonitoringSettings ResolveTemplateEffectiveSettings(MonitoringTemplate template)
    {
        var templates = _workspaceStore.Workspace.Templates.ToDictionary(candidate => candidate.Id);
        var settings = _resolver.ResolveTemplate(template, templates);
        if (template.TargetKind == MonitoringTemplateScope.Sensor &&
            FindSensorDefinition(_workspaceStore.Workspace.SensorDefinitions, template.SensorTypeKey) is { } definition)
        {
            MonitoringSettings.ApplyCredentialValuesForKinds(settings, definition.CredentialKinds);
        }

        return settings;
    }

    private MonitoringSettings ResolveTemplateInheritedSettings(MonitoringTemplate template)
    {
        var templates = _workspaceStore.Workspace.Templates.ToDictionary(candidate => candidate.Id);
        var chain = ResolveTemplateChain(template.Id, templates).ToList();
        if (chain.Count <= 1)
        {
            return new MonitoringSettings();
        }

        var inherited = new MonitoringSettings();
        foreach (var ancestor in chain.Take(chain.Count - 1))
        {
            inherited.ApplyFrom(ancestor.Settings);
        }

        return inherited;
    }

    /// <summary>
    /// Applies a template to an element as a one-shot copy: the template's resolved settings are
    /// baked into the element and <see cref="MonitoringElement.TemplateOriginId"/> records the origin.
    /// When <paramref name="elementWins"/> is true (sensor creation) the element's own values win over
    /// the template; otherwise (re-apply / restore) the template overwrites its own fields.
    /// </summary>
    private void ApplyTemplateCopy(MonitoringElement element, MonitoringTemplate template, bool elementWins)
    {
        var templates = _workspaceStore.Workspace.Templates.ToDictionary(candidate => candidate.Id);
        var resolved = _resolver.ResolveTemplate(template, templates);

        if (elementWins)
        {
            resolved.ApplyFrom(element.Settings);
            element.Settings = resolved;
        }
        else
        {
            element.Settings.ApplyFrom(resolved);
        }

        // Tags from the template merge into the element's own tags (copy semantics).
        if (template.Tags.Count > 0)
        {
            element.Tags = MonitoringTagResolver.Normalize(element.Tags.Concat(template.Tags));
        }

        element.TemplateOriginId = template.Id;
        element.AppliedTemplateIds.Clear();
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

    private static List<SelectListItem> BuildNotificationTargetOptions(
        IReadOnlyList<WorkspaceNodeRow> nodes,
        Guid? selectedTargetId)
    {
        var options = new List<SelectListItem>
        {
            new("All workspace", string.Empty, selectedTargetId is null)
        };

        options.AddRange(nodes.Select(node => new SelectListItem(
            $"{node.Kind}: {node.Path}",
            node.Id.ToString(),
            node.Id == selectedTargetId)));

        return options;
    }

    private static List<SelectListItem> BuildNotificationStateOptions(IEnumerable<SensorState> selectedStates)
    {
        var selected = selectedStates.ToHashSet();
        return Enum.GetValues<SensorState>()
            .Where(state => state is not SensorState.Paused and not SensorState.Unknown)
            .Select(state => new SelectListItem(FormatSensorStateLabel(state), state.ToString(), selected.Contains(state)))
            .ToList();
    }

    private static List<SelectListItem> BuildNotificationSenderOptions(
        IReadOnlyList<NotificationSender> senders,
        Guid? selectedSenderId)
    {
        var options = new List<SelectListItem>
        {
            new("Select sender", string.Empty, selectedSenderId is null)
        };

        options.AddRange(senders.Select(sender => new SelectListItem(
            $"{sender.Name} ({sender.Kind})",
            sender.Id.ToString(),
            sender.Id == selectedSenderId)));

        return options;
    }

    private static List<SelectListItem> BuildNotificationReceiverOptions(
        IReadOnlyList<NotificationReceiver> receivers,
        Guid? selectedReceiverId)
    {
        var options = new List<SelectListItem>
        {
            new("Select receiver", string.Empty, selectedReceiverId is null)
        };

        options.AddRange(receivers.Select(receiver => new SelectListItem(
            $"{receiver.Name} ({receiver.Kind})",
            receiver.Id.ToString(),
            receiver.Id == selectedReceiverId)));

        return options;
    }

    private IReadOnlyList<WorkspaceNotificationRuleRow> BuildNotificationRuleRows(
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyList<WorkspaceNodeRow> nodes)
    {
        return snapshot.NotificationRules
            .Select(rule => new WorkspaceNotificationRuleRow(
                rule.Id,
                rule.Name,
                rule.Enabled,
                BuildNotificationSenderSummary(snapshot.NotificationSenders, rule.SenderId, rule.ChannelKind),
                BuildNotificationReceiverSummary(snapshot.NotificationReceivers, rule.ReceiverId, rule.ChannelKind, rule.Recipient),
                BuildNotificationTargetSummary(rule, nodes),
                BuildNotificationTriggerSummary(rule.TriggerStates),
                BuildNotificationCooldownSummary(rule.CooldownMinutes)))
            .ToArray();
    }

    private IReadOnlyList<WorkspaceNotificationSenderRow> BuildNotificationSenderRows(MonitoringWorkspaceSnapshot snapshot)
    {
        return snapshot.NotificationSenders
            .Select(sender => new WorkspaceNotificationSenderRow(
                sender.Id,
                sender.Name,
                sender.Enabled,
                sender.Kind.ToString(),
                BuildNotificationSenderSummary(snapshot.NotificationSenders, sender.Id, sender.Kind == NotificationEndpointKind.Webhook ? NotificationChannelKind.Webhook : NotificationChannelKind.Email)))
            .ToArray();
    }

    private IReadOnlyList<WorkspaceNotificationReceiverRow> BuildNotificationReceiverRows(MonitoringWorkspaceSnapshot snapshot)
    {
        return snapshot.NotificationReceivers
            .Select(receiver => new WorkspaceNotificationReceiverRow(
                receiver.Id,
                receiver.Name,
                receiver.Enabled,
                receiver.Kind.ToString(),
                receiver.Target,
                BuildNotificationReceiverSummary(snapshot.NotificationReceivers, receiver.Id, receiver.Kind == NotificationEndpointKind.Webhook ? NotificationChannelKind.Webhook : NotificationChannelKind.Email, receiver.Target)))
            .ToArray();
    }

    private IReadOnlyList<WorkspaceAlertRow> BuildAlertRows(MonitoringWorkspaceSnapshot snapshot)
    {
        return snapshot.Alerts
            .OrderByDescending(alert => alert.IsActive)
            .ThenByDescending(alert => alert.LastSeenUtc)
            .Select(alert => new WorkspaceAlertRow(
                alert.Id,
                alert.ElementId,
                alert.ElementKind,
                alert.ElementName,
                alert.ElementPath,
                alert.State,
                GetAlertStateKey(alert.State),
                FormatSensorStateLabel(alert.State),
                alert.Message,
                alert.FirstSeenUtc.ToLocalTime().ToString("g"),
                alert.LastSeenUtc.ToLocalTime().ToString("g"),
                alert.IsActive,
                alert.IsAcknowledged,
                alert.IsRecovered,
                alert.AcknowledgedUtc?.ToLocalTime().ToString("g"),
                alert.AcknowledgedBy,
                alert.RecoveredUtc?.ToLocalTime().ToString("g"),
                alert.ResolvedUtc?.ToLocalTime().ToString("g"),
                alert.FirstSeenUtc.ToUnixTimeMilliseconds(),
                alert.LastSeenUtc.ToUnixTimeMilliseconds()))
            .ToArray();
    }

    /// <summary>
    /// Builds the sensor-type dropdown options grouped into <c>&lt;optgroup&gt;</c>s by
    /// <see cref="SensorTypeCategories"/> (Windows, Linux, Databases, …), ordered by category
    /// then display name. One <see cref="SelectListGroup"/> instance per category so the
    /// select tag helper merges the groups.
    /// </summary>
    private static List<SelectListItem> BuildSensorTypeOptions(
        IEnumerable<SensorDefinition> definitions,
        string? selectedKey)
    {
        var groups = SensorTypeCategories.Order
            .ToDictionary(name => name, name => new SelectListGroup { Name = name }, StringComparer.OrdinalIgnoreCase);

        return definitions
            .Select(definition => (definition, category: SensorTypeCategories.Resolve(definition.Key)))
            .OrderBy(item => SensorTypeCategories.OrderIndex(item.category))
            .ThenBy(item => item.definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(item => new SelectListItem(
                item.definition.DisplayName,
                item.definition.Key,
                string.Equals(item.definition.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
            {
                Group = groups.TryGetValue(item.category, out var group) ? group : null
            })
            .ToList();
    }

    private static string BuildNotificationTargetSummary(NotificationRule rule, IReadOnlyList<WorkspaceNodeRow> nodes)
    {
        if (rule.TargetElementId is not Guid targetId)
        {
            return "all workspace";
        }

        var target = nodes.FirstOrDefault(node => node.Id == targetId);
        if (target is null)
        {
            return "missing target";
        }

        return rule.IncludeDescendants
            ? $"{target.Path} (subtree)"
            : target.Path;
    }

    private static string BuildNotificationSenderSummary(
        IReadOnlyList<NotificationSender> senders,
        Guid? senderId,
        NotificationChannelKind legacyKind)
    {
        var sender = senderId is Guid id
            ? senders.FirstOrDefault(candidate => candidate.Id == id)
            : null;

        if (sender is null)
        {
            return legacyKind.ToString().ToLowerInvariant();
        }

        return sender.Kind switch
        {
            NotificationEndpointKind.Webhook => $"{sender.Name} · webhook",
            _ => $"{sender.Name} · email"
        };
    }

    private static string BuildNotificationReceiverSummary(
        IReadOnlyList<NotificationReceiver> receivers,
        Guid? receiverId,
        NotificationChannelKind legacyKind,
        string legacyRecipient)
    {
        var receiver = receiverId is Guid id
            ? receivers.FirstOrDefault(candidate => candidate.Id == id)
            : null;

        if (receiver is null)
        {
            var fallback = string.IsNullOrWhiteSpace(legacyRecipient) ? "unset" : legacyRecipient;
            return $"{legacyKind.ToString().ToLowerInvariant()} · {fallback}";
        }

        return receiver.Kind switch
        {
            NotificationEndpointKind.Webhook => $"{receiver.Name} · webhook",
            _ => $"{receiver.Name} · email"
        };
    }

    private static string BuildNotificationTriggerSummary(IEnumerable<SensorState> triggerStates)
    {
        var labels = triggerStates
            .Distinct()
            .Select(FormatSensorStateLabel)
            .ToArray();

        return labels.Length == 0 ? "no states" : string.Join(", ", labels);
    }

    private static string BuildNotificationCooldownSummary(int? cooldownMinutes)
    {
        return cooldownMinutes is int cooldown && cooldown > 0
            ? $"cooldown {cooldown}m"
            : "no cooldown";
    }

    private (string Summary, string Subject, string Text, string Html) BuildNotificationRulePreview(
        string ruleName,
        string subjectTemplate,
        string textTemplate,
        string htmlTemplate,
        Guid? targetElementId,
        bool includeDescendants,
        IEnumerable<SensorState> triggerStates,
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyList<WorkspaceNodeRow> nodes,
        IReadOnlyDictionary<Guid, SensorObservation> latestSensorObservations,
        DateTimeOffset now)
    {
        var targetNode = targetElementId is Guid id
            ? nodes.FirstOrDefault(node => node.Id == id)
            : null;

        var alertCandidates = snapshot.Alerts
            .Where(alert => targetNode is null || IsAlertWithinRuleScope(alert.ElementPath, targetNode.Path, includeDescendants))
            .ToArray();

        var selectedAlert = alertCandidates
            .Where(alert => alert.IsActive)
            .OrderByDescending(alert => GetNotificationSeverityRank(alert.State))
            .ThenByDescending(alert => alert.LastSeenUtc)
            .FirstOrDefault()
            ?? alertCandidates.OrderByDescending(alert => alert.LastSeenUtc).FirstOrDefault();

        var selectedSensorNode = selectedAlert is not null
            ? nodes.FirstOrDefault(node => node.Id == selectedAlert.ElementId)
            : targetNode?.Kind == MonitoringElementKind.Sensor
                ? targetNode
                : null;

        if (selectedSensorNode is null && targetNode is not null && targetNode.Kind is MonitoringElementKind.Probe or MonitoringElementKind.Folder or MonitoringElementKind.Host)
        {
            selectedSensorNode = nodes.FirstOrDefault(node =>
                node.Kind == MonitoringElementKind.Sensor &&
                IsAlertWithinRuleScope(node.Path, targetNode.Path, includeDescendants));
        }

        if (selectedSensorNode is null)
        {
            selectedSensorNode = nodes.FirstOrDefault(node =>
                    node.Kind == MonitoringElementKind.Sensor && latestSensorObservations.ContainsKey(node.Id))
                ?? nodes.FirstOrDefault(node => node.Kind == MonitoringElementKind.Sensor);
        }

        latestSensorObservations.TryGetValue(selectedSensorNode?.Id ?? Guid.Empty, out var latestObservation);

        var templateContext = BuildNotificationTemplateContext(
            ruleName,
            targetNode,
            selectedSensorNode,
            selectedAlert,
            latestObservation,
            now);

        var summary = selectedAlert is null
            ? targetNode is null
                ? "Preview uses sample sensor data from the workspace."
                : $"Preview uses data from {targetNode.Path}."
            : $"Preview uses alert data from {selectedAlert.ElementPath}.";

        if (!triggerStates.Any())
        {
            summary += " No trigger states selected.";
        }

        return (
            summary,
            NotificationTemplateRenderer.RenderText(subjectTemplate, templateContext, NotificationTemplateCatalog.DefaultSubjectTemplate),
            NotificationTemplateRenderer.RenderText(textTemplate, templateContext, NotificationTemplateCatalog.DefaultTextTemplate),
            NotificationTemplateRenderer.RenderHtml(htmlTemplate, templateContext, NotificationTemplateCatalog.DefaultHtmlTemplate));
    }

    private static NotificationTemplateContext BuildNotificationTemplateContext(
        string ruleName,
        WorkspaceNodeRow? targetNode,
        WorkspaceNodeRow? sensorNode,
        MonitoringAlert? alert,
        SensorObservation? observation,
        DateTimeOffset now)
    {
        var context = new NotificationTemplateContext();
        var elementNode = sensorNode ?? targetNode;
        var defaultChannelKey = observation?.DefaultChannelKey;
        var defaultChannel = observation?.Channels.FirstOrDefault(channel =>
            channel.IsDefault ||
            (!string.IsNullOrWhiteSpace(defaultChannelKey) &&
             string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase)))
            ?? observation?.Channels.FirstOrDefault();
        var sensorMeasurementKind = defaultChannel?.MeasurementKind ?? SensorUnitConverter.GuessMeasurementKind(defaultChannel?.Unit);
        var sensorUnit = defaultChannel?.Unit ?? string.Empty;
        var sensorScale = SensorUnitConverter.CreateScale(GetScaleReferenceValue(observation, defaultChannelKey), sensorUnit, sensorMeasurementKind);
        var sensorValueDisplay = SensorUnitConverter.Format(observation?.DefaultValue, sensorScale, sensorMeasurementKind);
        var state = alert?.State ?? observation?.State ?? (elementNode?.IsPaused == true ? SensorState.Paused : SensorState.Unknown);
        var stateLabel = alert is not null
            ? FormatSensorStateLabel(alert.State)
            : observation is not null
                ? FormatSensorStateLabel(observation.State)
                : elementNode?.StateLabel ?? MonitoringStatePresentation.Label(state);
        var stateColor = alert is not null
            ? MonitoringStatePresentation.Color(alert.State)
            : observation is not null
                ? MonitoringStatePresentation.Color(observation.State)
                : MonitoringStatePresentation.Color(state);
        var stateKey = alert is not null
            ? GetAlertStateKey(alert.State)
            : observation is not null
                ? MonitoringStatePresentation.Key(observation.State)
                : elementNode?.StateKey ?? string.Empty;

        context.SetValue("rule.name", ruleName);
        context.SetValue("state.label", stateLabel);
        context.SetValue("state.key", stateKey);
        context.SetValue("state.color", stateColor);
        context.SetValue("message", alert?.Message ?? observation?.Message ?? elementNode?.StateMessage ?? string.Empty);
        context.SetValue("rendered_at", now);

        context.SetValue("element.name", elementNode?.Name ?? string.Empty);
        context.SetValue("element.path", elementNode?.Path ?? targetNode?.Path ?? string.Empty);
        context.SetValue("element.kind", elementNode?.Kind.ToString() ?? string.Empty);
        context.SetValue("element.details", elementNode?.Details ?? string.Empty);

        context.SetValue("sensor.name", sensorNode?.Name ?? string.Empty);
        context.SetValue("sensor.type", sensorNode?.SensorTypeKey ?? string.Empty);
        context.SetValue("sensor.target", sensorNode?.Target ?? string.Empty);
        context.SetValue("sensor.value", sensorValueDisplay.Text);
        context.SetValue("sensor.unit", sensorValueDisplay.Unit);
        context.SetValue("sensor.value_with_unit", sensorValueDisplay.CombinedText);
        context.SetValue("sensor.last_check", observation?.TimestampUtc);

        context.SetValue("alert.first_seen", alert?.FirstSeenUtc);
        context.SetValue("alert.last_seen", alert?.LastSeenUtc);
        context.SetValue("alert.acknowledged_at", alert?.AcknowledgedUtc);
        context.SetValue("alert.acknowledged_by", alert?.AcknowledgedBy ?? string.Empty);
        context.SetValue("alert.resolved_at", alert?.ResolvedUtc);
        context.SetValue("problem.since", alert?.FirstSeenUtc ?? observation?.TimestampUtc);
        context.SetValue("problem.age", alert is not null ? now - alert.FirstSeenUtc : observation is not null ? now - observation.TimestampUtc : null);

        context.SetValue("probe.name", observation?.ExecutedByProbeName ?? DeriveProbeName(sensorNode, targetNode));
        context.SetValue("probe.id", observation?.ExecutedByProbeId ?? string.Empty);
        context.SetValue("probe.last_seen", observation?.TimestampUtc);

        context.SetValue("channels.summary", BuildChannelsSummary(observation));
        context.SetRawHtml("state.badge_html", BuildStateBadgeHtml(stateLabel, stateColor));
        context.SetRawHtml("channels.table_html", BuildChannelsTableHtml(observation, state));

        return context;
    }

    private static string BuildChannelsSummary(SensorObservation? observation)
    {
        if (observation is null || observation.Channels.Count == 0)
        {
            return string.Empty;
        }

        var channels = observation.Channels.Where(channel => !channel.IsVirtual).ToArray();
        if (channels.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(" · ", channels.Select(channel =>
        {
            var display = SensorUnitConverter.Format(channel.Value, channel.Unit, channel.MeasurementKind);
            return string.IsNullOrWhiteSpace(channel.Label)
                ? $"{channel.Key}: {display.CombinedText}"
                : $"{channel.Label}: {display.CombinedText}";
        }));
    }

    private static string BuildStateBadgeHtml(string stateLabel, string stateColor)
    {
        var background = string.IsNullOrWhiteSpace(stateColor) ? "#4567d2" : stateColor.Trim();
        var foreground = IsLightHexColor(background) ? "#16202c" : "#ffffff";

        return $"<span style=\"display:inline-flex;align-items:center;gap:0.4rem;border-radius:999px;padding:0.35rem 0.75rem;font-size:0.78rem;font-weight:700;color:{foreground};background:{background};\">{WebUtility.HtmlEncode(stateLabel)}</span>";
    }

    private static string BuildChannelsTableHtml(SensorObservation? observation, SensorState state)
    {
        if (observation is null || observation.Channels.Count == 0)
        {
            return string.Empty;
        }

        var channels = observation.Channels.Where(channel => !channel.IsVirtual).ToArray();
        if (channels.Length == 0)
        {
            return string.Empty;
        }

        var rows = channels.Select(channel =>
        {
            var label = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(channel.Label) ? channel.Key : channel.Label);
            var display = SensorUnitConverter.Format(channel.Value, channel.Unit, channel.MeasurementKind);
            var value = WebUtility.HtmlEncode(display.Text);
            var unit = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(display.Unit) ? string.Empty : $" {display.Unit}");
            var channelState = channel.State ?? state;
            var badge = WebUtility.HtmlEncode(FormatSensorStateLabel(channelState));
            var rowStyle = channel.IsDefault ? "font-weight:600;" : string.Empty;
            return $"<tr style=\"{rowStyle}\"><td style=\"padding:0.35rem 0;border-bottom:1px solid rgba(148,163,184,0.18);\">{label}</td><td style=\"padding:0.35rem 0;border-bottom:1px solid rgba(148,163,184,0.18);text-align:right;\">{value}{unit}</td><td style=\"padding:0.35rem 0;border-bottom:1px solid rgba(148,163,184,0.18);text-align:right;\">{badge}</td></tr>";
        });

        return $"""
<table style="width:100%;border-collapse:collapse;font-size:0.9rem;">
  <thead>
    <tr>
      <th style="text-align:left;padding:0 0 0.45rem 0;color:#6b7280;font-weight:600;">Channel</th>
      <th style="text-align:right;padding:0 0 0.45rem 0;color:#6b7280;font-weight:600;">Value</th>
      <th style="text-align:right;padding:0 0 0.45rem 0;color:#6b7280;font-weight:600;">State</th>
    </tr>
  </thead>
  <tbody>
    {string.Join(Environment.NewLine, rows)}
  </tbody>
</table>
""";
    }

    private static double? GetScaleReferenceValue(SensorObservation? observation, string? defaultChannelKey)
    {
        if (observation is null)
        {
            return null;
        }

        var values = new List<double>();

        var defaultValue = SensorHistoryAnalytics.GetDefaultValue(observation, defaultChannelKey);
        if (defaultValue.HasValue)
        {
            values.Add(Math.Abs(defaultValue.Value));
        }

        values.AddRange(observation.Channels
            .Where(channel => !channel.IsVirtual && channel.Value.HasValue)
            .Select(channel => Math.Abs(channel.Value!.Value)));

        return values.Count == 0 ? null : values.Max();
    }

    private static string DeriveProbeName(WorkspaceNodeRow? sensorNode, WorkspaceNodeRow? targetNode)
    {
        var source = sensorNode ?? targetNode;
        if (source is null || string.IsNullOrWhiteSpace(source.Path))
        {
            return string.Empty;
        }

        return source.Path.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
    }

    private static DateTimeOffset? ParseLastCheck(string? lastCheck)
    {
        if (string.IsNullOrWhiteSpace(lastCheck))
        {
            return null;
        }

        return DateTimeOffset.TryParse(lastCheck, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsAlertWithinRuleScope(string alertPath, string targetPath, bool includeDescendants)
    {
        if (string.IsNullOrWhiteSpace(alertPath) || string.IsNullOrWhiteSpace(targetPath))
        {
            return true;
        }

        if (string.Equals(alertPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return includeDescendants && alertPath.StartsWith(targetPath + " /", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLightHexColor(string color)
    {
        if (string.IsNullOrWhiteSpace(color) || !color.StartsWith('#'))
        {
            return false;
        }

        var hex = color.Trim().TrimStart('#');
        if (hex.Length != 6 && hex.Length != 8)
        {
            return false;
        }

        try
        {
            var r = Convert.ToInt32(hex[..2], 16);
            var g = Convert.ToInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToInt32(hex.Substring(4, 2), 16);
            var brightness = ((r * 299) + (g * 587) + (b * 114)) / 1000;
            return brightness >= 180;
        }
        catch
        {
            return false;
        }
    }

    private static int GetNotificationSeverityRank(SensorState state)
    {
        return state switch
        {
            SensorState.Critical => 3,
            SensorState.Warning => 2,
            SensorState.Healthy => 1,
            _ => 0
        };
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

    private static ScheduleEditorState BuildScheduleEditorState(MonitoringSettings localSettings, MonitoringSettings effectiveSettings)
    {
        var (mode, everyValue, everyUnit, dayOfWeek, dayOfMonth, time) =
            ReadScheduleInput(localSettings.PollingSchedule, localSettings.PollingInterval);

        var daysOfWeek = localSettings.PollingSchedule is { Mode: MonitoringScheduleMode.Weekly } weekly
            ? weekly.ResolveDays().ToList()
            : new List<DayOfWeek>();

        return new ScheduleEditorState(
            mode,
            everyValue,
            everyUnit,
            dayOfWeek,
            daysOfWeek,
            dayOfMonth,
            time,
            FormatScheduleSummary(effectiveSettings));
    }

    private static (string Mode, int? EveryValue, string EveryUnit, DayOfWeek? DayOfWeek, int? DayOfMonth, string? Time)
        ReadScheduleInput(MonitoringSchedule? schedule, TimeSpan? legacyInterval)
    {
        if (schedule is not null)
        {
            return schedule.Mode switch
            {
                MonitoringScheduleMode.Daily => (
                    "daily",
                    null,
                    "minutes",
                    DayOfWeek.Monday,
                    1,
                    FormatScheduleTime(schedule.TimeOfDay)),
                MonitoringScheduleMode.Weekly => (
                    "weekly",
                    null,
                    "minutes",
                    schedule.DayOfWeek ?? DayOfWeek.Monday,
                    1,
                    FormatScheduleTime(schedule.TimeOfDay)),
                MonitoringScheduleMode.Monthly => (
                    "monthly",
                    null,
                    "minutes",
                    DayOfWeek.Monday,
                    Math.Clamp(schedule.DayOfMonth ?? 1, 1, 31),
                    FormatScheduleTime(schedule.TimeOfDay)),
                _ => BuildEveryScheduleInput(schedule.EverySeconds ?? 300)
            };
        }

        if (legacyInterval is TimeSpan interval)
        {
            return BuildEveryScheduleInput((int)Math.Round(interval.TotalSeconds));
        }

        return ("inherit", null, "minutes", DayOfWeek.Monday, 1, "00:00");
    }

    private static (string Mode, int? EveryValue, string EveryUnit, DayOfWeek? DayOfWeek, int? DayOfMonth, string? Time)
        BuildEveryScheduleInput(int seconds)
    {
        var safeSeconds = Math.Max(seconds, 1);

        // Express the interval in the largest whole unit so the editor shows a clean value.
        if (safeSeconds % 86400 == 0)
        {
            return ("every", safeSeconds / 86400, "days", DayOfWeek.Monday, 1, "00:00");
        }

        if (safeSeconds % 3600 == 0)
        {
            return ("every", safeSeconds / 3600, "hours", DayOfWeek.Monday, 1, "00:00");
        }

        if (safeSeconds % 60 == 0)
        {
            return ("every", safeSeconds / 60, "minutes", DayOfWeek.Monday, 1, "00:00");
        }

        return ("every", safeSeconds, "seconds", DayOfWeek.Monday, 1, "00:00");
    }

    private static string FormatScheduleSummary(MonitoringSettings settings)
    {
        if (settings.PollingSchedule is not null)
        {
            return settings.PollingSchedule.Summary();
        }

        if (settings.PollingInterval is TimeSpan interval)
        {
            return $"every {MonitoringSchedule.FormatDuration(interval)}";
        }

        return "every 15s";
    }

    private static string FormatScheduleTime(TimeSpan? time)
    {
        return (time ?? TimeSpan.Zero).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
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

    private static string? BuildTelemetryProfileSummary(Matmon.Core.Telemetry.SensorTelemetryProfile? profile)
    {
        if (profile is null)
        {
            return null;
        }

        return $"Type defaults ({profile.Name}): keep raw checks {profile.RawObservationDays} d, " +
               $"summarise {DescribeBucketGranularity(profile.StatisticsBucketMinutes)}, " +
               $"keep summaries {profile.StatisticsRetentionDays} d, event log {profile.EventRetentionDays} d.";
    }

    public static string DescribeBucketGranularity(int minutes) => minutes switch
    {
        <= 0 => "no",
        60 => "hourly",
        360 => "every 6 h",
        720 => "every 12 h",
        1440 => "daily",
        < 60 => $"every {minutes} min",
        _ when minutes % 1440 == 0 => $"every {minutes / 1440} d",
        _ when minutes % 60 == 0 => $"every {minutes / 60} h",
        _ => $"every {minutes} min"
    };

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

    private static string GetAlertStateKey(SensorState state)
    {
        return state switch
        {
            SensorState.Warning => "warning",
            SensorState.Critical => "error",
            SensorState.Paused => MonitoringStatePresentation.PausedKey,
            SensorState.Disabled => "disabled",
            SensorState.Healthy => "ok",
            SensorState.Unknown => MonitoringStatePresentation.UnknownKey,
            _ => "error"
        };
    }

    private List<SelectListItem> BuildElementParentOptions(IReadOnlyList<WorkspaceNodeRow> nodes, MonitoringElement? selectedElement)
    {
        var excluded = selectedElement is null
            ? new HashSet<Guid>()
            : GetDescendantIds(selectedElement).Append(selectedElement.Id).ToHashSet();
        var allowedKinds = selectedElement switch
        {
            ProbeElement => new[] { MonitoringElementKind.Probe },
            FolderElement => new[] { MonitoringElementKind.Probe, MonitoringElementKind.Folder },
            HostElement => new[] { MonitoringElementKind.Probe, MonitoringElementKind.Folder },
            SensorElement => new[] { MonitoringElementKind.Probe, MonitoringElementKind.Folder, MonitoringElementKind.Host },
            _ => Array.Empty<MonitoringElementKind>()
        };

        if (selectedElement is ProbeElement { ParentId: null })
        {
            return new List<SelectListItem>();
        }

        return nodes
            .Where(node => allowedKinds.Length == 0 || allowedKinds.Contains(node.Kind))
            .Where(node => !excluded.Contains(node.Id))
            .Select(node => new SelectListItem($"{node.Kind}: {node.Path}", node.Id.ToString(), node.Id == selectedElement?.ParentId))
            .ToList();
    }

    private static IEnumerable<Guid> GetDescendantIds(MonitoringElement element)
    {
        if (element is not MonitoringContainerElement container)
        {
            yield break;
        }

        foreach (var child in container.Children)
        {
            yield return child.Id;

            foreach (var descendant in GetDescendantIds(child))
            {
                yield return descendant;
            }
        }
    }

    private static List<SelectListItem> BuildTemplateParentOptions(
        IReadOnlyList<MonitoringTemplate> templates,
        MonitoringTemplate? selectedTemplate)
    {
        var excluded = selectedTemplate is null
            ? new HashSet<Guid>()
            : GetTemplateDescendantIds(selectedTemplate.Id, templates).Append(selectedTemplate.Id).ToHashSet();

        return templates
            .Where(template => !excluded.Contains(template.Id))
            .Select(template => new SelectListItem($"{template.Name} ({template.TargetKind})", template.Id.ToString(), template.Id == selectedTemplate?.ParentTemplateId))
            .ToList();
    }

    private static List<SelectListItem> BuildTemplateApplyTargetOptions(
        IReadOnlyList<WorkspaceNodeRow> nodes,
        MonitoringTemplate? template,
        Guid? selectedTargetId)
    {
        if (template is null)
        {
            return [];
        }

        return nodes
            .Where(node => IsTemplateApplicableToElement(template.TargetKind, node.Kind))
            .Select(node => new SelectListItem($"{node.Kind}: {node.Path}", node.Id.ToString(), node.Id == selectedTargetId))
            .ToList();
    }

    private static bool IsTemplateApplicableToElement(MonitoringTemplateScope templateScope, MonitoringElementKind elementKind)
    {
        return templateScope == MonitoringTemplateScope.Any || string.Equals(templateScope.ToString(), elementKind.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Guid> GetTemplateDescendantIds(Guid templateId, IReadOnlyList<MonitoringTemplate> templates)
    {
        foreach (var child in templates.Where(template => template.ParentTemplateId == templateId))
        {
            yield return child.Id;

            foreach (var descendant in GetTemplateDescendantIds(child.Id, templates))
            {
                yield return descendant;
            }
        }
    }

    private IReadOnlyList<WorkspaceNodeRow> BuildNodeRows(
        MonitoringElement root,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templateMap,
        IReadOnlyDictionary<Guid, TelemetrySeriesSnapshot> telemetrySeriesMap,
        IReadOnlySet<Guid> acknowledgedElementIds)
    {
        var rows = new List<WorkspaceNodeRow>();
        BuildNodeRows(root, rows, templateMap, telemetrySeriesMap, acknowledgedElementIds, depth: 0, parentPath: string.Empty, inheritedTags: []);
        return rows;
    }

    private void BuildNodeRows(
        MonitoringElement element,
        List<WorkspaceNodeRow> rows,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templateMap,
        IReadOnlyDictionary<Guid, TelemetrySeriesSnapshot> telemetrySeriesMap,
        IReadOnlySet<Guid> acknowledgedElementIds,
        int depth,
        string parentPath,
        IReadOnlyList<string> inheritedTags)
    {
        var path = string.IsNullOrWhiteSpace(parentPath) ? element.Name : $"{parentPath} / {element.Name}";
        var ownTags = MonitoringTagResolver.Normalize(element.Tags);
        var effectiveTags = MonitoringTagResolver.Normalize(inheritedTags.Concat(element.Tags));
        var effectiveSettings = ResolveElementEffectiveSettings(element);
        var settingsSummary = effectiveSettings.Summary();
        var templateSummary = BuildTemplateSummary(element, templateMap);
        var details = element switch
        {
            ProbeElement probe when !string.IsNullOrWhiteSpace(probe.Description) => probe.Description!,
            FolderElement folder when !string.IsNullOrWhiteSpace(folder.Description) => folder.Description!,
            HostElement host when !string.IsNullOrWhiteSpace(host.Address) => host.Address,
            SensorElement sensorElement => $"{sensorElement.SensorTypeKey} -> {FormatSensorTarget(sensorElement)}",
            _ => string.Empty
        };
        var isPausedSensor = element is SensorElement pausedSensor && pausedSensor.IsPaused;
        var isHighlightedSensor = element.Kind == MonitoringElementKind.Sensor && effectiveSettings.Highlight == true;
        var stateKey = isPausedSensor ? MonitoringStatePresentation.PausedKey : string.Empty;
        var stateLabel = isPausedSensor ? MonitoringStatePresentation.PausedLabel : null;
        var stateMessage = isPausedSensor ? "polling paused" : null;

        rows.Add(new WorkspaceNodeRow(
            element.Id,
            element.Kind,
            GetKindIconKey(element.Kind),
            element.Name,
            depth,
            element.ParentId,
            path,
            details,
            settingsSummary,
            templateSummary,
            (element as ProbeElement)?.ProbeId,
            (element as ProbeElement)?.EnrollmentToken,
            (element as HostElement)?.Address,
            (element as SensorElement)?.SensorTypeKey,
            element is SensorElement rowSensor ? ResolveEffectiveSensorTarget(rowSensor) : null,
            isHighlightedSensor,
            isPausedSensor,
            stateKey,
            stateLabel,
            stateMessage,
            effectiveTags,
            ownTags,
            acknowledgedElementIds.Contains(element.Id)));

        if (element is MonitoringContainerElement container)
        {
            foreach (var child in container.Children)
            {
                BuildNodeRows(child, rows, templateMap, telemetrySeriesMap, acknowledgedElementIds, depth + 1, path, effectiveTags);
            }
        }
    }

    private string FormatSensorTarget(SensorElement sensor)
    {
        var target = ResolveEffectiveSensorTarget(sensor);
        if (string.IsNullOrWhiteSpace(target))
        {
            return "(no target)";
        }

        return string.IsNullOrWhiteSpace(sensor.Target) ? $"{target} (inherited)" : target;
    }

    private static string GetKindIconKey(MonitoringElementKind kind)
    {
        return kind switch
        {
            MonitoringElementKind.Probe => "probe",
            MonitoringElementKind.Folder => "folder",
            MonitoringElementKind.Host => "host",
            MonitoringElementKind.Sensor => "sensor",
            _ => "square"
        };
    }

    private IReadOnlyList<WorkspaceProbeRow> BuildProbeRows(
        IReadOnlyList<WorkspaceNodeRow> nodes,
        IReadOnlyDictionary<string, ProbeStatusSnapshot> probeStatuses,
        DateTimeOffset now)
    {
        var heartbeatWindowSeconds = Math.Clamp(_runtimeOptions.HeartbeatIntervalSeconds, 5, 300);
        var probeStack = new Stack<(int Depth, MonitoringSeverity Severity)>();
        var rows = new List<WorkspaceProbeRow>();

        foreach (var node in nodes)
        {
            while (probeStack.Count > 0 && probeStack.Peek().Depth >= node.Depth)
            {
                probeStack.Pop();
            }

            if (node.Kind != MonitoringElementKind.Probe)
            {
                continue;
            }

            var enrollmentToken = node.EnrollmentToken;
            var probeStatus = !string.IsNullOrWhiteSpace(node.ProbeId) && probeStatuses.TryGetValue(node.ProbeId!, out var status)
                ? status
                : null;
            var inheritedSeverity = probeStack.Count > 0 ? probeStack.Peek().Severity : MonitoringSeverity.Ok;
            var ownSeverity = node.Depth == 0
                ? MonitoringSeverity.Ok
                : probeStatus is null
                    ? MonitoringSeverity.Error
                    : MonitoringStatePresentation.FromHeartbeatAge(
                        Math.Max((now - probeStatus.LastSeenUtc).TotalSeconds, 0),
                        heartbeatWindowSeconds);
            var severity = MonitoringStatePresentation.Max(inheritedSeverity, ownSeverity);

            rows.Add(new WorkspaceProbeRow(
                node.Id,
                node.Name,
                node.ProbeId ?? "-",
                enrollmentToken ?? "-",
                MonitoringStatePresentation.Label(severity),
                probeStatus?.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss") ?? (node.Depth == 0 ? "local" : "-"),
                probeStatus is null
                    ? (node.Depth == 0 ? "local primary" : "no heartbeat")
                    : BuildProbeStatusMessage(severity, now - probeStatus.LastSeenUtc),
                BuildProbeBootstrapSnippet(node.ProbeId, node.Name, enrollmentToken)));

            probeStack.Push((node.Depth, severity));
        }

        return rows;
    }

    private static string NormalizeMonitoringViewMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "list" => "list",
            _ => "tree"
        };
    }

    private static string NormalizeMonitoringSize(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "s" => "s",
            "l" => "l",
            _ => "m"
        };
    }

    private static string NormalizeMonitoringKindFilter(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "probe" => "probe",
            "folder" => "folder",
            "host" => "host",
            "sensor" => "sensor",
            _ => "all"
        };
    }

    private static string NormalizeMonitoringStateFilter(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "ok" => "ok",
            "warning" => "warning",
            "error" => "error",
            "paused" => "paused",
            "unknown" => "unknown",
            "disabled" => "disabled",
            _ => "all"
        };
    }

    private static string NormalizeMonitoringSearch(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static Func<WorkspaceNodeRow, bool> BuildMonitoringFilterPredicate(
        string kindFilter,
        string stateFilter,
        string tagFilter,
        string searchText)
    {
        return node =>
        {
            if (!string.Equals(kindFilter, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(node.Kind.ToString(), kindFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(stateFilter, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(node.StateKey, stateFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Tag filter matches effective tags (node.Tags = own + inherited), so a sensor under a
            // tagged folder/host matches too.
            if (!string.IsNullOrWhiteSpace(tagFilter) &&
                !node.Tags.Any(tag => string.Equals(tag, tagFilter, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(searchText))
            {
                return true;
            }

            return ContainsText(node.Name, searchText)
                || ContainsText(node.Path, searchText)
                || ContainsText(node.Details, searchText)
                || ContainsText(node.SettingsSummary, searchText)
                || ContainsText(node.TemplateSummary, searchText)
                || ContainsText(node.StateLabel, searchText)
                || ContainsText(node.StateMessage, searchText)
                || node.Tags.Any(tag => ContainsText(tag, searchText));
        };
    }

    private static bool ContainsText(string? value, string searchText)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<WorkspaceNodeRow> FilterTreeNodes(
        IReadOnlyList<WorkspaceNodeRow> nodes,
        Func<WorkspaceNodeRow, bool> predicate)
    {
        var index = 0;
        return FilterNodeLevel(0);

        IReadOnlyList<WorkspaceNodeRow> FilterNodeLevel(int depth)
        {
            var results = new List<WorkspaceNodeRow>();

            while (index < nodes.Count)
            {
                var node = nodes[index];
                if (node.Depth < depth)
                {
                    break;
                }

                if (node.Depth > depth)
                {
                    break;
                }

                index++;
                var children = FilterNodeLevel(depth + 1);
                if (predicate(node) || children.Count > 0)
                {
                    results.Add(node);
                    results.AddRange(children);
                }
            }

            return results;
        }
    }

    private IReadOnlyList<WorkspaceMonitoringTreeNode> BuildMonitoringTreeNodes(
        IReadOnlyList<WorkspaceNodeRow> nodes,
        IReadOnlyDictionary<Guid, DashboardNodeViewModel> liveNodeMap,
        IReadOnlyDictionary<Guid, TelemetrySeriesSnapshot> telemetrySeriesMap,
        IReadOnlyDictionary<Guid, SensorObservation> latestSensorObservations)
    {
        var builders = new Dictionary<Guid, MonitoringTreeNodeBuilder>();
        var roots = new List<MonitoringTreeNodeBuilder>();

        foreach (var node in nodes)
        {
            var liveNode = liveNodeMap.TryGetValue(node.Id, out var dashboardNode)
                ? dashboardNode
                : null;
            var series = node.Kind == MonitoringElementKind.Sensor && telemetrySeriesMap.TryGetValue(node.Id, out var telemetrySeries)
                ? telemetrySeries
                : null;
            var latestObservation = latestSensorObservations.TryGetValue(node.Id, out var observation)
                ? observation
                : null;

            var builder = new MonitoringTreeNodeBuilder(node, liveNode, series, latestObservation);
            builders[node.Id] = builder;

            if (node.ParentId is Guid parentId && builders.TryGetValue(parentId, out var parent))
            {
                parent.Children.Add(builder);

                if (node.Kind != MonitoringElementKind.Probe)
                {
                    parent.DisplayChildren.Add(builder);
                }
            }

            if (node.Kind == MonitoringElementKind.Probe || node.ParentId is null)
            {
                roots.Add(builder);
            }
        }

        foreach (var root in roots)
        {
            root.InitializeAggregateState();
        }

        return roots.Select(root => root.ToViewModel()).ToArray();
    }

    private sealed class MonitoringTreeNodeBuilder
    {
        private readonly WorkspaceNodeRow _node;
        private readonly DashboardNodeViewModel? _liveNode;
        private readonly TelemetrySeriesSnapshot? _series;
        private readonly SensorObservation? _latestObservation;

        public MonitoringTreeNodeBuilder(WorkspaceNodeRow node, DashboardNodeViewModel? liveNode, TelemetrySeriesSnapshot? series, SensorObservation? latestObservation)
        {
            _node = node;
            _liveNode = liveNode;
            _series = series;
            _latestObservation = latestObservation;
        }

        public List<MonitoringTreeNodeBuilder> Children { get; } = [];

        public List<MonitoringTreeNodeBuilder> DisplayChildren { get; } = [];

        public int SensorCount { get; private set; }

        public int WarningCount { get; private set; }

        public int ErrorCount { get; private set; }

        private string StateKey => _liveNode?.StateKey
            ?? _node.StateKey
            ?? _series?.StateKey
            ?? string.Empty;

        private string StateLabel => _liveNode?.StateLabel
            ?? _node.StateLabel
            ?? _series?.StateLabel
            ?? string.Empty;

        private string StateColor => _liveNode?.StateColor
            ?? _series?.StateColor
            ?? string.Empty;

        private string? StateMessage => _liveNode?.StateMessage ?? _node.StateMessage;

        private double? CurrentValue => _series?.CurrentValue ?? _latestObservation?.Value;

        private string? Unit => _series?.Unit;

        private string? LastCheck => _latestObservation?.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

        public void InitializeAggregateState()
        {
            foreach (var child in Children)
            {
                child.InitializeAggregateState();
            }

            var selfSensorCount = _node.Kind == MonitoringElementKind.Sensor ? 1 : 0;
            var selfWarningCount = _node.Kind == MonitoringElementKind.Sensor && string.Equals(StateKey, "warning", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var selfErrorCount = _node.Kind == MonitoringElementKind.Sensor && string.Equals(StateKey, "error", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            SensorCount = selfSensorCount + Children.Sum(child => child.SensorCount);
            WarningCount = selfWarningCount + Children.Sum(child => child.WarningCount);
            ErrorCount = selfErrorCount + Children.Sum(child => child.ErrorCount);
        }

        public WorkspaceMonitoringTreeNode ToViewModel()
        {
            return new WorkspaceMonitoringTreeNode
            {
                Id = _node.Id,
                Kind = _node.Kind,
                KindIconKey = _node.KindIconKey,
                Name = _node.Name,
                Depth = _node.Depth,
                Path = _node.Path,
                Details = _node.Details,
                SettingsSummary = _node.SettingsSummary,
                TemplateSummary = _node.TemplateSummary,
                ProbeId = _node.ProbeId,
                EnrollmentToken = _node.EnrollmentToken,
                Address = _node.Address,
                SensorTypeKey = _node.SensorTypeKey,
                Target = _node.Target,
                IsHighlighted = _node.IsHighlighted || _series?.IsHighlighted == true,
                IsPaused = _node.IsPaused,
                IsAcknowledged = _node.IsAcknowledged,
                StateKey = StateKey,
                StateLabel = StateLabel,
                StateColor = StateColor,
                StateMessage = StateMessage,
                CurrentValue = CurrentValue,
                Unit = Unit,
                LastCheck = LastCheck,
                SensorCount = SensorCount,
                WarningCount = WarningCount,
                ErrorCount = ErrorCount,
                ChildCount = DisplayChildren.Count,
                SeriesKey = _series?.Key,
                SeriesLineColor = _series?.LineColor,
                SeriesPointCount = _series?.Points.Count ?? 0,
                SensorTypeLabel = _series?.SensorTypeLabel,
                Tags = _node.OwnTags,
                Children = DisplayChildren.Select(child => child.ToViewModel()).ToArray()
            };
        }
    }

    private static string BuildMonitoringFilterSummary(
        string kindFilter,
        string stateFilter,
        string searchText,
        int visibleCount,
        int totalCount)
    {
        var parts = new List<string>();

        if (!string.Equals(kindFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(kindFilter);
        }

        if (!string.Equals(stateFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(stateFilter);
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            parts.Add($"\"{searchText}\"");
        }

        var filterText = parts.Count == 0 ? "all" : string.Join(" / ", parts);
        return $"{visibleCount}/{totalCount} / {filterText}";
    }

    private static string BuildProbeStatusMessage(MonitoringSeverity severity, TimeSpan age)
    {
        return severity switch
        {
            MonitoringSeverity.Ok => $"heartbeat {age.TotalSeconds:0.#}s ago",
            MonitoringSeverity.Warning => $"heartbeat delayed {age.TotalSeconds:0.#}s",
            MonitoringSeverity.Error => "heartbeat missing",
            _ => "heartbeat missing"
        };
    }

    private IReadOnlyList<WorkspaceTemplateRow> BuildTemplateRows(MonitoringWorkspaceSnapshot snapshot)
    {
        var parentMap = snapshot.Templates.ToDictionary(template => template.Id);
        var sensorDefinitionMap = snapshot.SensorDefinitions.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);

        return snapshot.Templates
            .Select(template =>
            {
                var impactRows = BuildTemplateImpactRows(snapshot.RootProbe, template, parentMap);
                var directImpactCount = impactRows.Count(row => row.ImpactKind is "direct" or "template");
                var inheritedImpactCount = impactRows.Count(row => row.ImpactKind == "inherited");
                var sensorTypeKey = template.TargetKind == MonitoringTemplateScope.Sensor ? template.SensorTypeKey : null;
                var sensorTypeLabel = !string.IsNullOrWhiteSpace(sensorTypeKey) &&
                    sensorDefinitionMap.TryGetValue(sensorTypeKey, out var sensorDefinition)
                        ? sensorDefinition.DisplayName
                        : sensorTypeKey;

                return new WorkspaceTemplateRow(
                    template.Id,
                    template.Name,
                    template.TargetKind.ToString(),
                    template.TargetKind.ToString().ToLowerInvariant(),
                    template.Settings.Summary(),
                    ResolveTemplateParentName(template, parentMap),
                    sensorTypeKey,
                    sensorTypeLabel,
                    template.TargetKind == MonitoringTemplateScope.Sensor,
                    impactRows.Count,
                    directImpactCount,
                    inheritedImpactCount,
                    template.Settings.Parameters.Count,
                    template.Settings.Thresholds.Count,
                    template.Settings.Credentials.Count);
            })
            .ToArray();
    }

    private static string? ResolveTemplateParentName(MonitoringTemplate template, IReadOnlyDictionary<Guid, MonitoringTemplate> templateMap)
    {
        return template.ParentTemplateId is Guid parentId && templateMap.TryGetValue(parentId, out var parent)
            ? parent.Name
            : null;
    }

    private static string BuildTemplateSummary(MonitoringElement element, IReadOnlyDictionary<Guid, MonitoringTemplate> templateMap)
    {
        if (element.TemplateOriginId is Guid originId && templateMap.TryGetValue(originId, out var origin))
        {
            return $"from {origin.Name}";
        }

        return "no template";
    }

    private static IReadOnlyList<TemplateImpactRow> BuildTemplateImpactRows(
        MonitoringElement root,
        MonitoringTemplate template,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templateMap)
    {
        var rows = new Dictionary<Guid, TemplateImpactRow>();
        Traverse(root, string.Empty, inheritedSource: null);
        return rows.Values
            .OrderBy(row => row.SensorPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        void Traverse(MonitoringElement element, string parentPath, TemplateImpactSource? inheritedSource)
        {
            var path = string.IsNullOrWhiteSpace(parentPath)
                ? element.Name
                : $"{parentPath} / {element.Name}";
            // Copy model: a sensor is impacted only by the template it was created from / last
            // restored from (its origin), directly or via that origin's parent chain. No propagation
            // to children (templates are no longer live-inherited down the tree).
            _ = inheritedSource;
            var originId = (element as SensorElement)?.TemplateOriginId;
            var matchesDirectly = originId == template.Id;
            var matchesThroughTemplateChain = !matchesDirectly &&
                originId is Guid chainOrigin &&
                TemplateChainContains([chainOrigin], template.Id, templateMap);
            var source = matchesDirectly
                ? new TemplateImpactSource(element.Id, element.Kind, element.Name, path, "direct")
                : matchesThroughTemplateChain
                    ? new TemplateImpactSource(element.Id, element.Kind, element.Name, path, "template")
                    : null;

            if (element is SensorElement sensor && source is not null)
            {
                rows[sensor.Id] = new TemplateImpactRow(
                    sensor.Id,
                    sensor.Name,
                    path,
                    sensor.SensorTypeKey,
                    source.ElementKind,
                    source.ElementName,
                    source.ElementPath,
                    source.ElementId == sensor.Id ? source.ImpactKind : "inherited");
            }

            if (element is MonitoringContainerElement container)
            {
                foreach (var child in container.Children)
                {
                    Traverse(child, path, source);
                }
            }
        }
    }

    private static bool TemplateChainContains(
        IEnumerable<Guid> appliedTemplateIds,
        Guid templateId,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templateMap)
    {
        return appliedTemplateIds.Any(appliedTemplateId =>
            ResolveTemplateChain(appliedTemplateId, templateMap).Any(template => template.Id == templateId));
    }

    private static IEnumerable<MonitoringTemplate> ResolveTemplateChain(Guid templateId, IReadOnlyDictionary<Guid, MonitoringTemplate> templateMap)
    {
        var stack = new Stack<MonitoringTemplate>();
        var visited = new HashSet<Guid>();
        var currentId = templateId;

        while (templateMap.TryGetValue(currentId, out var current) && visited.Add(currentId))
        {
            stack.Push(current);
            if (current.ParentTemplateId is not Guid parentId)
            {
                break;
            }

            currentId = parentId;
        }

        while (stack.Count > 0)
        {
            yield return stack.Pop();
        }
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

    public IReadOnlyList<WorkspaceNodeRow> GetDirectSensorChildren(IReadOnlyList<WorkspaceNodeRow> nodes, int parentIndex)
    {
        if (parentIndex < 0 || parentIndex >= nodes.Count)
        {
            return Array.Empty<WorkspaceNodeRow>();
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
    string Cooldown);

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
    string Summary);

public sealed class CreateProbeInput
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? ParentId { get; set; }
}

public sealed class CreateFolderInput
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? ParentId { get; set; }
}

public sealed class CreateHostInput
{
    public string Name { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? ParentId { get; set; }
}

/// <summary>
/// Shared shape consumed by the reusable sensor-editor partials (Shared/_Sensor*.cshtml)
/// so the create, element-edit and template-edit pages render the same markup from one
/// source. Implemented by CreateSensorInput, WorkspaceElementEditorInput and
/// WorkspaceTemplateEditorInput.
/// </summary>
public interface ISensorThresholdEditor
{
    SensorChannelMode SensorChannelMode { get; }

    int SensorChannelThresholdVisibleCount { get; }

    List<WorkspaceSensorChannelThresholdFieldInput> SensorChannelThresholdFields { get; }
}

/// <summary>Shared binding surface for the reusable <c>_SensorParameters</c> editor partial.</summary>
public interface ISensorParameterEditor
{
    string? SensorTypeKey { get; }

    List<WorkspaceSensorParameterFieldInput> SensorParameterFields { get; set; }

    string SensorAdvancedParametersText { get; set; }
}

/// <summary>Shared binding surface for the reusable <c>_SensorCredentials</c> editor partial.</summary>
public interface ISensorCredentialEditor
{
    List<WorkspaceSensorParameterFieldInput> SensorParameterFields { get; set; }

    List<SelectListItem> CredentialOptions { get; set; }

    Guid? SelectedCredentialId { get; set; }
}

/// <summary>Shared binding surface for the reusable <c>_SensorSchedule</c> editor partial.</summary>
public interface ISensorScheduleEditor
{
    /// <summary>Schedule mode: inherit | every | daily | weekly | monthly.</summary>
    string SchedulePreset { get; set; }

    /// <summary>For "every": the interval value expressed in <see cref="ScheduleEveryUnit"/>.</summary>
    int? ScheduleEveryValue { get; set; }

    /// <summary>For "every": seconds | minutes | hours | days.</summary>
    string ScheduleEveryUnit { get; set; }

    DayOfWeek? ScheduleDayOfWeek { get; set; }

    /// <summary>Weekly schedule weekdays (multi-select), e.g. Monday + Thursday.</summary>
    List<DayOfWeek> ScheduleDaysOfWeek { get; set; }

    int? ScheduleDayOfMonth { get; set; }

    string? ScheduleTime { get; set; }

    string? ScheduleInheritedLabel { get; set; }
}

public sealed class CreateSensorInput : ISensorThresholdEditor, ISensorScheduleEditor, ISensorParameterEditor, ISensorCredentialEditor
{
    public string Name { get; set; } = string.Empty;

    public bool NameAutoGenerated { get; set; } = true;

    public string SensorTypeKey { get; set; } = "ping";

    public Guid? TemplateId { get; set; }

    public List<SelectListItem> TemplateOptions { get; set; } = [];

    public string Target { get; set; } = string.Empty;

    public string? TargetPlaceholder { get; set; }

    public string? Description { get; set; }

    public string? TagsText { get; set; }

    public Guid? ParentId { get; set; }

    public bool? Highlight { get; set; }

    public string? HighlightInheritedLabel { get; set; }

    public string SchedulePreset { get; set; } = "inherit";

    public int? ScheduleEveryValue { get; set; }

    public string ScheduleEveryUnit { get; set; } = "minutes";

    public DayOfWeek? ScheduleDayOfWeek { get; set; } = DayOfWeek.Monday;

    public List<DayOfWeek> ScheduleDaysOfWeek { get; set; } = [];

    public int? ScheduleDayOfMonth { get; set; } = 1;

    public string? ScheduleTime { get; set; }

    public string? ScheduleInheritedLabel { get; set; }

    public Guid? SelectedCredentialId { get; set; }

    public List<SelectListItem> CredentialOptions { get; set; } = [];

    public List<WorkspaceSensorParameterFieldInput> SensorParameterFields { get; set; } = [];

    public string SensorAdvancedParametersText { get; set; } = string.Empty;

    public string SnmpWalkRootOid { get; set; } = "1.3.6.1.2.1";

    public List<WorkspaceSnmpWalkItemInput> SnmpWalkItems { get; set; } = [];

    public List<WorkspaceSensorChannelThresholdFieldInput> SensorChannelThresholdFields { get; set; } = [];

    public int SensorChannelThresholdVisibleCount { get; set; }

    public SensorChannelMode SensorChannelMode { get; set; } = SensorChannelMode.Dynamic;
}

public sealed class CreateTemplateInput
{
    public string Name { get; set; } = string.Empty;

    public MonitoringTemplateScope TargetKind { get; set; } = MonitoringTemplateScope.Any;

    public string SensorTypeKey { get; set; } = PingSensorExecutor.Definition.Key;

    public Guid? ParentTemplateId { get; set; }
}

public sealed class CreateNotificationRuleInput
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public Guid? SenderId { get; set; }

    public Guid? ReceiverId { get; set; }

    public Guid? TargetElementId { get; set; }

    public bool IncludeDescendants { get; set; } = true;

    public List<SensorState> TriggerStates { get; set; } = [];

    public int? CooldownMinutes { get; set; }

    public string SubjectTemplate { get; set; } = NotificationTemplateCatalog.DefaultSubjectTemplate;

    public string TextTemplate { get; set; } = NotificationTemplateCatalog.DefaultTextTemplate;

    public string HtmlTemplate { get; set; } = NotificationTemplateCatalog.DefaultHtmlTemplate;

    public List<SelectListItem> SenderOptions { get; set; } = [];

    public List<SelectListItem> ReceiverOptions { get; set; } = [];

    public List<SelectListItem> TargetOptions { get; set; } = [];

    public List<SelectListItem> TriggerStateOptions { get; set; } = [];
}

public class CreateNotificationSenderInput
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public NotificationEndpointKind Kind { get; set; } = NotificationEndpointKind.Email;

    public string SenderName { get; set; } = "Matmon";

    public string SenderEmail { get; set; } = "matmon@example.local";

    public string SmtpHost { get; set; } = "smtp.example.local";

    public int? SmtpPort { get; set; } = 587;

    public bool UseSsl { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string EndpointUrl { get; set; } = string.Empty;

    public string? Secret { get; set; }

    public int? TimeoutSeconds { get; set; } = 10;
}

public sealed class WorkspaceNotificationSenderEditorInput : CreateNotificationSenderInput
{
    public Guid Id { get; set; }
}

public class CreateNotificationReceiverInput
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public NotificationEndpointKind Kind { get; set; } = NotificationEndpointKind.Email;

    public string Target { get; set; } = string.Empty;

    public string? Secret { get; set; }

    public int? TimeoutSeconds { get; set; } = 10;
}

public sealed class WorkspaceNotificationReceiverEditorInput : CreateNotificationReceiverInput
{
    public Guid Id { get; set; }
}

public sealed class WorkspaceElementEditorInput : ISensorThresholdEditor, ISensorScheduleEditor, ISensorParameterEditor, ISensorCredentialEditor
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? TagsText { get; set; }

    public Guid? ParentId { get; set; }

    public string? ProbeId { get; set; }

    public string? EnrollmentToken { get; set; }

    public string? ProbeSubnetsText { get; set; }

    public string? Address { get; set; }

    public string? SensorTypeKey { get; set; }

    public string? Target { get; set; }

    public string? TargetPlaceholder { get; set; }

    public bool? Highlight { get; set; }

    public string? HighlightInheritedLabel { get; set; }

    public bool IsPaused { get; set; }

    public string SchedulePreset { get; set; } = "inherit";

    public int? ScheduleEveryValue { get; set; }

    public string ScheduleEveryUnit { get; set; } = "minutes";

    public DayOfWeek? ScheduleDayOfWeek { get; set; } = DayOfWeek.Monday;

    public List<DayOfWeek> ScheduleDaysOfWeek { get; set; } = [];

    public int? ScheduleDayOfMonth { get; set; } = 1;

    public string? ScheduleTime { get; set; }

    public string? ScheduleInheritedLabel { get; set; }

    public string EnabledMode { get; set; } = "inherit";

    public string? EnabledInheritedLabel { get; set; }

    public int? PollingIntervalSeconds { get; set; }

    public string? PollingIntervalSecondsPlaceholder { get; set; }

    public int? TimeoutSeconds { get; set; }

    public string? TimeoutSecondsPlaceholder { get; set; }

    public int? RetryCount { get; set; }

    public string? RetryCountPlaceholder { get; set; }

    public int? EventRetentionDays { get; set; }

    public string? EventRetentionDaysPlaceholder { get; set; }

    public int? ObservationRetentionDays { get; set; }

    public string? ObservationRetentionDaysPlaceholder { get; set; }

    public int? StatisticsRetentionDays { get; set; }

    public string? StatisticsRetentionDaysPlaceholder { get; set; }

    public int? StatisticsBucketMinutes { get; set; }

    public string? StatisticsBucketMinutesPlaceholder { get; set; }

    /// <summary>Optional fixed graph y-axis bounds (sensor native unit); null = auto-scale.</summary>
    public double? GraphMinValue { get; set; }

    public double? GraphMaxValue { get; set; }

    /// <summary>Human-readable per-sensor-type telemetry defaults (null for non-sensors).</summary>
    public string? TelemetryProfileSummary { get; set; }

    public string ParametersText { get; set; } = string.Empty;

    public string? ParametersTextPlaceholder { get; set; }

    public string SensorAdvancedParametersText { get; set; } = string.Empty;

    public string ThresholdsText { get; set; } = string.Empty;

    public string? ThresholdsTextPlaceholder { get; set; }

    public List<WorkspaceSensorChannelThresholdFieldInput> SensorChannelThresholdFields { get; set; } = [];

    public int SensorChannelThresholdVisibleCount { get; set; }

    public SensorChannelMode SensorChannelMode { get; set; } = SensorChannelMode.Dynamic;

    public List<Guid> AppliedTemplateIds { get; set; } = [];

    public Guid? TemplateOriginId { get; set; }

    public string? TemplateOriginName { get; set; }

    public List<SelectListItem> ParentOptions { get; set; } = [];

    public List<SelectListItem> TemplateOptions { get; set; } = [];

    public List<SelectListItem> SensorTypeOptions { get; set; } = [];

    public List<SelectListItem> CredentialOptions { get; set; } = [];

    public string? BootstrapSnippet { get; set; }

    public List<WorkspaceSensorParameterFieldInput> SensorParameterFields { get; set; } = [];

    public List<WorkspaceCredentialBundleInput> CredentialBundles { get; set; } = [];

    public int CredentialBundleVisibleCount { get; set; }

    public Guid? SelectedCredentialId { get; set; }
}

public sealed class WorkspaceSensorParameterFieldInput
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string? Group { get; set; }

    public SensorParameterKind Kind { get; set; } = SensorParameterKind.Text;

    public string? Description { get; set; }

    public bool Required { get; set; }

    public string? Placeholder { get; set; }

    public string? DisplayPlaceholder { get; set; }

    public string? InheritedValue { get; set; }

    public string? EffectiveValue { get; set; }

    public int? Min { get; set; }

    public int? Max { get; set; }

    public string? Step { get; set; }

    public string? Value { get; set; }

    public MonitoringCredentialKind? CredentialKind { get; set; }

    public bool IsCredential => CredentialKind is not null;

    public string? VisibleWhenParameterKey { get; set; }

    public string VisibleWhenValuesText { get; set; } = string.Empty;

    public bool IsVisible { get; set; } = true;

    public List<SelectListItem> Options { get; set; } = [];
}

public sealed class WorkspaceSensorChannelThresholdFieldInput
{
    public string ChannelKey { get; set; } = string.Empty;

    public string ChannelLabel { get; set; } = string.Empty;

    public string? Unit { get; set; }

    public bool IsDefault { get; set; }

    public string WarningComparison { get; set; } = ">";

    public string WarningValue { get; set; } = string.Empty;

    public string? WarningValuePlaceholder { get; set; }

    public string CriticalComparison { get; set; } = ">";

    public string CriticalValue { get; set; } = string.Empty;

    public string? CriticalValuePlaceholder { get; set; }

    public string Visual { get; set; } = "auto";

    /// <summary>Whether this channel is recorded into long-term statistics.</summary>
    public bool Logged { get; set; } = true;

    /// <summary>The channel's own default logging state (posted hidden), so the save only stores an override when it differs.</summary>
    public bool LogByDefault { get; set; } = true;

    /// <summary>A virtual/derived channel (e.g. <c>sensorState</c>): no user thresholds, but it can be picked as the default channel and given a visual.</summary>
    public bool IsVirtual { get; set; }

    public bool IsDeleted { get; set; }
}

public sealed class WorkspaceSnmpWalkItemInput
{
    public bool Selected { get; set; }

    public string Oid { get; set; } = string.Empty;

    public string Syntax { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool IsNumeric { get; set; }
}

internal sealed record ScheduleEditorState(
    string Preset,
    int? EveryValue,
    string EveryUnit,
    DayOfWeek? DayOfWeek,
    List<DayOfWeek> DaysOfWeek,
    int? DayOfMonth,
    string? Time,
    string InheritedLabel);

internal sealed record SensorParameterEditorState(
    List<WorkspaceSensorParameterFieldInput> Fields,
    string AdvancedText);

internal sealed record SensorThresholdEditorState(
    List<WorkspaceSensorChannelThresholdFieldInput> Fields,
    int VisibleCount);

public sealed class WorkspaceTemplateEditorInput : ISensorThresholdEditor, ISensorScheduleEditor, ISensorParameterEditor
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? TagsText { get; set; }

    public MonitoringTemplateScope TargetKind { get; set; } = MonitoringTemplateScope.Any;

    public string SensorTypeKey { get; set; } = PingSensorExecutor.Definition.Key;

    public List<WorkspaceSensorParameterFieldInput> SensorParameterFields { get; set; } = [];

    public string SensorAdvancedParametersText { get; set; } = string.Empty;

    public Guid? ParentTemplateId { get; set; }

    public string EnabledMode { get; set; } = "inherit";

    public int? PollingIntervalSeconds { get; set; }

    public int? TimeoutSeconds { get; set; }

    public int? RetryCount { get; set; }

    public int? EventRetentionDays { get; set; }

    public int? ObservationRetentionDays { get; set; }

    public int? StatisticsRetentionDays { get; set; }

    public int? StatisticsBucketMinutes { get; set; }

    public string ParametersText { get; set; } = string.Empty;

    public string ThresholdsText { get; set; } = string.Empty;

    public bool? Highlight { get; set; }

    public string? HighlightInheritedLabel { get; set; }

    public string SchedulePreset { get; set; } = "inherit";

    public int? ScheduleEveryValue { get; set; }

    public string ScheduleEveryUnit { get; set; } = "minutes";

    public DayOfWeek? ScheduleDayOfWeek { get; set; } = DayOfWeek.Monday;

    public List<DayOfWeek> ScheduleDaysOfWeek { get; set; } = [];

    public int? ScheduleDayOfMonth { get; set; } = 1;

    public string? ScheduleTime { get; set; }

    public string? ScheduleInheritedLabel { get; set; }

    public List<WorkspaceSensorChannelThresholdFieldInput> SensorChannelThresholdFields { get; set; } = [];

    public int SensorChannelThresholdVisibleCount { get; set; }

    public SensorChannelMode SensorChannelMode { get; set; } = SensorChannelMode.Dynamic;

    public List<SelectListItem> ParentOptions { get; set; } = [];

    public string? EnabledInheritedLabel { get; set; }

    public string? PollingIntervalSecondsPlaceholder { get; set; }

    public string? TimeoutSecondsPlaceholder { get; set; }

    public string? RetryCountPlaceholder { get; set; }

    public string? EventRetentionDaysPlaceholder { get; set; }

    public string? ObservationRetentionDaysPlaceholder { get; set; }

    public string? StatisticsRetentionDaysPlaceholder { get; set; }

    public string? StatisticsBucketMinutesPlaceholder { get; set; }

    public string? ParametersTextPlaceholder { get; set; }

    public string? ThresholdsTextPlaceholder { get; set; }

    public List<SelectListItem> CredentialOptions { get; set; } = [];

    public List<WorkspaceCredentialBundleInput> CredentialBundles { get; set; } = [];

    public int CredentialBundleVisibleCount { get; set; }

    public Guid? SelectedCredentialId { get; set; }

    public IReadOnlyList<TemplateImpactRow> ImpactRows { get; set; } = [];
}

public sealed record TemplateImpactRow(
    Guid SensorId,
    string SensorName,
    string SensorPath,
    string SensorTypeKey,
    MonitoringElementKind AppliedOnKind,
    string AppliedOnName,
    string AppliedOnPath,
    string ImpactKind);

internal sealed record TemplateImpactSource(
    Guid ElementId,
    MonitoringElementKind ElementKind,
    string ElementName,
    string ElementPath,
    string ImpactKind);

public sealed class WorkspaceCredentialBundleInput
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public MonitoringCredentialKind Kind { get; set; } = MonitoringCredentialKind.Generic;

    public string? Description { get; set; }

    public string? WinrmUsername { get; set; }

    public string? WinrmPassword { get; set; }

    public string? SshUsername { get; set; }

    public string? SshPassword { get; set; }

    public string? SshPrivateKeyPath { get; set; }

    public string? PveUser { get; set; }

    public string? PveTokenId { get; set; }

    public string? PveTokenSecret { get; set; }

    public string? MssqlUsername { get; set; }

    public string? MssqlPassword { get; set; }

    public string? SnmpCommunity { get; set; }

    public string? SnmpV3Username { get; set; }

    public string? SnmpV3AuthProtocol { get; set; }

    public string? SnmpV3AuthPassword { get; set; }

    public string? SnmpV3PrivacyProtocol { get; set; }

    public string? SnmpV3PrivacyPassword { get; set; }

    public string? SnmpV3ContextName { get; set; }

    public string? UnifiApiKey { get; set; }

    public string? GenericUsername { get; set; }

    public string? GenericPassword { get; set; }

    public string? GenericToken { get; set; }

    public string ValuesText { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }
}

public sealed class WorkspaceNotificationRuleEditorInput
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public Guid? SenderId { get; set; }

    public Guid? ReceiverId { get; set; }

    public Guid? TargetElementId { get; set; }

    public bool IncludeDescendants { get; set; } = true;

    public List<SensorState> TriggerStates { get; set; } = [];

    public int? CooldownMinutes { get; set; }

    public string SubjectTemplate { get; set; } = NotificationTemplateCatalog.DefaultSubjectTemplate;

    public string TextTemplate { get; set; } = NotificationTemplateCatalog.DefaultTextTemplate;

    public string HtmlTemplate { get; set; } = NotificationTemplateCatalog.DefaultHtmlTemplate;

    public List<SelectListItem> SenderOptions { get; set; } = [];

    public List<SelectListItem> ReceiverOptions { get; set; } = [];

    public List<SelectListItem> TargetOptions { get; set; } = [];

    public List<SelectListItem> TriggerStateOptions { get; set; } = [];
}

public sealed class TemplateApplyInput
{
    public Guid TemplateId { get; set; }

    public Guid? TargetElementId { get; set; }

    public List<SelectListItem> TargetOptions { get; set; } = [];
}

public sealed class EmailNotificationSettingsInput
{
    public string SenderName { get; set; } = string.Empty;

    public string SenderEmail { get; set; } = string.Empty;

    public string SmtpHost { get; set; } = string.Empty;

    public int? SmtpPort { get; set; }

    public bool UseSsl { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }
}

public sealed class WebhookNotificationSettingsInput
{
    public string EndpointUrl { get; set; } = string.Empty;

    public string? Secret { get; set; }

    public int? TimeoutSeconds { get; set; }
}
