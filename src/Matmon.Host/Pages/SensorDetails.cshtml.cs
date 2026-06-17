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
            var result = await _sensorExecutionService.ExecuteNowAsync(SensorId);
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

    public IActionResult OnPostSetDefaultChannel(string channelKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(channelKey))
            {
                throw new InvalidOperationException("Channel key is required.");
            }

            var sensor = _workspaceStore.FindElement(SensorId) as SensorElement
                ?? throw new InvalidOperationException("Selected element is not a sensor.");

            sensor.Settings.DefaultChannelKey = channelKey.Trim();
            _workspaceStore.Save();

            StatusMessage = $"Graph channel set to '{channelKey.Trim()}'.";
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
        var defaultChannelLabel = defaultChannel is null
            ? (string.IsNullOrWhiteSpace(defaultChannelKey) ? "Default" : HumanizeChannelKey(defaultChannelKey))
            : string.IsNullOrWhiteSpace(defaultChannel.Label) ? defaultChannel.Key : defaultChannel.Label;
        var executionProbe = FormatExecutionProbe(latestObservation);
        var statisticsBuckets = _workspaceStore.GetSensorStatistics(sensor.Id);
        var statisticsSummary = BuildStatisticsSummary(statisticsBuckets, displayScale, measurementKind);
        var unitConversion = BuildUnitConversion(rawUnit, displayScale, measurementKind, currentValue ?? scaleReferenceValue);

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
            executionProbe,
            defaultChannelLabel,
            unit,
            currentValue.HasValue ? currentDisplay.Text : "—",
            latestObservation is null ? null : latestObservation.TimestampUtc.ToLocalTime().ToString("dd.MM HH:mm:ss"),
            latestObservation is null ? null : FormatDuration(latestObservation.Duration),
            windows,
            selectedWindow,
            BuildChannelRows(latestObservation, fallbackUnit, defaultChannelKey),
            BuildRecentObservationRows(history, fallbackUnit, defaultChannelKey, displayScale),
            statisticsSummary,
            unitConversion);

        return true;
    }

    private static SensorStatisticsSummary? BuildStatisticsSummary(
        IReadOnlyList<SensorStatisticsBucket> buckets,
        SensorUnitScale scale,
        SensorMeasurementKind kind)
    {
        if (buckets.Count == 0)
        {
            return null;
        }

        var bucketMinutes = buckets[^1].BucketMinutes;
        var rows = buckets
            .OrderByDescending(bucket => bucket.BucketStartUtc)
            .Take(500)
            .Select(bucket => new SensorStatisticsRow(
                FormatBucketPeriod(bucket.BucketStartUtc, bucketMinutes),
                bucket.BucketStartUtc.ToUnixTimeMilliseconds(),
                FormatStat(bucket.Average, scale, kind),
                FormatStat(bucket.Minimum, scale, kind),
                FormatStat(bucket.Maximum, scale, kind),
                FormatStat(bucket.LowPercentile, scale, kind),
                FormatStat(bucket.HighPercentile, scale, kind),
                bucket.SampleCount,
                bucket.UptimePercent is double uptime ? $"{uptime.ToString("0.#", CultureInfo.InvariantCulture)} %" : null,
                MonitoringStatePresentation.Key(bucket.State)))
            .ToArray();

        var unit = string.IsNullOrWhiteSpace(scale.Unit) ? null : scale.Unit;
        return new SensorStatisticsSummary(DescribeGranularity(bucketMinutes), buckets.Count, unit, rows);
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
        return value.HasValue ? SensorUnitConverter.Format(value, scale, kind).Text : "—";
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

    private static IReadOnlyList<SensorChannelRow> BuildChannelRows(SensorObservation? latestObservation, string fallbackUnit, string? defaultChannelKey)
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
            var (visualKey, fillPercent) = ResolveChannelVisual(latestObservation.Value, fallbackUnit, SensorMeasurementKind.Unknown);
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
                var (visualKey, fillPercent) = ResolveChannelVisual(channel.Value, channel.Unit, channel.MeasurementKind);

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
        SensorMeasurementKind kind)
    {
        var resolved = kind != SensorMeasurementKind.Unknown
            ? kind
            : SensorUnitConverter.GuessMeasurementKind(unit);

        return resolved switch
        {
            SensorMeasurementKind.Percent when value.HasValue => ("progress", Math.Clamp(value.Value, 0d, 100d)),
            SensorMeasurementKind.Boolean when value.HasValue => ("boolean", value.Value >= 0.5d ? 100d : 0d),
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
                    observation.TimestampUtc.ToLocalTime().ToString("dd.MM HH:mm:ss"),
                    MonitoringStatePresentation.Key(observation.State),
                    MonitoringStatePresentation.Label(observation.State),
                    defaultValue.HasValue ? display.Text : "—",
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
            : "—";
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
    SensorUnitConversion? UnitConversion);

public sealed record SensorStatisticsSummary(
    string GranularityLabel,
    int BucketCount,
    string? Unit,
    IReadOnlyList<SensorStatisticsRow> Rows);

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
    string StateKey);

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
