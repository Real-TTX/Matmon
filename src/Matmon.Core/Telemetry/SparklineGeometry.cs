namespace Matmon.Core.Telemetry;

/// <summary>
/// Turns a series of numbers into an SVG path, in a fixed viewBox the tile stretches to fit. Pure and
/// framework-free, so the awkward part - the scale - can be unit-tested without a store.
///
/// The scale is passed IN rather than derived per series, and that is the whole point for a multi-series
/// chart: normalising each line to its own min/max makes every line look identical, so the one that spiked
/// cannot be spotted. One shared scale across the chart is what makes the lines comparable - and what makes
/// mixing units a mistake the caller has to avoid (see <see cref="Scale"/>).
/// </summary>
public static class SparklineGeometry
{
    public const double Width = 100;
    public const double Height = 40;

    /// <summary>Vertical breathing room so a line at the very top or bottom is not clipped by the stroke.</summary>
    private const double Padding = 2;

    /// <summary>The min/max spanning EVERY series, so they share one axis. A flat series (or one single
    /// value) would give a zero range, which is widened to 1 so it draws as a line through the middle
    /// instead of dividing by zero.</summary>
    public static (double Min, double Max) Scale(IEnumerable<IReadOnlyList<double>> series)
    {
        var min = double.MaxValue;
        var max = double.MinValue;

        foreach (var values in series)
        {
            foreach (var value in values)
            {
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
        }

        if (min > max)
        {
            return (0, 1);
        }

        return Math.Abs(max - min) < 0.00001 ? (min - 0.5, max + 0.5) : (min, max);
    }

    /// <summary>A polyline through <paramref name="values"/> on the given scale. Null for fewer than two
    /// points - one point is not a trend, and a one-point path renders as an invisible dot that reads as a
    /// broken chart.</summary>
    public static string? Line(IReadOnlyList<double> values, double min, double max)
    {
        if (values.Count < 2)
        {
            return null;
        }

        var range = Math.Abs(max - min) < 0.00001 ? 1 : max - min;
        var step = Width / (values.Count - 1);

        return "M " + string.Join(" L ", values.Select((value, index) =>
        {
            var x = index * step;
            var y = Height - ((value - min) / range * (Height - 2 * Padding)) - Padding;
            return $"{Format(x)} {Format(y)}";
        }));
    }

    /// <summary>
    /// A polyline placed by TIME: each point's X is its position in the chart's window (0 = window start,
    /// 1 = now). Evenly spacing points by index - what <see cref="Line"/> does for a single sensor - is wrong
    /// as soon as lines from different sensors share one chart: a sensor that only started reporting two hours
    /// ago would be stretched over the whole day, and one that polls every 30 s would not line up with one that
    /// polls every 5 min. Null for fewer than two points.
    /// </summary>
    public static string? TimeLine(IReadOnlyList<(double X, double Value)> points, double min, double max)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var range = Math.Abs(max - min) < 0.00001 ? 1 : max - min;
        return "M " + string.Join(" L ", points.Select(point =>
        {
            var x = Math.Clamp(point.X, 0, 1) * Width;
            var y = Height - ((Math.Clamp(point.Value, min, max) - min) / range * (Height - 2 * Padding)) - Padding;
            return $"{Format(x)} {Format(y)}";
        }));
    }

    /// <summary>
    /// At most <paramref name="buckets"/> points, by averaging within equal slices of the window (X 0..1):
    /// a chart a few hundred pixels wide has no use for thousands of points, and every one of them travels on
    /// each live poll. Averaging (rather than picking every n-th point) keeps a short spike's weight in its
    /// slice instead of dropping it at random. Input must be ordered by X; empty slices simply produce no point.
    /// </summary>
    public static IReadOnlyList<(double X, double Value)> Downsample(IReadOnlyList<(double X, double Value)> points, int buckets)
    {
        if (buckets <= 0 || points.Count <= buckets)
        {
            return points;
        }

        return points
            .GroupBy(point => Math.Min(buckets - 1, (int)Math.Floor(Math.Clamp(point.X, 0, 1) * buckets)))
            .OrderBy(group => group.Key)
            .Select(group => (group.Average(point => point.X), group.Average(point => point.Value)))
            .ToArray();
    }

    /// <summary>
    /// Axis bounds a person can read: the data range widened to round numbers (a step of 1, 2 or 5 times a
    /// power of ten, about five steps over the range), so the labels say "0 / 25 / 50" rather than
    /// "3.17 / 27.9 / 52.6". A percentage axis never leaves 0..100 - a CPU line cannot go to 105 %.
    /// </summary>
    public static (double Min, double Max) NiceScale(double min, double max, bool percent)
    {
        if (min > max)
        {
            (min, max) = (max, min);
        }

        var range = max - min;
        if (range < 0.00001)
        {
            range = Math.Max(Math.Abs(max) * 0.1, 1);
            min -= range / 2;
            max += range / 2;
        }

        var rough = range / 5;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var step = new[] { 1d, 2d, 5d, 10d }.Select(factor => factor * magnitude).First(candidate => candidate >= rough);
        var niceMin = Math.Floor(min / step) * step;
        var niceMax = Math.Ceiling(max / step) * step;

        if (percent)
        {
            niceMin = Math.Max(0, niceMin);
            niceMax = Math.Min(100, niceMax);
            if (niceMax <= niceMin)
            {
                niceMax = Math.Min(100, niceMin + step);
            }
        }

        return (niceMin, niceMax);
    }

    private static string Format(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
