using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

[Authorize]
public sealed class SensorDetailsModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly ISensorExecutionService _sensorExecutionService;
    private readonly MonitoringInheritanceResolver _resolver = new();

    public SensorDetailsModel(
        IMonitoringWorkspaceStore workspaceStore,
        ISensorExecutionService sensorExecutionService)
    {
        _workspaceStore = workspaceStore;
        _sensorExecutionService = sensorExecutionService;
    }

    [BindProperty(SupportsGet = true)]
    public Guid SensorId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Window { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? StatsFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? StatsTo { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? StatsGroup { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? StatsRange { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? StatsChannel { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string CurrentUrl => $"{Request.Path}{Request.QueryString}";

    public SensorDetailsViewModel View { get; private set; } = default!;

    public IActionResult OnGet()
    {
        return LoadView() ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostRunNowAsync()
    {
        try
        {
            var result = await _sensorExecutionService.ExecuteNowAsync(SensorId, cancellationToken: HttpContext.RequestAborted);
            StatusMessage = $"Run now: {MonitoringStatePresentation.Label(result.State)} - check {result.Duration.TotalMilliseconds:0.#} ms"
                + (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $" - {result.Message}");
            return RedirectToPage(new { sensorId = SensorId, window = NormalizeWindowKey(Window) });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return LoadView() ? Page() : NotFound();
        }
    }

    public IActionResult OnPostToggleSensorPause()
    {
        try
        {
            var sensor = _workspaceStore.FindElement(SensorId) as SensorElement
                ?? throw new InvalidOperationException("Selected element is not a sensor.");

            var paused = !sensor.IsPaused;
            if (!_workspaceStore.SetSensorPaused(sensor.Id, paused))
            {
                throw new InvalidOperationException("Sensor could not be updated.");
            }

            StatusMessage = paused ? $"Sensor '{sensor.Name}' paused." : $"Sensor '{sensor.Name}' resumed.";
            return RedirectToPage(new { sensorId = SensorId, window = NormalizeWindowKey(Window) });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return LoadView() ? Page() : NotFound();
        }
    }

    private bool LoadView()
    {
        Window = NormalizeWindowKey(Window);

        var workspace = _workspaceStore.Workspace;
        var sensor = _workspaceStore.FindElement(SensorId) as SensorElement;
        if (sensor is null)
        {
            return false;
        }

        var elementsById = _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
        var lineage = BuildLineage(sensor, elementsById);
        var templateMap = workspace.Templates.ToDictionary(template => template.Id);
        var effectiveSettings = _resolver.Resolve(lineage, templateMap);
        var sensorDefinition = workspace.SensorDefinitions.FirstOrDefault(def =>
            string.Equals(def.Key, sensor.SensorTypeKey, StringComparison.OrdinalIgnoreCase));
        var usageLevel = sensorDefinition?.UsageLevel ?? SensorUsageCatalog.Resolve(sensor.SensorTypeKey);
        var history = _workspaceStore.GetSensorHistory(sensor.Id, TimeSpan.FromDays(7), maxCount: 5000);
        var latestObservation = history.LastOrDefault();
        var defaultChannelKey = effectiveSettings.DefaultChannelKey;
        var defaultChannel = SensorHistoryAnalytics.GetDefaultChannel(latestObservation, defaultChannelKey);
        var currentValue = SensorHistoryAnalytics.GetDefaultValue(latestObservation, defaultChannelKey);
        var fallbackUnit = GetFallbackUnit(sensor.SensorTypeKey);
        var rawUnit = defaultChannel?.Unit ?? fallbackUnit;
        var scaleReferenceValue = GetScaleReferenceValue(currentValue, history, defaultChannelKey);
        var measurementKind = defaultChannel?.MeasurementKind ?? SensorUnitConverter.GuessMeasurementKind(rawUnit);
        var displayScale = SensorUnitConverter.CreateScale(scaleReferenceValue, rawUnit, measurementKind);
        var currentDisplay = SensorUnitConverter.Format(currentValue, displayScale, measurementKind);
        var unit = currentDisplay.Unit;
        var currentState = sensor.IsPaused ? SensorState.Paused : latestObservation?.State ?? SensorState.Unknown;
        var currentStateKey = sensor.IsPaused
            ? MonitoringStatePresentation.PausedKey
            : MonitoringStatePresentation.Key(currentState);
        var currentStateLabel = sensor.IsPaused
            ? MonitoringStatePresentation.PausedLabel
            : MonitoringStatePresentation.Label(currentState);
        var currentStateColor = sensor.IsPaused
            ? MonitoringStatePresentation.PausedColor
            : MonitoringStatePresentation.Color(currentState);
        var currentMessage = sensor.IsPaused
            ? "sensor paused"
            : latestObservation?.Message ?? BuildDefaultMessage(sensor.SensorTypeKey, currentValue, currentState, displayScale);
        var axisMin = effectiveSettings.GraphMinValue.HasValue ? displayScale.Convert(effectiveSettings.GraphMinValue.Value) : (double?)null;
        var axisMax = effectiveSettings.GraphMaxValue.HasValue ? displayScale.Convert(effectiveSettings.GraphMaxValue.Value) : (double?)null;
        var windows = BuildWindows(history, currentStateColor, defaultChannelKey, displayScale, axisMin, axisMax).ToArray();
        var selectedWindow = windows.FirstOrDefault(window => string.Equals(window.Key, Window, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(window => string.Equals(window.Key, "1d", StringComparison.OrdinalIgnoreCase))
            ?? windows[0];
        var ownTags = MonitoringTagResolver.Normalize(sensor.Tags);
        var tagChips = MonitoringTagResolver.ResolveEffective(lineage)
            .Select(tag => new SensorTagChip(tag, !MonitoringTagResolver.HasTag(ownTags, tag)))
            .ToArray();
        var defaultChannelLabel = defaultChannel is null
            ? (string.IsNullOrWhiteSpace(defaultChannelKey) ? "Default" : HumanizeChannelKey(defaultChannelKey))
            : string.IsNullOrWhiteSpace(defaultChannel.Label) ? defaultChannel.Key : defaultChannel.Label;
        var executionProbe = FormatExecutionProbe(latestObservation);
        var statisticsBuckets = _workspaceStore.GetSensorStatistics(sensor.Id);
        var statisticsSummary = BuildChannelStatistics(
            statisticsBuckets, latestObservation, defaultChannelKey, defaultChannelLabel, displayScale, measurementKind);
        var unitConversion = BuildUnitConversion(rawUnit, displayScale, measurementKind, currentValue ?? scaleReferenceValue);
        var isAcknowledged = workspace.Alerts
            .Any(alert => alert.IsActive && alert.IsAcknowledged && alert.ElementId == sensor.Id);

        View = new SensorDetailsViewModel(
            sensor.Id,
            sensor.Name,
            string.Join(" / ", lineage.Select(item => item.Name)),
            sensorDefinition?.DisplayName ?? sensor.SensorTypeKey,
            sensor.SensorTypeKey,
            SensorUsageCatalog.Key(usageLevel),
            SensorUsageCatalog.Label(usageLevel),
            SensorTargetResolver.Resolve(sensor, lineage),
            effectiveSettings.Summary(),
            currentStateKey,
            currentStateLabel,
            currentStateColor,
            currentMessage,
            sensor.IsPaused,
            isAcknowledged,
            executionProbe,
            defaultChannelLabel,
            unit,
            currentValue.HasValue ? currentDisplay.Text : "-",
            latestObservation is null ? null : latestObservation.TimestampUtc.ToDisplay().ToString("dd.MM HH:mm:ss"),
            latestObservation is null ? null : FormatDuration(latestObservation.Duration),
            windows,
            selectedWindow,
            BuildChannelRows(latestObservation, fallbackUnit, defaultChannelKey, effectiveSettings),
            BuildRecentObservationRows(history, fallbackUnit, defaultChannelKey, displayScale),
            statisticsSummary,
            unitConversion,
            tagChips);

        return true;
    }

    private static readonly string[] StatisticsGroups = ["native", "day", "month", "year"];
    private static readonly string[] StatisticsRanges = ["24h", "7d", "30d", "90d", "all"];
    private const string DefaultStatisticsRange = "7d";

    private static string NormalizeStatisticsGroup(string? group)
    {
        var value = group?.Trim().ToLowerInvariant();
        return StatisticsGroups.Contains(value) ? value! : "native";
    }

    private static string NormalizeStatisticsRange(string? range)
    {
        var value = range?.Trim().ToLowerInvariant();
        return StatisticsRanges.Contains(value) ? value! : DefaultStatisticsRange;
    }

    private static (DateTime? From, DateTime? To) ResolveRelativeRange(string range, DateTime now) => range switch
    {
        "24h" => (now.AddHours(-24), now),
        "7d" => (now.AddDays(-7), now),
        "30d" => (now.AddDays(-30), now),
        "90d" => (now.AddDays(-90), now),
        _ => (null, null) // all time
    };

    // A coarser default grain per range keeps the bar count sane (24h → raw buckets,
    // a week/month → daily, a quarter → monthly). An explicit group always wins.
    private static string DefaultGroupForRange(string range) => range switch
    {
        "7d" or "30d" => "day",
        "90d" => "month",
        _ => "native"
    };

    private static string NormalizeChannelKey(string? key)
        => string.IsNullOrWhiteSpace(key) ? "default" : key.Trim();

    // Statistics are stored per channel. Picks the channel to show (the requested
    // one, else the primary, else the channel with the most history), derives that
    // channel's unit scale, and lists the available channels for the selector.
    private SensorStatisticsSummary? BuildChannelStatistics(
        IReadOnlyList<SensorStatisticsBucket> buckets,
        SensorObservation? latestObservation,
        string? primaryChannelKey,
        string defaultChannelLabel,
        SensorUnitScale primaryScale,
        SensorMeasurementKind primaryKind)
    {
        if (buckets.Count == 0)
        {
            return null;
        }

        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (latestObservation is not null)
        {
            foreach (var channel in latestObservation.Channels)
            {
                labels[NormalizeChannelKey(channel.Key)] = string.IsNullOrWhiteSpace(channel.Label) ? channel.Key : channel.Label;
            }
        }

        // Channels that actually carry statistics, busiest first (the primary
        // channel has the longest history, so it naturally leads).
        var channelKeys = buckets
            .GroupBy(bucket => NormalizeChannelKey(bucket.DefaultChannelKey))
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .ToList();

        var primaryKey = NormalizeChannelKey(primaryChannelKey);
        var requestedKey = string.IsNullOrWhiteSpace(StatsChannel) ? null : NormalizeChannelKey(StatsChannel);

        var selectedKey = channelKeys.FirstOrDefault(key =>
                requestedKey is not null && string.Equals(key, requestedKey, StringComparison.OrdinalIgnoreCase))
            ?? channelKeys.FirstOrDefault(key => string.Equals(key, primaryKey, StringComparison.OrdinalIgnoreCase))
            ?? channelKeys[0];

        var channelBuckets = buckets
            .Where(bucket => string.Equals(NormalizeChannelKey(bucket.DefaultChannelKey), selectedKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        SensorUnitScale scale;
        SensorMeasurementKind kind;
        if (string.Equals(selectedKey, primaryKey, StringComparison.OrdinalIgnoreCase))
        {
            // The primary channel keeps the same scale as the overview/graph.
            scale = primaryScale;
            kind = primaryKind;
        }
        else
        {
            var unit = channelBuckets.Select(bucket => bucket.Unit).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            kind = SensorUnitConverter.GuessMeasurementKind(unit);
            var reference = channelBuckets
                .Select(bucket => bucket.Maximum ?? bucket.Average)
                .Where(value => value.HasValue)
                .Select(value => Math.Abs(value!.Value))
                .DefaultIfEmpty(0d)
                .Max();
            scale = SensorUnitConverter.CreateScale(reference, unit, kind);
        }

        string LabelFor(string key) =>
            string.Equals(key, primaryKey, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(defaultChannelLabel)
                ? defaultChannelLabel
                : labels.TryGetValue(key, out var label) ? label : HumanizeChannelKey(key);

        var channelOptions = channelKeys
            .Select(key => new SensorStatisticsChannelOption(key, LabelFor(key), string.Equals(key, selectedKey, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return BuildStatisticsSummary(channelBuckets, scale, kind, StatsGroup, StatsRange, StatsFrom, StatsTo, channelOptions, selectedKey);
    }

    private static SensorStatisticsSummary? BuildStatisticsSummary(
        IReadOnlyList<SensorStatisticsBucket> buckets,
        SensorUnitScale scale,
        SensorMeasurementKind kind,
        string? group,
        string? range,
        string? fromText,
        string? toText,
        IReadOnlyList<SensorStatisticsChannelOption> channels,
        string selectedChannelKey)
    {
        if (buckets.Count == 0)
        {
            return null;
        }

        var bucketMinutes = buckets[^1].BucketMinutes;

        // An explicit From/To is a custom range and wins; otherwise a relative range
        // (default: last 7 days) is resolved from "now".
        var explicitFrom = TryParseLocalDate(fromText, endOfDay: false);
        var explicitTo = TryParseLocalDate(toText, endOfDay: true);
        var isCustom = explicitFrom is not null || explicitTo is not null;

        string selectedRange;
        DateTime? fromLocal;
        DateTime? toLocal;
        if (isCustom)
        {
            selectedRange = "custom";
            fromLocal = explicitFrom;
            toLocal = explicitTo;
        }
        else
        {
            selectedRange = NormalizeStatisticsRange(range);
            (fromLocal, toLocal) = ResolveRelativeRange(selectedRange, DateTime.Now);
        }

        var selectedGroup = !string.IsNullOrWhiteSpace(group)
            ? NormalizeStatisticsGroup(group)
            : (isCustom ? "native" : DefaultGroupForRange(selectedRange));

        var filtered = buckets
            .Where(bucket =>
            {
                var local = bucket.BucketStartUtc.ToLocalTime().DateTime;
                if (fromLocal is { } f && local < f)
                {
                    return false;
                }

                if (toLocal is { } t && local > t)
                {
                    return false;
                }

                return true;
            })
            .ToList();

        var matchedBuckets = filtered.Count;

        // Re-group the stored buckets into the chosen calendar grain and aggregate.
        var grouped = filtered
            .GroupBy(bucket => StatisticsPeriodStart(bucket.BucketStartUtc, selectedGroup, bucketMinutes))
            .Select(grp => AggregateStatisticsGroup(grp.Key, grp.ToList(), selectedGroup, bucketMinutes, scale, kind))
            .OrderByDescending(row => row.PeriodEpochMs)
            .Take(500)
            .ToArray();

        var unit = string.IsNullOrWhiteSpace(scale.Unit) ? null : scale.Unit;
        var granularityLabel = selectedGroup switch
        {
            "day" => "Daily",
            "month" => "Monthly",
            "year" => "Yearly",
            _ => DescribeGranularity(bucketMinutes)
        };

        return new SensorStatisticsSummary(
            granularityLabel,
            matchedBuckets,
            unit,
            grouped,
            selectedGroup,
            selectedRange,
            // Only echo dates into the custom From/To pickers for a custom range; relative
            // ranges leave them empty so the active range chip stays the source of truth.
            isCustom ? fromLocal?.ToString("yyyy-MM-dd") : null,
            isCustom ? toLocal?.ToString("yyyy-MM-dd") : null,
            channels,
            selectedChannelKey);
    }

    private static DateTime? TryParseLocalDate(string? text, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        var date = parsed.Date;
        return endOfDay ? date.AddDays(1).AddTicks(-1) : date;
    }

    private static DateTimeOffset StatisticsPeriodStart(DateTimeOffset bucketStartUtc, string group, int bucketMinutes)
    {
        var local = bucketStartUtc.ToLocalTime();
        var date = local.DateTime;
        var start = group switch
        {
            "day" => new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified),
            "month" => new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Unspecified),
            "year" => new DateTime(date.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            _ => date // native: each bucket keeps its own start
        };

        return new DateTimeOffset(start, local.Offset);
    }

    private static SensorStatisticsRow AggregateStatisticsGroup(
        DateTimeOffset periodStart,
        IReadOnlyList<SensorStatisticsBucket> members,
        string group,
        int bucketMinutes,
        SensorUnitScale scale,
        SensorMeasurementKind kind)
    {
        var label = group switch
        {
            "day" => periodStart.ToString("ddd dd.MM.yyyy", CultureInfo.InvariantCulture),
            "month" => periodStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            "year" => periodStart.ToString("yyyy", CultureInfo.InvariantCulture),
            _ => FormatBucketPeriod(periodStart, bucketMinutes)
        };

        var sampleCount = members.Sum(m => m.SampleCount);
        var weight = members.Where(m => m.SampleCount > 0).Sum(m => m.SampleCount);

        double? WeightedAverage(Func<SensorStatisticsBucket, double?> selector)
        {
            var contributing = members.Where(m => selector(m).HasValue && m.SampleCount > 0).ToList();
            if (contributing.Count == 0)
            {
                var anyValue = members.Select(selector).FirstOrDefault(v => v.HasValue);
                return anyValue;
            }

            var totalWeight = contributing.Sum(m => m.SampleCount);
            return totalWeight == 0
                ? contributing.Average(m => selector(m)!.Value)
                : contributing.Sum(m => selector(m)!.Value * m.SampleCount) / totalWeight;
        }

        var average = WeightedAverage(m => m.Average);
        var minimum = members.Where(m => m.Minimum.HasValue).Select(m => m.Minimum!.Value).DefaultIfEmpty().Min();
        var hasMin = members.Any(m => m.Minimum.HasValue);
        var maximum = members.Where(m => m.Maximum.HasValue).Select(m => m.Maximum!.Value).DefaultIfEmpty().Max();
        var hasMax = members.Any(m => m.Maximum.HasValue);
        var lowPercentile = WeightedAverage(m => m.LowPercentile);
        var highPercentile = WeightedAverage(m => m.HighPercentile);

        var healthy = members.Sum(m => m.HealthyCount);
        var warning = members.Sum(m => m.WarningCount);
        var critical = members.Sum(m => m.CriticalCount);
        var stateSamples = healthy + warning + critical;
        string? uptimeText = stateSamples > 0
            ? $"{(healthy / (double)stateSamples * 100d).ToString("0.#", CultureInfo.InvariantCulture)} %"
            : null;

        var state = critical > 0
            ? SensorState.Critical
            : warning > 0
                ? SensorState.Warning
                : healthy > 0
                    ? SensorState.Healthy
                    : members[0].State;

        // Clicking a row drills one calendar grain finer, scoped to that period.
        string? drillFrom = null, drillTo = null, drillGroup = null;
        var day = periodStart.Date;
        switch (group)
        {
            case "year":
                drillFrom = new DateTime(day.Year, 1, 1).ToString("yyyy-MM-dd");
                drillTo = new DateTime(day.Year, 12, 31).ToString("yyyy-MM-dd");
                drillGroup = "month";
                break;
            case "month":
                var first = new DateTime(day.Year, day.Month, 1);
                drillFrom = first.ToString("yyyy-MM-dd");
                drillTo = first.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");
                drillGroup = "day";
                break;
            case "day":
                drillFrom = day.ToString("yyyy-MM-dd");
                drillTo = drillFrom;
                drillGroup = "native";
                break;
        }

        var averageValue = average.HasValue ? scale.Convert(average.Value) : (double?)null;

        return new SensorStatisticsRow(
            label,
            periodStart.ToUnixTimeMilliseconds(),
            FormatStat(average, scale, kind),
            FormatStat(hasMin ? minimum : null, scale, kind),
            FormatStat(hasMax ? maximum : null, scale, kind),
            FormatStat(lowPercentile, scale, kind),
            FormatStat(highPercentile, scale, kind),
            sampleCount,
            uptimeText,
            MonitoringStatePresentation.Key(state),
            averageValue,
            drillFrom,
            drillTo,
            drillGroup);
    }

    private static SensorUnitConversion? BuildUnitConversion(
        string rawUnit,
        SensorUnitScale scale,
        SensorMeasurementKind kind,
        double? sampleValue)
    {
        var normalizedRaw = SensorUnitConverter.NormalizeUnit(rawUnit);
        var sameUnit = string.Equals(normalizedRaw, scale.Unit, StringComparison.OrdinalIgnoreCase);
        var unitFactor = Math.Abs(scale.Factor - 1d) < 1e-9;
        if (sameUnit && unitFactor)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(normalizedRaw) && string.IsNullOrWhiteSpace(scale.Unit))
        {
            return null;
        }

        string? example = null;
        if (sampleValue is double value && Math.Abs(value) > double.Epsilon)
        {
            var rawText = value.ToString("0.###", CultureInfo.InvariantCulture);
            var rawCombined = string.IsNullOrWhiteSpace(normalizedRaw) ? rawText : $"{rawText} {normalizedRaw}";
            var display = SensorUnitConverter.Format(value, scale, kind).CombinedText;
            example = $"{rawCombined} = {display}";
        }

        return new SensorUnitConversion(
            string.IsNullOrWhiteSpace(normalizedRaw) ? "raw" : normalizedRaw,
            string.IsNullOrWhiteSpace(scale.Unit) ? "raw" : scale.Unit,
            example);
    }

    private static string FormatStat(double? value, SensorUnitScale scale, SensorMeasurementKind kind)
    {
        return value.HasValue ? SensorUnitConverter.Format(value, scale, kind).Text : "-";
    }

    private static string FormatBucketPeriod(DateTimeOffset bucketStartUtc, int bucketMinutes)
    {
        var local = bucketStartUtc.ToLocalTime();
        return bucketMinutes >= 1440 ? local.ToString("dd.MM") : local.ToString("dd.MM HH:mm");
    }

    private static string DescribeGranularity(int minutes) => minutes switch
    {
        <= 0 => "raw",
        60 => "Hourly",
        360 => "6-hourly",
        720 => "12-hourly",
        1440 => "Daily",
        < 60 => $"{minutes}-minute",
        _ when minutes % 1440 == 0 => $"{minutes / 1440}-day",
        _ when minutes % 60 == 0 => $"{minutes / 60}-hour",
        _ => $"{minutes}-minute"
    };

    private static IEnumerable<SensorWindowStatistics> BuildWindows(
        IReadOnlyList<SensorObservation> observations,
        string lineColor,
        string? defaultChannelKey,
        SensorUnitScale? scale = null,
        double? axisMin = null,
        double? axisMax = null)
    {
        var now = DateTimeOffset.UtcNow;
        yield return SensorHistoryAnalytics.BuildWindowStatistics(observations, "1h", "1h", TimeSpan.FromHours(1), now, lineColor, defaultChannelKey, scale, axisMin: axisMin, axisMax: axisMax);
        yield return SensorHistoryAnalytics.BuildWindowStatistics(observations, "1d", "1D", TimeSpan.FromDays(1), now, lineColor, defaultChannelKey, scale, axisMin: axisMin, axisMax: axisMax);
        yield return SensorHistoryAnalytics.BuildWindowStatistics(observations, "1w", "1W", TimeSpan.FromDays(7), now, lineColor, defaultChannelKey, scale, axisMin: axisMin, axisMax: axisMax);
    }

    private static IReadOnlyList<SensorChannelRow> BuildChannelRows(SensorObservation? latestObservation, string fallbackUnit, string? defaultChannelKey, MonitoringSettings settings)
    {
        if (latestObservation is null)
        {
            return [];
        }

        var defaultChannel = SensorHistoryAnalytics.GetDefaultChannel(latestObservation, defaultChannelKey);
        if (latestObservation.Channels.Count == 0)
        {
            if (!latestObservation.Value.HasValue)
            {
                return [];
            }

            var state = latestObservation.State;
            var (visualKey, fillPercent) = ResolveChannelVisual(latestObservation.Value, fallbackUnit, SensorMeasurementKind.Unknown, MonitoringSettings.GetChannelVisual(settings, "default"));
            return
            [
                new SensorChannelRow(
                    "default",
                    "Default",
                    SensorUnitConverter.Format(latestObservation.Value, fallbackUnit).Text,
                    SensorUnitConverter.Format(latestObservation.Value, fallbackUnit).Unit,
                    MonitoringStatePresentation.Key(state),
                    MonitoringStatePresentation.Label(state),
                    latestObservation.Message,
                    true,
                    visualKey,
                    fillPercent)
            ];
        }

        return latestObservation.Channels
            .Select(channel =>
            {
                var state = channel.State ?? SensorState.Healthy;
                var isDefault = defaultChannel is not null
                    ? string.Equals(channel.Key, defaultChannel.Key, StringComparison.OrdinalIgnoreCase)
                    : channel.IsDefault;
                var (visualKey, fillPercent) = ResolveChannelVisual(channel.Value, channel.Unit, channel.MeasurementKind, MonitoringSettings.GetChannelVisual(settings, channel.Key));

                return new SensorChannelRow(
                    channel.Key,
                    string.IsNullOrWhiteSpace(channel.Label) ? channel.Key : channel.Label,
                    SensorUnitConverter.Format(channel.Value, channel.Unit, channel.MeasurementKind).Text,
                    SensorUnitConverter.Format(channel.Value, channel.Unit, channel.MeasurementKind).Unit,
                    MonitoringStatePresentation.Key(state),
                    MonitoringStatePresentation.Label(state),
                    channel.Message ?? latestObservation.Message,
                    isDefault,
                    visualKey,
                    fillPercent);
            })
            .ToArray();
    }

    /// <summary>
    /// Picks a per-channel visual from the measurement kind: percentages render as
    /// a progress meter (0–100), booleans as an on/off badge, everything else as a
    /// plain value. The fill percent is only meaningful for the progress visual.
    /// </summary>
    private static (string VisualKey, double? FillPercent) ResolveChannelVisual(
        double? value,
        string? unit,
        SensorMeasurementKind kind,
        string? configuredVisual = null)
    {
        var resolved = kind != SensorMeasurementKind.Unknown
            ? kind
            : SensorUnitConverter.GuessMeasurementKind(unit);

        var configured = string.IsNullOrWhiteSpace(configuredVisual) ? "auto" : configuredVisual.Trim().ToLowerInvariant();

        // "auto" derives the visual from the measurement kind (as before).
        if (configured == "auto")
        {
            return resolved switch
            {
                SensorMeasurementKind.Percent when value.HasValue => ("progress", Math.Clamp(value.Value, 0d, 100d)),
                SensorMeasurementKind.Boolean when value.HasValue => ("boolean", value.Value >= 0.5d ? 100d : 0d),
                _ => ("value", null)
            };
        }

        // Explicit override. Progress/Gauge need a 0–100 fill, which we only know for
        // percentages; otherwise fall back to a plain value. "graph" stays in the main
        // chart, so a per-channel row shows the value.
        var percentFill = resolved == SensorMeasurementKind.Percent && value.HasValue
            ? Math.Clamp(value.Value, 0d, 100d)
            : (double?)null;

        return configured switch
        {
            "progress" when percentFill.HasValue => ("progress", percentFill),
            "gauge" when percentFill.HasValue => ("gauge", percentFill),
            "boolean" when value.HasValue => ("boolean", value.Value >= 0.5d ? 100d : 0d),
            _ => ("value", null)
        };
    }

    private static IReadOnlyList<SensorObservationRow> BuildRecentObservationRows(
        IReadOnlyList<SensorObservation> history,
        string fallbackUnit,
        string? defaultChannelKey,
        SensorUnitScale? scale = null)
    {
        var displayScale = scale ?? SensorUnitScale.Identity(fallbackUnit);

        return history
            .TakeLast(12)
            .Select(observation =>
            {
                var defaultValue = SensorHistoryAnalytics.GetDefaultValue(observation, defaultChannelKey);
                var channelCount = observation.Channels.Count > 0
                    ? observation.Channels.Count(channel => !channel.IsVirtual)
                    : observation.Value.HasValue ? 1 : 0;

                var display = SensorUnitConverter.Format(defaultValue, displayScale);
                return new SensorObservationRow(
                    observation.TimestampUtc.ToDisplay().ToString("dd.MM HH:mm:ss"),
                    MonitoringStatePresentation.Key(observation.State),
                    MonitoringStatePresentation.Label(observation.State),
                    defaultValue.HasValue ? display.Text : "-",
                    display.Unit,
                    FormatDuration(observation.Duration),
                    observation.Message,
                    FormatExecutionProbe(observation),
                    channelCount);
            })
            .ToArray();
    }

    private static double? GetScaleReferenceValue(
        double? currentValue,
        IReadOnlyList<SensorObservation> history,
        string? defaultChannelKey)
    {
        var values = new List<double>();

        if (currentValue.HasValue)
        {
            values.Add(Math.Abs(currentValue.Value));
        }

        values.AddRange(history
            .Select(observation => SensorHistoryAnalytics.GetDefaultValue(observation, defaultChannelKey))
            .Where(value => value.HasValue)
            .Select(value => Math.Abs(value!.Value)));

        return values.Count == 0 ? null : values.Max();
    }

    private static string? FormatExecutionProbe(SensorObservation? observation)
    {
        if (observation is null ||
            (string.IsNullOrWhiteSpace(observation.ExecutedByProbeId) &&
             string.IsNullOrWhiteSpace(observation.ExecutedByProbeName)))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(observation.ExecutedByProbeName))
        {
            return observation.ExecutedByProbeId;
        }

        if (string.IsNullOrWhiteSpace(observation.ExecutedByProbeId))
        {
            return observation.ExecutedByProbeName;
        }

        return $"{observation.ExecutedByProbeName} ({observation.ExecutedByProbeId})";
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

    private static string NormalizeWindowKey(string? window)
    {
        return window?.Trim().ToLowerInvariant() switch
        {
            "1h" => "1h",
            "1d" => "1d",
            "1w" => "1w",
            _ => "1d"
        };
    }

    private static string BuildDefaultMessage(string sensorTypeKey, double? value, SensorState state, SensorUnitScale scale)
    {
        if (state == SensorState.Disabled)
        {
            return "sensor disabled";
        }

        if (!value.HasValue)
        {
            if (string.Equals(sensorTypeKey, "probe-heartbeat", StringComparison.OrdinalIgnoreCase))
            {
                return state == SensorState.Critical
                    ? "no heartbeat received"
                    : "heartbeat pending";
            }

            return state == SensorState.Critical ? "measurement failed" : "no measurements yet";
        }

        var display = SensorUnitConverter.Format(value, scale);
        return string.IsNullOrWhiteSpace(display.Unit)
            ? $"value {display.Text}"
            : $"value {display.Text} {display.Unit}";
    }

    private static string GetFallbackUnit(string sensorTypeKey)
    {
        return sensorTypeKey.ToLowerInvariant() switch
        {
            "ping" => "ms",
            "http" => "ms",
            "snmp" => string.Empty,
            "probe-heartbeat" => "s",
            "powershell" => string.Empty,
            _ => string.Empty
        };
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

    private static string FormatValue(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "-";
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
}

public sealed record SensorDetailsViewModel(
    Guid SensorId,
    string Name,
    string Path,
    string SensorTypeLabel,
    string SensorTypeKey,
    string UsageLevelKey,
    string UsageLevelLabel,
    string Target,
    string SettingsSummary,
    string StateKey,
    string StateLabel,
    string StateColor,
    string? StateMessage,
    bool IsPaused,
    bool IsAcknowledged,
    string? ExecutionProbe,
    string DefaultChannelLabel,
    string Unit,
    string CurrentValueText,
    string? LastSeenText,
    string? LastDurationText,
    IReadOnlyList<SensorWindowStatistics> Windows,
    SensorWindowStatistics SelectedWindow,
    IReadOnlyList<SensorChannelRow> Channels,
    IReadOnlyList<SensorObservationRow> RecentObservations,
    SensorStatisticsSummary? Statistics,
    SensorUnitConversion? UnitConversion,
    IReadOnlyList<SensorTagChip> Tags);

public sealed record SensorTagChip(string Text, bool Inherited);

public sealed record SensorStatisticsSummary(
    string GranularityLabel,
    int BucketCount,
    string? Unit,
    IReadOnlyList<SensorStatisticsRow> Rows,
    string SelectedGroup,
    string SelectedRange,
    string? FromText,
    string? ToText,
    IReadOnlyList<SensorStatisticsChannelOption> Channels,
    string SelectedChannelKey);

public sealed record SensorStatisticsChannelOption(
    string Key,
    string Label,
    bool IsSelected);

public sealed record SensorStatisticsRow(
    string PeriodText,
    long PeriodEpochMs,
    string AverageText,
    string MinimumText,
    string MaximumText,
    string LowPercentileText,
    string HighPercentileText,
    int SampleCount,
    string? UptimeText,
    string StateKey,
    double? AverageValue = null,
    string? DrillFrom = null,
    string? DrillTo = null,
    string? DrillGroup = null);

public sealed record SensorUnitConversion(
    string RawUnit,
    string DisplayUnit,
    string? Example);

public sealed record SensorChannelRow(
    string Key,
    string Label,
    string ValueText,
    string Unit,
    string StateKey,
    string StateLabel,
    string? Message,
    bool IsDefault,
    string VisualKey = "value",
    double? FillPercent = null);

public sealed record SensorObservationRow(
    string TimestampText,
    string StateKey,
    string StateLabel,
    string ValueText,
    string Unit,
    string DurationText,
    string? Message,
    string? ExecutionProbe,
    int ChannelCount);
