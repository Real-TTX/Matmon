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
                // Never surface an inherited secret (password/token) as a visible placeholder -
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
                    // from the selected or inherited credential bundle - which is NOT reflected in
                    // the posted/inherited param value here - so don't block the save on a blank
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
}

internal sealed record SensorParameterEditorState(
    List<WorkspaceSensorParameterFieldInput> Fields,
    string AdvancedText);
