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

    private static string Format(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
