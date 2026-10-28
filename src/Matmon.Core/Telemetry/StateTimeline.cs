using System.Globalization;

namespace Matmon.Core.Telemetry;

/// <summary>One stretch of a state band: where it starts and how wide it is, in percent of the window, and its tone.</summary>
public readonly record struct StateSegment(double Start, double Width, string Tone);

/// <summary>
/// The state band of a timeline tile, pure so it is tested without a store. Each check's state holds until the next check
/// (or the end of the window); the time before the first check in the window is left empty rather than guessed, and
/// neighbouring stretches of the same tone are merged so a sensor that was fine all day is ONE segment, not 2 880.
/// </summary>
public static class StateTimeline
{
    public static IReadOnlyList<StateSegment> Segments(IEnumerable<(DateTimeOffset At, string Tone)> checks, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var span = (endUtc - startUtc).TotalMilliseconds;
        if (span <= 0)
        {
            return [];
        }

        var ordered = checks
            .Where(check => check.At < endUtc)
            .OrderBy(check => check.At)
            .ToList();
        var result = new List<StateSegment>();
        for (var index = 0; index < ordered.Count; index++)
        {
            var from = ordered[index].At < startUtc ? startUtc : ordered[index].At;
            var to = index + 1 < ordered.Count ? ordered[index + 1].At : endUtc;
            if (to <= from)
            {
                continue;
            }

            var start = (from - startUtc).TotalMilliseconds / span * 100;
            var width = (to - from).TotalMilliseconds / span * 100;
            var tone = ordered[index].Tone;
            if (result.Count > 0 && result[^1].Tone == tone && Math.Abs(result[^1].Start + result[^1].Width - start) < 0.0001)
            {
                result[^1] = result[^1] with { Width = result[^1].Width + width };
            }
            else
            {
                result.Add(new StateSegment(start, width, tone));
            }
        }

        return result;
    }

    /// <summary>"start,width,tone;..." with two decimals - compact enough to ride every live update.</summary>
    public static string Format(IEnumerable<StateSegment> segments) =>
        string.Join(';', segments.Select(segment => string.Create(CultureInfo.InvariantCulture, $"{segment.Start:0.##},{segment.Width:0.##},{segment.Tone}")));
}
