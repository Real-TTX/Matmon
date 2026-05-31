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
        var unit = defaultChannel?.Unit ?? fallbackUnit;
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
            : latestObservation?.Message ?? BuildDefaultMessage(sensor.SensorTypeKey, currentValue, currentState);
        var windows = BuildWindows(history, currentStateColor, defaultChannelKey).ToArray();
        var selectedWindow = windows.FirstOrDefault(window => string.Equals(window.Key, Window, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(window => string.Equals(window.Key, "1d", StringComparison.OrdinalIgnoreCase))
            ?? windows[0];
        var defaultChannelLabel = defaultChannel is null
            ? (string.IsNullOrWhiteSpace(defaultChannelKey) ? "Default" : HumanizeChannelKey(defaultChannelKey))
            : string.IsNullOrWhiteSpace(defaultChannel.Label) ? defaultChannel.Key : defaultChannel.Label;
        var executionProbe = FormatExecutionProbe(latestObservation);

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
            currentValue.HasValue ? FormatValue(currentValue.Value) : "—",
            latestObservation is null ? null : latestObservation.TimestampUtc.ToLocalTime().ToString("dd.MM HH:mm:ss"),
            latestObservation is null ? null : FormatDuration(latestObservation.Duration),
            windows,
            selectedWindow,
            BuildChannelRows(latestObservation, fallbackUnit, defaultChannelKey),
            BuildRecentObservationRows(history, fallbackUnit, defaultChannelKey));

        return true;
    }

    private static IEnumerable<SensorWindowStatistics> BuildWindows(
        IReadOnlyList<SensorObservation> observations,
        string lineColor,
        string? defaultChannelKey)
    {
        var now = DateTimeOffset.UtcNow;
        yield return SensorHistoryAnalytics.BuildWindowStatistics(observations, "1h", "1h", TimeSpan.FromHours(1), now, lineColor, defaultChannelKey);
        yield return SensorHistoryAnalytics.BuildWindowStatistics(observations, "1d", "1D", TimeSpan.FromDays(1), now, lineColor, defaultChannelKey);
        yield return SensorHistoryAnalytics.BuildWindowStatistics(observations, "1w", "1W", TimeSpan.FromDays(7), now, lineColor, defaultChannelKey);
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
            return
            [
                new SensorChannelRow(
                    "default",
                    "Default",
                    FormatValue(latestObservation.Value),
                    fallbackUnit,
                    MonitoringStatePresentation.Key(state),
                    MonitoringStatePresentation.Label(state),
                    latestObservation.Message,
                    true)
            ];
        }

        return latestObservation.Channels
            .Select(channel =>
            {
                var state = channel.State ?? SensorState.Healthy;
                var isDefault = defaultChannel is not null
                    ? string.Equals(channel.Key, defaultChannel.Key, StringComparison.OrdinalIgnoreCase)
                    : channel.IsDefault;

                return new SensorChannelRow(
                    channel.Key,
                    string.IsNullOrWhiteSpace(channel.Label) ? channel.Key : channel.Label,
                    FormatValue(channel.Value),
                    channel.Unit ?? fallbackUnit,
                    MonitoringStatePresentation.Key(state),
                    MonitoringStatePresentation.Label(state),
                    channel.Message ?? latestObservation.Message,
                    isDefault);
            })
            .ToArray();
    }

    private static IReadOnlyList<SensorObservationRow> BuildRecentObservationRows(
        IReadOnlyList<SensorObservation> history,
        string fallbackUnit,
        string? defaultChannelKey)
    {
        return history
            .TakeLast(12)
            .Select(observation =>
            {
                var defaultValue = SensorHistoryAnalytics.GetDefaultValue(observation, defaultChannelKey);
                var channelCount = observation.Channels.Count > 0
                    ? observation.Channels.Count
                    : observation.Value.HasValue ? 1 : 0;

                return new SensorObservationRow(
                    observation.TimestampUtc.ToLocalTime().ToString("dd.MM HH:mm:ss"),
                    MonitoringStatePresentation.Key(observation.State),
                    MonitoringStatePresentation.Label(observation.State),
                    defaultValue.HasValue ? FormatValue(defaultValue) : "—",
                    fallbackUnit,
                    FormatDuration(observation.Duration),
                    observation.Message,
                    FormatExecutionProbe(observation),
                    channelCount);
            })
            .ToArray();
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

    private static string BuildDefaultMessage(string sensorTypeKey, double? value, SensorState state)
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

        var unit = GetFallbackUnit(sensorTypeKey);
        return string.IsNullOrWhiteSpace(unit)
            ? $"value {FormatValue(value)}"
            : $"value {FormatValue(value)} {unit}";
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
            _ => "value"
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
    IReadOnlyList<SensorObservationRow> RecentObservations);

public sealed record SensorChannelRow(
    string Key,
    string Label,
    string ValueText,
    string Unit,
    string StateKey,
    string StateLabel,
    string? Message,
    bool IsDefault);

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
