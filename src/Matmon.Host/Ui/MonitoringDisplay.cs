using System.Globalization;
using Matmon.Core.Domain;

namespace Matmon.Host.Ui;

/// <summary>Shared display formatting for monitoring values. Single source for the small formatters that were
/// previously copy-pasted across page models (SensorDetails, ProbeUsage, Workspace, Config, BackupRestore).</summary>
public static class MonitoringDisplay
{
    /// <summary>Compact execution-duration text: "12.3 ms" / "4.5 s" / "01:23".</summary>
    public static string FormatDuration(TimeSpan duration)
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

    /// <summary>Thousands-separated count for the UI ("12,345").</summary>
    public static string FormatCount(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>The sensor's effective polling summary: explicit schedule, else explicit interval, else the
    /// per-type default - so the UI shows the real effective cadence instead of looking unset.</summary>
    public static string FormatScheduleSummary(MonitoringSettings settings, TimeSpan defaultInterval)
    {
        if (settings.PollingSchedule is not null)
        {
            return settings.PollingSchedule.Summary();
        }

        if (settings.PollingInterval is TimeSpan interval)
        {
            return $"every {MonitoringSchedule.FormatDuration(interval)}";
        }

        return $"every {MonitoringSchedule.FormatDuration(defaultInterval)}";
    }

    /// <summary>A readable label from a raw channel key: separators become spaces, camelCase is split, and the
    /// result is title-cased ("cpuLoad.avg" → "Cpu Load Avg").</summary>
    public static string HumanizeChannelKey(string channelKey)
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
