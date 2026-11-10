using Matmon.Core.Telemetry;

namespace Matmon.Tests;

/// <summary>The sunburst map widget's geometry: rings from the inside out, each segment as wide as its share of the sensors.</summary>
public sealed class SunburstLayoutTests
{
    private static SunburstNode Leaf(string name) => new() { Label = name };

    [Fact]
    public void Segments_are_as_wide_as_their_share_and_children_sit_inside_their_parent()
    {
        var hostA = new SunburstNode { Label = "A" };
        hostA.Children.AddRange([Leaf("a1"), Leaf("a2"), Leaf("a3")]);
        var hostB = new SunburstNode { Label = "B" };
        hostB.Children.Add(Leaf("b1"));
        var root = new SunburstNode { Label = "root" };
        root.Children.AddRange([hostA, hostB]);

        var segments = SunburstLayout.Layout(root);

        Assert.Equal(6, segments.Count);
        Assert.Equal(2, SunburstLayout.RingCount(segments));
        var a = segments.Single(segment => segment.Node == hostA);
        var b = segments.Single(segment => segment.Node == hostB);
        Assert.Equal((1, 0d, 270d), (a.Ring, a.StartDegrees, a.EndDegrees));
        Assert.Equal((1, 270d, 360d), (b.Ring, b.StartDegrees, b.EndDegrees));
        Assert.All(segments.Where(segment => hostA.Children.Contains(segment.Node)),
            segment => Assert.True(segment.Ring == 2 && segment.StartDegrees >= 0 && segment.EndDegrees <= 270.0001));
        // Depth first, in the given order: the position of a segment is stable.
        Assert.Equal(["A", "a1", "a2", "a3", "B", "b1"], segments.Select(segment => segment.Node.Label));
    }

    [Fact]
    public void Paths_and_the_encoded_geometry_are_culture_proof_and_a_full_circle_is_drawn()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var path = SunburstLayout.ArcPath(20, 40, 0, 90.5);
            Assert.DoesNotContain(",", path);
            Assert.StartsWith("M0 -40A40 40 0 0 1", path);

            var full = SunburstLayout.ArcPath(20, 40, 0, 360);
            Assert.Equal(2, full.Split('M').Length - 1);

            var encoded = SunburstLayout.Encode(new SunburstSegment(2, 10.25, 99.5, Leaf("x")));
            Assert.Equal("2,10.25,99.5", encoded);
            Assert.True(SunburstLayout.TryDecode(encoded, out var ring, out var start, out var end));
            Assert.Equal((2, 10.25, 99.5), (ring, start, end));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
