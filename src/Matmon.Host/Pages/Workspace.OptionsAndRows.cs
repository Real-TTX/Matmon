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
            _ => []
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
        // Carry the live state onto the flat row so the state filter (which runs on these rows) works - previously
        // only paused was set, so filtering by warning/error/ok matched nothing.
        var liveSeries = telemetrySeriesMap.TryGetValue(element.Id, out var seriesForState) ? seriesForState : null;
        var stateKey = isPausedSensor
            ? MonitoringStatePresentation.PausedKey
            : (liveSeries?.StateKey ?? string.Empty);
        var stateLabel = isPausedSensor ? MonitoringStatePresentation.PausedLabel : liveSeries?.StateLabel;
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
                probeStatus?.LastSeenUtc.ToDisplay().ToString("HH:mm:ss") ?? (node.Depth == 0 ? "local" : "-"),
                probeStatus is null
                    ? (node.Depth == 0 ? "local primary" : "no heartbeat")
                    : BuildProbeStatusMessage(severity, now - probeStatus.LastSeenUtc),
                BuildProbeBootstrapSnippet(node.ProbeId, node.Name, enrollmentToken)));

            probeStack.Push((node.Depth, severity));
        }

        return rows;
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
}
