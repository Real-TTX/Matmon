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

        private string? LastCheck => _latestObservation?.TimestampUtc.ToDisplay().ToString("HH:mm:ss");

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
}
