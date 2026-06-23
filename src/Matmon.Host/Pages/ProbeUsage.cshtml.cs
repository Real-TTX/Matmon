using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

[Authorize]
public sealed class ProbeUsageModel : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly IProbeRegistry _probeRegistry;
    private readonly StorageOverviewProvider _storageOverviewProvider;
    private readonly MonitoringInheritanceResolver _resolver = new();

    public ProbeUsageModel(
        IMonitoringWorkspaceStore workspaceStore,
        IProbeRegistry probeRegistry,
        StorageOverviewProvider storageOverviewProvider)
    {
        _workspaceStore = workspaceStore;
        _probeRegistry = probeRegistry;
        _storageOverviewProvider = storageOverviewProvider;
    }

    [BindProperty(SupportsGet = true)]
    public Guid ProbeElementId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ViewMode { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? StateFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? UsageFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? SortBy { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool HighlightedOnly { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string CurrentUrl => $"{Request.Path}{Request.QueryString}";

    public ProbeUsageViewModel View { get; private set; } = default!;

    public IActionResult OnGet()
    {
        return LoadView() ? Page() : NotFound();
    }

    private bool LoadView()
    {
        var probe = _workspaceStore.FindElement(ProbeElementId) as ProbeElement;
        if (probe is null)
        {
            return false;
        }

        var storage = _storageOverviewProvider.GetOverview();
        var workspace = _workspaceStore.Workspace;
        var elementsById = _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
        var templateMap = workspace.Templates.ToDictionary(template => template.Id);
        var sensorDefinitions = workspace.SensorDefinitions.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);
        var latestObservations = _workspaceStore.GetLatestSensorObservations();
        var observationCounts = _workspaceStore.GetSensorObservationCounts();
        var recentHistory = _workspaceStore.GetRecentSensorHistoryBySensor(TimeSpan.FromHours(1), maxPerSensor: 240);
        var probeStatuses = _probeRegistry.GetAll().ToDictionary(snapshot => snapshot.ProbeId, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var recentCutoff = now - TimeSpan.FromHours(1);
        var lineage = BuildLineage(probe, elementsById);
        var path = string.Join(" / ", lineage.Select(element => element.Name));
        var isPrimary = probe.ParentId is null ||
            string.Equals(probe.ProbeId, "primary", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(probe.ProbeId, "master", StringComparison.OrdinalIgnoreCase);
        var liveStatus = !string.IsNullOrWhiteSpace(probe.ProbeId) && probeStatuses.TryGetValue(probe.ProbeId, out var snapshot)
            ? snapshot
            : null;
        var status = ResolveProbeStatus(isPrimary, liveStatus);
        var statusColor = status.StateKey == MonitoringStatePresentation.Key(MonitoringSeverity.Error)
            ? MonitoringStatePresentation.Color(SensorState.Critical)
            : MonitoringStatePresentation.Color(SensorState.Healthy);

        var sensorRows = new List<ProbeUsageSensorRow>();
        var estimatedRetainedBytesTotal = 0d;

        foreach (var sensor in EnumerateDescendants(probe).OfType<SensorElement>())
        {
            var sensorLineage = BuildLineage(sensor, elementsById);
            var effectiveSettings = _resolver.Resolve(sensorLineage, templateMap);
            var definition = sensorDefinitions.TryGetValue(sensor.SensorTypeKey, out var sensorDefinition)
                ? sensorDefinition
                : null;
            var usageLevel = definition?.UsageLevel ?? SensorUsageCatalog.Resolve(sensor.SensorTypeKey);
            var history = recentHistory.TryGetValue(sensor.Id, out var observations)
                ? observations
                : Array.Empty<SensorObservation>();
            history = history
                .Where(observation => observation.TimestampUtc >= recentCutoff)
                .ToArray();
            latestObservations.TryGetValue(sensor.Id, out var latestObservation);
            latestObservation ??= history.LastOrDefault();

            var state = sensor.IsPaused
                ? SensorState.Paused
                : latestObservation?.State ?? SensorState.Unknown;

            var scheduleSummary = FormatScheduleSummary(effectiveSettings);
            var estimatedRunsPerDay = EstimateExecutionsPerDay(effectiveSettings, sensor.SensorTypeKey);
            var averageBytes = EstimateAverageObservationBytes(history, latestObservation);
            var estimatedBytesPerHour = averageBytes * estimatedRunsPerDay / 24d;
            var estimatedBytesPerDay = averageBytes * estimatedRunsPerDay;
            var estimatedRetainedBytes = sensor.IsPaused
                ? 0d
                : EstimateRetainedBytes(effectiveSettings, sensor.SensorTypeKey, estimatedBytesPerDay, latestObservation);
            estimatedRetainedBytesTotal += estimatedRetainedBytes;
            var averageDuration = history.Length > 0
                ? history.Average(entry => entry.Duration.TotalMilliseconds)
                : latestObservation?.Duration.TotalMilliseconds;
            var loadScore = EstimateLoadScore(usageLevel, estimatedRunsPerDay, averageDuration, state);
            var storedObservationCount = observationCounts.TryGetValue(sensor.Id, out var storedCount) ? storedCount : 0;
            var target = SensorTargetResolver.Resolve(sensor, sensorLineage);
            var metaSummaryParts = new List<string>
            {
                string.Join(" / ", sensorLineage.Select(element => element.Name)),
                definition?.DisplayName ?? sensor.SensorTypeKey
            };

            if (!string.IsNullOrWhiteSpace(target))
            {
                metaSummaryParts.Add($"Target: {target}");
            }

            var searchText = string.Join(' ',
                sensor.Name,
                string.Join(" / ", sensorLineage.Select(element => element.Name)),
                target,
                sensor.SensorTypeKey,
                definition?.DisplayName ?? sensor.SensorTypeKey,
                scheduleSummary,
                latestObservation?.Message ?? string.Empty,
                sensor.IsPaused ? "paused" : state.ToString(),
                effectiveSettings.Highlight == true ? "highlighted" : string.Empty);

            sensorRows.Add(new ProbeUsageSensorRow(
                sensor.Id,
                sensor.Name,
                string.Join(" / ", sensorLineage.Select(element => element.Name)),
                target,
                string.Join(" · ", metaSummaryParts),
                sensor.SensorTypeKey,
                definition?.DisplayName ?? sensor.SensorTypeKey,
                SensorUsageCatalog.Key(usageLevel),
                SensorUsageCatalog.Label(usageLevel),
                sensor.IsPaused ? MonitoringStatePresentation.PausedKey : MonitoringStatePresentation.Key(state),
                sensor.IsPaused ? MonitoringStatePresentation.PausedLabel : MonitoringStatePresentation.Label(state),
                sensor.IsPaused ? "sensor paused" : latestObservation?.Message,
                sensor.IsPaused,
                effectiveSettings.Highlight == true,
                scheduleSummary,
                latestObservation is null ? "-" : latestObservation.TimestampUtc.ToLocalTime().ToString("dd.MM HH:mm"),
                history.Length.ToString(CultureInfo.InvariantCulture),
                averageDuration.HasValue ? FormatDuration(TimeSpan.FromMilliseconds(averageDuration.Value)) : "-",
                estimatedBytesPerHour,
                estimatedBytesPerDay,
                storage.FormatBytes((long)Math.Round(estimatedBytesPerHour)),
                storage.FormatBytes((long)Math.Round(estimatedBytesPerDay)),
                0d,
                loadScore,
                searchText,
                storedObservationCount,
                storedObservationCount.ToString("N0", CultureInfo.InvariantCulture)));
        }

        var normalizedViewMode = NormalizeViewMode(ViewMode);
        var normalizedSearch = NormalizeSearch(Search);
        var normalizedStateFilter = NormalizeStateFilter(StateFilter);
        var normalizedUsageFilter = NormalizeUsageFilter(UsageFilter);
        var normalizedSortBy = NormalizeSortBy(SortBy);
        var filtersActive = !string.IsNullOrWhiteSpace(normalizedSearch)
            || !string.Equals(normalizedStateFilter, "all", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(normalizedUsageFilter, "all", StringComparison.OrdinalIgnoreCase)
            || HighlightedOnly;
        var filteredSensorRows = sensorRows
            .Where(row => MatchesFilters(row, normalizedSearch, normalizedStateFilter, normalizedUsageFilter, HighlightedOnly))
            .ToList();

        filteredSensorRows = SortSensorRows(filteredSensorRows, normalizedSortBy);

        var visibleSensorCount = filteredSensorRows.Count;
        var visibleGroupCount = filteredSensorRows
            .GroupBy(row => new { row.SensorTypeKey, row.SensorTypeLabel, row.UsageLevelKey, row.UsageLevelLabel })
            .Count();
        var totalSensorCount = sensorRows.Count;
        var totalGroupCount = sensorRows
            .GroupBy(row => new { row.SensorTypeKey, row.SensorTypeLabel, row.UsageLevelKey, row.UsageLevelLabel })
            .Count();
        var visibleLoadScore = filteredSensorRows.Sum(row => row.LoadScore);
        var visibleMaxLoad = filteredSensorRows.Count == 0
            ? 1d
            : Math.Max(filteredSensorRows.Max(row => row.LoadScore), 0.0001d);
        var maxStoredObservationCount = filteredSensorRows.Count == 0
            ? 0
            : filteredSensorRows.Max(row => row.StoredObservationCount);
        var filteredSensorRowsWithPercent = filteredSensorRows
            .Select(row => row with
            {
                RelativeLoadPercent = row.LoadScore / visibleMaxLoad * 100d,
                RelativeLogPercent = maxStoredObservationCount <= 0
                    ? 0d
                    : row.StoredObservationCount / (double)maxStoredObservationCount * 100d
            })
            .ToArray();

        // "Largest logs": the sensors with the most actually-stored observations — a fast,
        // retrospective way to spot mis-scheduled (over-polling) sensors.
        var topLogSensors = filteredSensorRowsWithPercent
            .Where(row => row.StoredObservationCount > 0)
            .OrderByDescending(row => row.StoredObservationCount)
            .Take(8)
            .ToArray();
        var totalStoredObservationCount = filteredSensorRowsWithPercent.Sum(row => (long)row.StoredObservationCount);

        var groups = BuildUsageGroups(filteredSensorRowsWithPercent, visibleLoadScore, storage);
        var distributionSegments = BuildDistributionSegments(normalizedViewMode, groups, filteredSensorRowsWithPercent);
        var healthySensorCount = filteredSensorRowsWithPercent.Count(row => row.StateKey == MonitoringStatePresentation.Key(MonitoringSeverity.Ok));
        var warningSensorCount = filteredSensorRowsWithPercent.Count(row => row.StateKey == MonitoringStatePresentation.Key(MonitoringSeverity.Warning));
        var errorSensorCount = filteredSensorRowsWithPercent.Count(row => row.StateKey == MonitoringStatePresentation.Key(MonitoringSeverity.Error));
        var pausedSensorCount = filteredSensorRowsWithPercent.Count(row => row.StateKey == MonitoringStatePresentation.PausedKey);
        var estimatedBytesPerHourTotal = filteredSensorRowsWithPercent.Sum(row => row.EstimatedBytesPerHourValue);
        var estimatedBytesPerDayTotal = filteredSensorRowsWithPercent.Sum(row => row.EstimatedBytesPerDayValue);
        var estimatedBytesPerSecondTotal = estimatedBytesPerHourTotal / 3600d;

        View = new ProbeUsageViewModel(
            probe.Id,
            probe.Name,
            path,
            probe.ProbeId ?? "-",
            isPrimary ? "Primary" : "Secondary",
            status.StateKey,
            status.StateLabel,
            status.Message,
            status.LastSeenText,
            storage.FormatBytes(storage.DataDirectoryBytes),
            storage.DriveFreePercent.HasValue
                ? $"{storage.DriveFreePercent.Value:0.#}% free"
                : "-",
            storage.FormatBytes((long)Math.Round(estimatedBytesPerSecondTotal)),
            storage.FormatBytes((long)Math.Round(estimatedBytesPerHourTotal)),
            storage.FormatBytes((long)Math.Round(estimatedBytesPerDayTotal)),
            storage.FormatBytes((long)Math.Round(estimatedRetainedBytesTotal)),
            statusColor,
            visibleSensorCount,
            visibleGroupCount,
            totalSensorCount,
            totalGroupCount,
            healthySensorCount,
            warningSensorCount,
            errorSensorCount,
            pausedSensorCount,
            normalizedViewMode,
            normalizedSearch,
            normalizedStateFilter,
            normalizedUsageFilter,
            normalizedSortBy,
            HighlightedOnly,
            filtersActive,
            BuildFilterSummary(visibleSensorCount, totalSensorCount, visibleGroupCount, totalGroupCount, normalizedViewMode, normalizedSearch, normalizedStateFilter, normalizedUsageFilter, HighlightedOnly),
            BuildDistributionLabel(normalizedViewMode),
            distributionSegments,
            groups,
            filteredSensorRowsWithPercent,
            topLogSensors,
            totalStoredObservationCount.ToString("N0", CultureInfo.InvariantCulture));

        return true;
    }

    private static ProbeUsageStatus ResolveProbeStatus(bool isPrimary, ProbeStatusSnapshot? liveStatus)
    {
        if (isPrimary)
        {
            return new ProbeUsageStatus(
                MonitoringStatePresentation.Key(MonitoringSeverity.Ok),
                "Local",
                "local primary probe",
                "local");
        }

        if (liveStatus is null)
        {
            return new ProbeUsageStatus(
                MonitoringStatePresentation.Key(MonitoringSeverity.Error),
                "Offline",
                "no heartbeat",
                "-");
        }

        return new ProbeUsageStatus(
            MonitoringStatePresentation.Key(MonitoringSeverity.Ok),
            "Online",
            liveStatus.Message ?? "online",
            liveStatus.LastSeenUtc.ToLocalTime().ToString("dd.MM HH:mm:ss"));
    }

    private static double EstimateLoadScore(
        SensorUsageLevel usageLevel,
        double estimatedRunsPerDay,
        double? averageDurationMs,
        SensorState state)
    {
        if (state is SensorState.Paused or SensorState.Disabled)
        {
            return 0d;
        }

        var weight = SensorUsageCatalog.Weight(usageLevel);
        var durationWeight = averageDurationMs.HasValue
            ? Math.Max(averageDurationMs.Value / 1000d, 0.1d)
            : 0.25d;

        return weight * Math.Max(estimatedRunsPerDay, 0.1d) * durationWeight;
    }

    private static double EstimateExecutionsPerDay(MonitoringSettings settings, string? sensorTypeKey)
    {
        if (settings.PollingSchedule is { } schedule)
        {
            return schedule.Mode switch
            {
                MonitoringScheduleMode.Every => 86400d / Math.Max(schedule.EverySeconds ?? 1, 1),
                MonitoringScheduleMode.Daily => 1d,
                MonitoringScheduleMode.Weekly => 1d / 7d,
                MonitoringScheduleMode.Monthly => 1d / 30.4375d,
                _ => RunsPerDayFromInterval(SensorScheduleDefaults.Resolve(sensorTypeKey))
            };
        }

        if (settings.PollingInterval is TimeSpan interval && interval > TimeSpan.Zero)
        {
            return RunsPerDayFromInterval(interval);
        }

        // No explicit schedule: mirror the polling engine, which falls back to the per-type default.
        return RunsPerDayFromInterval(SensorScheduleDefaults.Resolve(sensorTypeKey));
    }

    private static double RunsPerDayFromInterval(TimeSpan interval) =>
        interval > TimeSpan.Zero ? TimeSpan.FromDays(1).TotalSeconds / interval.TotalSeconds : 96d;

    /// <summary>Approximate bytes for one stored statistics bucket row (incl. SQLite + index overhead).</summary>
    private const double StatisticsRowBytes = 120d;

    /// <summary>
    /// Steady-state on-disk footprint of a sensor once retention has caught up: raw samples kept
    /// for the raw-retention window plus the downsampled per-channel statistics for their window.
    /// </summary>
    private static double EstimateRetainedBytes(
        MonitoringSettings settings,
        string? sensorTypeKey,
        double estimatedBytesPerDay,
        SensorObservation? latestObservation)
    {
        var profile = SensorTelemetryProfiles.Resolve(sensorTypeKey);
        var rawDays = settings.ObservationRetentionDays ?? profile.RawObservationDays;
        var statsDays = settings.StatisticsRetentionDays ?? profile.StatisticsRetentionDays;
        var bucketMinutes = Math.Max(1, settings.StatisticsBucketMinutes ?? profile.StatisticsBucketMinutes);

        var rawBytes = estimatedBytesPerDay * Math.Max(0, rawDays);

        // One statistics row per logged (numeric, non-virtual) channel per bucket window.
        var loggedChannels = Math.Max(1, latestObservation?.Channels.Count(channel => !channel.IsVirtual && channel.Value.HasValue) ?? 1);
        var bucketsPerDay = 1440d / bucketMinutes;
        var statsBytes = loggedChannels * bucketsPerDay * StatisticsRowBytes * Math.Max(0, statsDays);

        return rawBytes + statsBytes;
    }

    private static double EstimateAverageObservationBytes(
        IReadOnlyList<SensorObservation> history,
        SensorObservation? latestObservation)
    {
        if (history.Count == 0)
        {
            return latestObservation is null
                ? 0d
                : EstimateObservationBytes(latestObservation);
        }

        var sampleSize = Math.Min(history.Count, 12);
        var sample = history.TakeLast(sampleSize).ToArray();
        return sample.Average(EstimateObservationBytes);
    }

    private static double EstimateObservationBytes(SensorObservation observation)
    {
        var json = JsonSerializer.Serialize(observation, JsonOptions);
        return Encoding.UTF8.GetByteCount(json);
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

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMilliseconds < 1000)
        {
            return $"{duration.TotalMilliseconds:0.#} ms";
        }

        if (duration.TotalSeconds < 60)
        {
            return $"{duration.TotalSeconds:0.#} s";
        }

        return duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<MonitoringElement> BuildLineage(
        MonitoringElement element,
        IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        var lineage = new List<MonitoringElement>();
        var current = element;

        while (true)
        {
            lineage.Add(current);

            if (current.ParentId is not Guid parentId || !elementsById.TryGetValue(parentId, out var parent))
            {
                break;
            }

            current = parent;
        }

        lineage.Reverse();
        return lineage;
    }

    private static IEnumerable<MonitoringElement> EnumerateDescendants(MonitoringContainerElement parent)
    {
        foreach (var child in parent.Children)
        {
            yield return child;

            if (child is MonitoringContainerElement container)
            {
                foreach (var descendant in EnumerateDescendants(container))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static string NormalizeViewMode(string? value)
    {
        return string.Equals(value?.Trim(), "table", StringComparison.OrdinalIgnoreCase) ? "table" : "grouped";
    }

    private static string NormalizeSearch(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static string NormalizeStateFilter(string? value)
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

    private static string NormalizeUsageFilter(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "high" => "high",
            "moderate" => "moderate",
            _ => "all"
        };
    }

    private static string NormalizeSortBy(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "name" => "name",
            "type" => "type",
            "state" => "state",
            "bytes" => "bytes",
            "log" => "log",
            _ => "load"
        };
    }

    private static bool MatchesFilters(
        ProbeUsageSensorRow row,
        string search,
        string stateFilter,
        string usageFilter,
        bool highlightedOnly)
    {
        if (highlightedOnly && !row.IsHighlighted)
        {
            return false;
        }

        if (!string.Equals(stateFilter, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.StateKey, stateFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(usageFilter, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.UsageLevelKey, usageFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(search) &&
            row.SearchText.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return true;
    }

    private static List<ProbeUsageSensorRow> SortSensorRows(List<ProbeUsageSensorRow> rows, string sortBy)
    {
        return sortBy switch
        {
            "name" => rows
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "type" => rows
                .OrderBy(row => row.SensorTypeLabel, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(row => row.LoadScore)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "state" => rows
                .OrderBy(row => GetStateSortRank(row.StateKey))
                .ThenByDescending(row => row.LoadScore)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "bytes" => rows
                .OrderByDescending(row => row.EstimatedBytesPerDayValue)
                .ThenByDescending(row => row.LoadScore)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "log" => rows
                .OrderByDescending(row => row.StoredObservationCount)
                .ThenByDescending(row => row.LoadScore)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => rows
                .OrderByDescending(row => row.LoadScore)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static int GetStateSortRank(string stateKey)
    {
        return stateKey.ToLowerInvariant() switch
        {
            "error" => 0,
            "warning" => 1,
            "paused" => 2,
            "ok" => 3,
            "unknown" => 4,
            "disabled" => 5,
            _ => 6
        };
    }

    private static IReadOnlyList<ProbeUsageGroupRow> BuildUsageGroups(
        IReadOnlyList<ProbeUsageSensorRow> sensorRows,
        double visibleLoadScore,
        StorageOverview storage)
    {
        return sensorRows
            .GroupBy(row => new { row.SensorTypeKey, row.SensorTypeLabel, row.UsageLevelKey, row.UsageLevelLabel })
            .Select(group =>
            {
                var orderedSensors = group
                    .OrderByDescending(sensor => sensor.LoadScore)
                    .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var groupLoad = group.Sum(sensor => sensor.LoadScore);
                var groupBytesPerHour = group.Sum(sensor => sensor.EstimatedBytesPerHourValue);
                var groupBytesPerDay = group.Sum(sensor => sensor.EstimatedBytesPerDayValue);
                var groupSamplesPerHour = group.Sum(sensor => sensor.SamplesPerHourValue);

                return new ProbeUsageGroupRow(
                    group.Key.SensorTypeKey,
                    group.Key.SensorTypeLabel,
                    group.Key.UsageLevelKey,
                    group.Key.UsageLevelLabel,
                    orderedSensors.Length,
                    groupSamplesPerHour,
                    storage.FormatBytes((long)Math.Round(groupBytesPerHour)),
                    storage.FormatBytes((long)Math.Round(groupBytesPerDay)),
                    visibleLoadScore <= 0 ? 0 : groupLoad / visibleLoadScore * 100d,
                    false,
                    orderedSensors);
            })
            .OrderByDescending(group => group.RelativeLoadPercent)
            .ThenBy(group => group.SensorTypeLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ProbeUsageDistributionSegmentRow> BuildDistributionSegments(
        string viewMode,
        IReadOnlyList<ProbeUsageGroupRow> groups,
        IReadOnlyList<ProbeUsageSensorRow> sensorRows)
    {
        const int maxSegments = 10;

        var source = string.Equals(viewMode, "table", StringComparison.OrdinalIgnoreCase)
            ? sensorRows
                .Where(row => row.RelativeLoadPercent > 0)
                .Select(row => new ProbeUsageDistributionSegmentRow(
                    row.Name,
                    row.UsageLevelKey,
                    row.RelativeLoadPercent,
                    $"{row.EstimatedBytesPerHourText}/h",
                    row.Path))
                .OrderByDescending(segment => segment.Percent)
                .ThenBy(segment => segment.Label, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : groups
                .Where(group => group.RelativeLoadPercent > 0)
                .Select(group => new ProbeUsageDistributionSegmentRow(
                    group.SensorTypeLabel,
                    group.UsageLevelKey,
                    group.RelativeLoadPercent,
                    $"{group.SensorCount} sensors",
                    $"{group.EstimatedBytesPerHourText}/h"))
                .OrderByDescending(segment => segment.Percent)
                .ThenBy(segment => segment.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (source.Count <= maxSegments)
        {
            return source.ToArray();
        }

        var visible = source.Take(maxSegments - 1).ToList();
        var hidden = source.Skip(maxSegments - 1).ToArray();
        visible.Add(new ProbeUsageDistributionSegmentRow(
            "Other",
            "moderate",
            hidden.Sum(segment => segment.Percent),
            $"{hidden.Length} items",
            null));
        return visible.ToArray();
    }

    private static string BuildFilterSummary(
        int visibleSensorCount,
        int totalSensorCount,
        int visibleGroupCount,
        int totalGroupCount,
        string viewMode,
        string search,
        string stateFilter,
        string usageFilter,
        bool highlightedOnly)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            parts.Add($"search \"{search}\"");
        }

        if (!string.Equals(stateFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"state {DescribeStateFilter(stateFilter)}");
        }

        if (!string.Equals(usageFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"usage {DescribeUsageFilter(usageFilter)}");
        }

        if (highlightedOnly)
        {
            parts.Add("highlighted only");
        }

        var baseSummary = $"Showing {visibleSensorCount}/{totalSensorCount} sensors in {visibleGroupCount}/{totalGroupCount} groups";
        var modeLabel = string.Equals(viewMode, "table", StringComparison.OrdinalIgnoreCase) ? "table view" : "grouped view";
        return parts.Count == 0
            ? $"{baseSummary} | {modeLabel}"
            : $"{baseSummary} | {modeLabel} | {string.Join(" | ", parts)}";
    }

    private static string BuildDistributionLabel(string viewMode)
    {
        return string.Equals(viewMode, "table", StringComparison.OrdinalIgnoreCase)
            ? "individual sensor load"
            : "group load by sensor type";
    }

    private static string DescribeStateFilter(string stateFilter)
    {
        return stateFilter switch
        {
            "ok" => "OK",
            "warning" => "Warning",
            "error" => "Error",
            "paused" => "Paused",
            "unknown" => "Unknown",
            "disabled" => "Disabled",
            _ => "All"
        };
    }

    private static string DescribeUsageFilter(string usageFilter)
    {
        return usageFilter switch
        {
            "low" => "Low",
            "moderate" => "Moderate",
            "high" => "High",
            _ => "All"
        };
    }
}

public sealed record ProbeUsageViewModel(
    Guid ProbeElementId,
    string Name,
    string Path,
    string ProbeId,
    string RoleLabel,
    string StatusKey,
    string StatusLabel,
    string StatusMessage,
    string LastSeenText,
    string DataSizeText,
    string StorageFreeText,
    string EstimatedBytesPerSecondText,
    string EstimatedBytesPerHourText,
    string EstimatedBytesPerDayText,
    string EstimatedProbeSizeText,
    string StatusColor,
    int SensorCount,
    int GroupCount,
    int TotalSensorCount,
    int TotalGroupCount,
    int HealthySensorCount,
    int WarningSensorCount,
    int ErrorSensorCount,
    int PausedSensorCount,
    string ViewMode,
    string Search,
    string StateFilter,
    string UsageFilter,
    string SortBy,
    bool HighlightedOnly,
    bool FiltersActive,
    string FilterSummary,
    string DistributionLabel,
    IReadOnlyList<ProbeUsageDistributionSegmentRow> DistributionSegments,
    IReadOnlyList<ProbeUsageGroupRow> Groups,
    IReadOnlyList<ProbeUsageSensorRow> Sensors,
    IReadOnlyList<ProbeUsageSensorRow> TopLogSensors,
    string TotalStoredObservationCountText);

public sealed record ProbeUsageStatus(
    string StateKey,
    string StateLabel,
    string Message,
    string LastSeenText);

public sealed record ProbeUsageGroupRow(
    string SensorTypeKey,
    string SensorTypeLabel,
    string UsageLevelKey,
    string UsageLevelLabel,
    int SensorCount,
    int SamplesPerHour,
    string EstimatedBytesPerHourText,
    string EstimatedBytesPerDayText,
    double RelativeLoadPercent,
    bool IsExpanded,
    IReadOnlyList<ProbeUsageSensorRow> Sensors);

public sealed record ProbeUsageSensorRow(
    Guid SensorId,
    string Name,
    string Path,
    string Target,
    string MetaSummary,
    string SensorTypeKey,
    string SensorTypeLabel,
    string UsageLevelKey,
    string UsageLevelLabel,
    string StateKey,
    string StateLabel,
    string? StateMessage,
    bool IsPaused,
    bool IsHighlighted,
    string ScheduleSummary,
    string LastSeenText,
    string SamplesPerHourText,
    string AverageDurationText,
    double EstimatedBytesPerHourValue,
    double EstimatedBytesPerDayValue,
    string EstimatedBytesPerHourText,
    string EstimatedBytesPerDayText,
    double RelativeLoadPercent,
    double LoadScore,
    string SearchText,
    int StoredObservationCount,
    string StoredObservationCountText,
    double RelativeLogPercent = 0d)
{
    public int SamplesPerHourValue => int.TryParse(SamplesPerHourText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : 0;
}

public sealed record ProbeUsageDistributionSegmentRow(
    string Label,
    string UsageLevelKey,
    double Percent,
    string DetailText,
    string? SubText);
