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

    private static SensorThresholdEditorState BuildSensorThresholdEditorState(
        string? sensorTypeKey,
        SensorChannelMode channelMode,
        MonitoringSettings settings,
        IReadOnlyList<WorkspaceSensorChannelThresholdFieldInput> currentFields,
        IReadOnlyList<SensorChannelValue>? observedChannels)
    {
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

        // A managed key that survived here (not already added as an observed channel) has stored config
        // but isn't in the latest run. Only flag it "orphaned" when the sensor actually reported channels
        // this run - for a sensor that has never run, these are legitimate pre-configured thresholds.
        var hasReportedChannels = observedChannels is { Count: > 0 };
        foreach (var channelKey in EnumerateManagedThresholdChannelKeys(settings))
        {
            if (!usedKeys.Add(channelKey))
            {
                continue;
            }

            var orphanField = BuildSensorThresholdField(sensorTypeKey, settings, channelKey, null, null, false, true, false, currentFieldMap);
            orphanField.IsOrphaned = hasReportedChannels;
            rows.Add(orphanField);
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
        var realRows = rows.Count(row => !string.IsNullOrWhiteSpace(row.ChannelKey));
        var visibleCount = channelMode == SensorChannelMode.Fixed
            ? rows.Count
            // Dynamic: nothing to show until channels exist (reported or configured). Once there are
            // real rows, show them all + one blank spare for a manual add (capped at the maximum).
            : realRows == 0
                ? 0
                : Math.Min(maximumRows, Math.Max(realRows + 1, configuredCount + 1));

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
        else
        {
            // Dynamic sensors (script/local script/…) discover their channels at runtime. Don't invent
            // a placeholder channel from defaultChannelKey - show nothing until the sensor has actually
            // run (or the user adds one manually). This is what prevents "phantom" channels in the editor.
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
            ChannelLabel = string.IsNullOrWhiteSpace(channelLabel) ? MonitoringDisplay.HumanizeChannelKey(channelKey) : channelLabel,
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
            ChannelLabel = MonitoringDisplay.HumanizeChannelKey(channelKey)
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
}

internal sealed record SensorThresholdEditorState(
    List<WorkspaceSensorChannelThresholdFieldInput> Fields,
    int VisibleCount);
