using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;

namespace Matmon.Host.Pages;

// The sensor page's two history lists: every CHECK (paged from the database, filterable by state) and the STATUS history
// (state changes, alerts, acknowledgements, pauses - built by SensorStatusHistory). Both filters and pages live in the
// address, so a link can open them: /monitoring/sensor/<id>?checks=problems#checks.
public sealed partial class SensorDetailsModel
{
    public const int HistoryPageSize = 50;

    /// <summary>The checks filter: problems | error | warning | ok | unknown (empty = all).</summary>
    [BindProperty(SupportsGet = true, Name = "checks")]
    public string? ChecksFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "checksPage")]
    public int ChecksPage { get; set; } = 1;

    /// <summary>The status history filter: alerts | states | ack | pause (empty = all).</summary>
    [BindProperty(SupportsGet = true, Name = "status")]
    public string? StatusFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "statusPage")]
    public int StatusPage { get; set; } = 1;

    public SensorChecksView Checks { get; private set; } = default!;

    public SensorStatusHistoryView StatusHistory { get; private set; } = default!;

    /// <summary>The address of the checks of a sensor filtered to its problems - what an alert links to.</summary>
    public static string FailingChecksPath(string pathBase, Guid sensorId)
        => $"{pathBase}/monitoring/sensor/{sensorId}?checks=problems#checks";

    private static readonly (string Key, string Label, SensorState[] States)[] CheckFilters =
    [
        ("problems", "Errors and warnings", [SensorState.Warning, SensorState.Critical]),
        ("error", "Errors", [SensorState.Critical]),
        ("warning", "Warnings", [SensorState.Warning]),
        ("ok", "OK", [SensorState.Healthy]),
        ("unknown", "No data", [SensorState.Unknown, SensorState.Disabled, SensorState.Paused])
    ];

    private static readonly (string Key, string Label, SensorStatusCategory Category)[] StatusFilters =
    [
        ("alerts", "Alerts", SensorStatusCategory.Alerts),
        ("states", "State changes", SensorStatusCategory.States),
        ("ack", "Acknowledgements and mutes", SensorStatusCategory.Acknowledgements),
        ("pause", "Paused and resumed", SensorStatusCategory.Pause)
    ];

    private SensorChecksView BuildChecks(
        SensorElement sensor,
        MonitoringSettings effectiveSettings,
        string fallbackUnit,
        string? defaultChannelKey,
        SensorUnitScale displayScale)
    {
        var counts = _workspaceStore.GetSensorObservationStateCounts(sensor.Id);
        int CountOf(IEnumerable<SensorState> states) => states.Sum(state => counts.TryGetValue(state, out var n) ? n : 0);

        var filter = CheckFilters.FirstOrDefault(option => string.Equals(option.Key, ChecksFilter?.Trim(), StringComparison.OrdinalIgnoreCase));
        ChecksFilter = filter.Key; // an unknown value counts as "all"
        var total = counts.Values.Sum();
        var matching = filter.Key is null ? total : CountOf(filter.States);
        var pageCount = Math.Max(1, (matching + HistoryPageSize - 1) / HistoryPageSize);
        ChecksPage = Math.Clamp(ChecksPage, 1, pageCount);

        var page = matching == 0
            ? []
            : _workspaceStore.GetSensorObservationPage(sensor.Id, filter.States, (ChecksPage - 1) * HistoryPageSize, HistoryPageSize);

        var profile = SensorTelemetryProfiles.Resolve(sensor.SensorTypeKey);
        var retentionDays = effectiveSettings.ObservationRetentionDays is > 0 ? effectiveSettings.ObservationRetentionDays.Value : profile.RawObservationDays;

        return new SensorChecksView(
            ChecksFilter,
            CheckFilters.Select(option => new SensorHistoryFilterOption(option.Key, option.Label, CountOf(option.States))).ToArray(),
            BuildObservationRows(page, fallbackUnit, defaultChannelKey, displayScale),
            total,
            matching,
            ChecksPage,
            pageCount,
            retentionDays);
    }

    private SensorStatusHistoryView BuildStatusHistory(SensorElement sensor)
    {
        var now = DateTimeOffset.UtcNow;
        var all = SensorStatusHistory.Build(
            _workspaceStore.GetElementEvents(sensor.Id),
            _workspaceStore.GetElementAlerts(sensor.Id),
            now);

        var filter = StatusFilters.FirstOrDefault(option => string.Equals(option.Key, StatusFilter?.Trim(), StringComparison.OrdinalIgnoreCase));
        StatusFilter = filter.Key;
        var matching = filter.Key is null ? all : all.Where(entry => entry.Category == filter.Category).ToArray();
        var pageCount = Math.Max(1, (matching.Count + HistoryPageSize - 1) / HistoryPageSize);
        StatusPage = Math.Clamp(StatusPage, 1, pageCount);

        var rows = matching
            .Skip((StatusPage - 1) * HistoryPageSize)
            .Take(HistoryPageSize)
            .Select(entry => ToStatusRow(entry, sensor.Id))
            .ToArray();

        return new SensorStatusHistoryView(
            StatusFilter,
            StatusFilters.Select(option => new SensorHistoryFilterOption(option.Key, option.Label, all.Count(entry => entry.Category == option.Category))).ToArray(),
            rows,
            all.Count,
            matching.Count,
            StatusPage,
            pageCount,
            SensorTelemetryProfiles.General.EventRetentionDays);
    }

    private SensorStatusRow ToStatusRow(SensorStatusEntry entry, Guid sensorId)
    {
        var (label, icon, tone) = entry.Kind switch
        {
            SensorStatusKind.StateChanged => ("State changed", "activity", (string?)null),
            SensorStatusKind.AlertRaised => ("Alert raised", "alert", "error"),
            SensorStatusKind.AlertReopened => ("Alert re-opened", "refresh", "error"),
            SensorStatusKind.AlertRecovered => ("Recovered", "check-circle", "ok"),
            SensorStatusKind.AlertEscalated => ("Escalated", "arrow-up", "warning"),
            SensorStatusKind.AlertAcknowledged => ("Acknowledged", "check", "info"),
            SensorStatusKind.AlertResolved => ("Alert resolved", "check-circle", "ok"),
            SensorStatusKind.AlertMuted => ("Alerts muted", "bell", "info"),
            SensorStatusKind.AlertUnmuted => ("Alerts un-muted", "bell", "info"),
            SensorStatusKind.Paused => ("Paused", "pause", "paused"),
            SensorStatusKind.Resumed => ("Resumed", "play", "info"),
            _ => ("Event", "info", null)
        };

        string? durationText = entry.Duration is { } duration
            ? entry.Kind switch
            {
                SensorStatusKind.StateChanged => entry.Ongoing ? $"{MonitoringDisplay.FormatSpan(duration)} so far" : $"for {MonitoringDisplay.FormatSpan(duration)}",
                SensorStatusKind.Resumed => $"paused for {MonitoringDisplay.FormatSpan(duration)}",
                SensorStatusKind.Paused => $"{MonitoringDisplay.FormatSpan(duration)} so far",
                SensorStatusKind.AlertRecovered or SensorStatusKind.AlertResolved => $"open for {MonitoringDisplay.FormatSpan(duration)}",
                _ => null
            }
            : null;

        var message = entry.Message;
        if (entry.ProblemDuration is { } problem)
        {
            var down = $"Down for {MonitoringDisplay.FormatSpan(problem)}";
            message = string.IsNullOrWhiteSpace(message) ? down : $"{down} - {message}";
        }

        // An alert that was raised: the checks that showed the problem are one click away.
        var checksLink = entry.Kind is SensorStatusKind.AlertRaised or SensorStatusKind.AlertReopened
            ? FailingChecksPath(Request.PathBase, sensorId)
            : null;

        return new SensorStatusRow(
            entry.TimestampUtc.ToDisplay().ToString("dd.MM HH:mm:ss"),
            entry.Kind.ToString(),
            label,
            icon,
            tone,
            entry.FromState is { } from ? MonitoringStatePresentation.Key(from) : null,
            entry.FromState is { } fromLabel ? MonitoringStatePresentation.Label(fromLabel) : null,
            entry.ToState is { } to ? MonitoringStatePresentation.Key(to) : null,
            entry.ToState is { } toLabel ? MonitoringStatePresentation.Label(toLabel) : null,
            entry.Actor,
            string.IsNullOrWhiteSpace(message) ? null : message,
            durationText,
            checksLink);
    }
}

public sealed record SensorHistoryFilterOption(string Key, string Label, int Count);

public sealed record SensorChecksView(
    string? Filter,
    IReadOnlyList<SensorHistoryFilterOption> Options,
    IReadOnlyList<SensorObservationRow> Rows,
    int Total,
    int Matching,
    int Page,
    int PageCount,
    int RetentionDays);

public sealed record SensorStatusHistoryView(
    string? Filter,
    IReadOnlyList<SensorHistoryFilterOption> Options,
    IReadOnlyList<SensorStatusRow> Rows,
    int Total,
    int Matching,
    int Page,
    int PageCount,
    int RetentionDays);

public sealed record SensorStatusRow(
    string TimestampText,
    string KindKey,
    string KindLabel,
    string Icon,
    string? Tone,
    string? FromKey,
    string? FromLabel,
    string? ToKey,
    string? ToLabel,
    string? Actor,
    string? Message,
    string? DurationText,
    string? ChecksLink);
