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

public sealed partial class WorkspaceModel
{
    public IActionResult OnPostReapplyElementTemplate()
    {
        try
        {
            var element = _workspaceStore.FindElement(ElementEditor.Id)
                ?? throw new InvalidOperationException("Element not found.");

            if (element.TemplateOriginId is not Guid originId)
            {
                throw new InvalidOperationException("This element has no origin template.");
            }

            var template = _workspaceStore.FindTemplate(originId)
                ?? throw new InvalidOperationException("The origin template no longer exists.");

            _workspaceStore.UpdateElement(element.Id, e => ApplyTemplateCopy(e, template, elementWins: false));
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
                ?? throw new InvalidOperationException("Element not found.");

            if (!_workspaceStore.UpdateElement(element.Id, e => e.TemplateOriginId = null))
            {
                throw new InvalidOperationException("Element not found.");
            }

            StatusMessage = $"Template origin detached from '{element.Name}'.";
            return RedirectToPage(new { selectedId = element.Id, selectedTemplateId = SelectedTemplateId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
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

            StatusMessage = $"Template '{template.Name}' created.";
            return RedirectToPage("/TemplateEditor", new { selectedTemplateId = template.Id, returnUrl = ReturnUrl });
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
                throw new InvalidOperationException("No template selected.");
            }

            var template = _workspaceStore.FindTemplate(TemplateEditor.Id)
                ?? throw new InvalidOperationException("Template not found.");
            var templateMap = _workspaceStore.Workspace.Templates.ToDictionary(candidate => candidate.Id);
            var impactedSensors = BuildTemplateImpactRows(_workspaceStore.Workspace.RootProbe, template, templateMap).Count;

            var templateName = template.Name;
            _workspaceStore.UpdateTemplate(TemplateEditor.Id, edited =>
            {
                ApplyTemplateEditor(edited, TemplateEditor);
                templateName = edited.Name;
            });

            StatusMessage = impactedSensors == 0
                ? $"Template '{templateName}' saved. No sensors affected."
                : $"Template '{templateName}' saved. {impactedSensors} sensor{(impactedSensors == 1 ? string.Empty : "s")} affected.";
            return RedirectToPage(new { selectedId = SelectedId, selectedTemplateId = TemplateEditor.Id });
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
                throw new InvalidOperationException("No template selected.");
            }

            var template = _workspaceStore.FindTemplate(TemplateEditor.Id)
                ?? throw new InvalidOperationException("Template not found.");

            if (!_workspaceStore.DeleteTemplate(template.Id))
            {
                throw new InvalidOperationException("The template could not be deleted.");
            }

            StatusMessage = $"Template '{template.Name}' deleted.";
            return RedirectToPage(new { selectedId = SelectedId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
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
            // unrendered and leave a gap - and ASP.NET's sequential collection binding stops at
            // the first missing index, silently dropping every posted param (e.g. "Script is
            // required" even though the script was typed).
            : BuildSensorParameterFields(templateDefinition, localSettings.Parameters, effectiveSettings.Parameters)
                .Where(field => !field.IsCredential)
                .ToList();
        var templateAdvancedParametersText = templateDefinition is null
            ? BuildSensorAdvancedParametersText(localSettings.Parameters, [])
            : BuildSensorAdvancedParametersText(localSettings.Parameters, templateDefinition.Parameters.Select(parameter => parameter.Key));
        var credentialBundleState = BuildCredentialBundleEditorState(localSettings.Credentials);
        var scheduleDefaultInterval = SensorScheduleDefaults.Resolve(template.SensorTypeKey);
        var scheduleState = BuildScheduleEditorState(localSettings, effectiveSettings, scheduleDefaultInterval);

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
            PollingIntervalSecondsPlaceholder = FormatSecondsPlaceholder(effectiveSettings.PollingInterval ?? scheduleDefaultInterval),
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
        var chain = MonitoringInheritanceResolver.ResolveTemplateChain(template.Id, templates).ToList();
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
            MonitoringInheritanceResolver.ResolveTemplateChain(appliedTemplateId, templateMap).Any(template => template.Id == templateId));
    }
}

internal sealed record TemplateImpactSource(
    Guid ElementId,
    MonitoringElementKind ElementKind,
    string ElementName,
    string ElementPath,
    string ImpactKind);
