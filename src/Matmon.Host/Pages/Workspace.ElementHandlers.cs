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

    public async Task<IActionResult> OnPostRunSensor(Guid sensorId, string? returnUrl)
    {
        try
        {
            if (_workspaceStore.FindElement(sensorId) is not SensorElement sensor)
            {
                throw new InvalidOperationException("Selected element is not a sensor.");
            }

            var result = await _sensorExecutionService.ExecuteNowAsync(sensorId, cancellationToken: HttpContext.RequestAborted);
            StatusMessage = $"Ran '{sensor.Name}': {FormatSensorStateLabel(result.State)} - {result.Duration.TotalMilliseconds:0.#} ms"
                + (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $" - {result.Message}");
            return RedirectAfterAction(returnUrl, "/Monitoring", null);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostRunElementSensors(Guid elementId, string? returnUrl)
    {
        try
        {
            var element = _workspaceStore.GetAllElements().FirstOrDefault(candidate => candidate.Id == elementId)
                ?? throw new InvalidOperationException("Element not found.");

            var sensorIds = EnumerateDescendantSensorIds(element).ToArray();
            if (sensorIds.Length == 0)
            {
                StatusMessage = $"No sensors under '{element.Name}'.";
                return RedirectAfterAction(returnUrl, "/Monitoring", null);
            }

            // A poll can take seconds (timeouts / slow targets), so run the whole subtree in the BACKGROUND with
            // its own DI scope and return immediately - awaiting a full subtree would hang the request/UI. The
            // fresh observations land just like the polling loop's; the tree reflects them on its next refresh.
            var ids = sensorIds;
            var scopeFactory = _scopeFactory;
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<ISensorExecutionService>();
                foreach (var id in ids)
                {
                    try { await executor.ExecuteNowAsync(id); }
                    catch { /* one failing sensor must not abort the rest of the batch */ }
                }
            });

            StatusMessage = $"Running {sensorIds.Length} sensor{(sensorIds.Length == 1 ? string.Empty : "s")} under '{element.Name}' now…";
            return RedirectAfterAction(returnUrl, "/Monitoring", null);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    private static IEnumerable<Guid> EnumerateDescendantSensorIds(MonitoringElement element)
    {
        if (element is SensorElement sensor)
        {
            yield return sensor.Id;
            yield break;
        }

        if (element is MonitoringContainerElement container)
        {
            foreach (var child in container.Children)
            {
                foreach (var id in EnumerateDescendantSensorIds(child))
                {
                    yield return id;
                }
            }
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

    public IActionResult OnPostSaveElement()
    {
        try
        {
            if (ElementEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("No element selected.");
            }

            var credentialIssueCount = 0;
            string? elementName = null;
            MonitoringElementKind elementKind = default;

            // Mutate under the store lock (UpdateElement) instead of editing a live reference outside
            // it, so the edit is serialized against readers and the polling service.
            var found = _workspaceStore.UpdateElement(ElementEditor.Id, element =>
            {
                credentialIssueCount = ApplyElementEditor(element, ElementEditor);
                elementName = element.Name;
                elementKind = element.Kind;
            });

            if (!found)
            {
                throw new InvalidOperationException("Element not found.");
            }

            StatusMessage = credentialIssueCount == 0
                ? $"{elementKind} '{elementName}' saved."
                : $"{elementKind} '{elementName}' saved. {credentialIssueCount} credential issue{(credentialIssueCount == 1 ? string.Empty : "s")} found.";
            return RedirectToPage(new { selectedId = ElementEditor.Id, selectedTemplateId = SelectedTemplateId });
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
                throw new InvalidOperationException("No probe element selected.");
            }

            var element = _workspaceStore.FindElement(ElementEditor.Id)
                ?? throw new InvalidOperationException("Element not found.");

            if (element is not ProbeElement probe)
            {
                throw new InvalidOperationException("Tokens can only be rotated on probes.");
            }

            _workspaceStore.RotateProbeToken(probe.Id);
            StatusMessage = $"Token for '{probe.Name}' rotated.";
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
                throw new InvalidOperationException("No element selected.");
            }

            var element = _workspaceStore.FindElement(ElementEditor.Id)
                ?? throw new InvalidOperationException("Element not found.");

            if (!_workspaceStore.DeleteElement(element.Id))
            {
                throw new InvalidOperationException("The element could not be deleted.");
            }

            StatusMessage = $"Element '{element.Name}' deleted.";
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
        var scheduleDefaultInterval = SensorScheduleDefaults.Resolve((element as SensorElement)?.SensorTypeKey);
        var scheduleState = BuildScheduleEditorState(localSettings, effectiveSettings, scheduleDefaultInterval);
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
            PollingIntervalSecondsPlaceholder = FormatSecondsPlaceholder(effectiveSettings.PollingInterval ?? scheduleDefaultInterval),
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
                    "The sensor type cannot be changed after creation - its history, channels and statistics depend on it. Create a new sensor to use a different type.");
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
                throw new InvalidOperationException("The parent could not be changed.");
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
}
