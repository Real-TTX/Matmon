using Matmon.Core.Telemetry;

namespace Matmon.Tests;

/// <summary>
/// The scale is the whole reason a multi-series chart works: normalise each line to its own min/max and they
/// all look identical, so the one that spiked cannot be spotted. These pin the shared-scale behaviour and the
/// two degenerate inputs that would otherwise divide by zero or draw an invisible dot.
/// </summary>
public class SparklineGeometryTests
{
    [Fact]
    public void Scale_spans_every_series()
    {
        var (min, max) = SparklineGeometry.Scale([[10d, 12d], [4d, 30d], [15d, 16d]]);

        Assert.Equal(4, min);
        Assert.Equal(30, max);
    }

    [Fact]
    public void Scale_widens_a_flat_series_instead_of_returning_a_zero_range()
    {
        var (min, max) = SparklineGeometry.Scale([[7d, 7d, 7d]]);

        Assert.True(max > min, "a zero range would divide by zero when the line is drawn");
    }

    [Fact]
    public void Scale_of_nothing_is_a_usable_range()
    {
        var (min, max) = SparklineGeometry.Scale([]);

        Assert.True(max > min);
    }

    [Fact]
    public void Line_needs_at_least_two_points()
    {
        Assert.Null(SparklineGeometry.Line([], 0, 1));
        Assert.Null(SparklineGeometry.Line([5d], 0, 10));
        Assert.NotNull(SparklineGeometry.Line([5d, 6d], 0, 10));
    }

    [Fact]
    public void Line_puts_the_highest_value_above_the_lowest()
    {
        // Same scale for both, which is the point: the bigger series must draw higher on the chart. SVG y
        // grows downwards, so "higher" means a SMALLER y.
        var low = SparklineGeometry.Line([1d, 1d], 0, 100)!;
        var high = SparklineGeometry.Line([90d, 90d], 0, 100)!;

        Assert.True(FirstY(high) < FirstY(low));
    }

    [Fact]
    public void Line_spans_the_full_width_whatever_the_point_count()
    {
        foreach (var count in new[] { 2, 7, 64 })
        {
            var values = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
            var path = SparklineGeometry.Line(values, 0, count)!;

            var lastX = double.Parse(path.Split(' ')[^2], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(SparklineGeometry.Width, lastX, 2);
        }
    }

    [Fact]
    public void Line_is_culture_invariant()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A German culture would emit "12,5", which turns one SVG coordinate pair into two and silently
            // breaks the path on exactly the machines this ships to.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var path = SparklineGeometry.Line([0d, 1d, 2d], 0, 3)!;

            Assert.DoesNotContain(",", path);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    private static double FirstY(string path) =>
        double.Parse(path.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
}
