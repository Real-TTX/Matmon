using System.Globalization;

namespace Matmon.Core.Telemetry;

/// <summary>One node of a sunburst: a probe, folder or host (with children) or a sensor (a leaf).</summary>
public sealed class SunburstNode
{
    public required string Label { get; init; }

    public Guid? ElementId { get; init; }

    /// <summary>The state key the segment is drawn in (error / warning / ok / unknown / paused).</summary>
    public string Tone { get; set; } = "unknown";

    /// <summary>A sensor's message, or a container's summary.</summary>
    public string? Detail { get; set; }

    public List<SunburstNode> Children { get; } = [];

    /// <summary>Sensors at or below this node - a leaf is one.</summary>
    public int Weight => Children.Count == 0 ? 1 : Children.Sum(child => child.Weight);
}

/// <summary>A drawn segment: which ring (1 = innermost around the centre) and its angles in degrees, clockwise from 12 o'clock.</summary>
public sealed record SunburstSegment(int Ring, double StartDegrees, double EndDegrees, SunburstNode Node);

/// <summary>
/// A sunburst: the target in the centre, each level of the tree one ring further out, every segment as wide as the share of
/// sensors below it and drawn in the worst state among them - "which part of the network is red" at a glance, from the
/// probe out to the single sensor. Pure, so the geometry is tested without a page.
/// </summary>
public static class SunburstLayout
{
    /// <summary>The segments of every node below <paramref name="root"/>, depth first and children in their given order -
    /// a stable order, so a segment keeps its position (the live patch goes by position).</summary>
    public static IReadOnlyList<SunburstSegment> Layout(SunburstNode root)
    {
        var segments = new List<SunburstSegment>();
        var total = root.Weight;
        if (total == 0 || root.Children.Count == 0)
        {
            return segments;
        }

        Place(root, 1, 0, 360, segments);
        return segments;
    }

    private static void Place(SunburstNode parent, int ring, double start, double end, List<SunburstSegment> segments)
    {
        var weight = parent.Weight;
        var cursor = start;
        foreach (var child in parent.Children)
        {
            var span = (end - start) * child.Weight / weight;
            segments.Add(new SunburstSegment(ring, cursor, cursor + span, child));
            if (child.Children.Count > 0)
            {
                Place(child, ring + 1, cursor, cursor + span, segments);
            }

            cursor += span;
        }
    }

    /// <summary>How many rings the segments use.</summary>
    public static int RingCount(IEnumerable<SunburstSegment> segments) => segments.Select(segment => segment.Ring).DefaultIfEmpty(0).Max();

    /// <summary>A point on a circle round (0,0); 0 degrees is straight up, angles grow clockwise (SVG's y points down).</summary>
    public static (double X, double Y) Point(double radius, double degrees)
    {
        var radians = (degrees - 90) * Math.PI / 180;
        return (radius * Math.Cos(radians), radius * Math.Sin(radians));
    }

    /// <summary>
    /// The SVG path of a ring segment between two radii and two angles, centred on (0,0). A full circle is drawn as two
    /// halves - one arc cannot start and end on the same point.
    /// </summary>
    public static string ArcPath(double innerRadius, double outerRadius, double startDegrees, double endDegrees)
    {
        var span = endDegrees - startDegrees;
        if (span >= 359.999)
        {
            return ArcPath(innerRadius, outerRadius, startDegrees, startDegrees + 180)
                + " " + ArcPath(innerRadius, outerRadius, startDegrees + 180, startDegrees + 359.999);
        }

        var large = span > 180 ? 1 : 0;
        var (ox1, oy1) = Point(outerRadius, startDegrees);
        var (ox2, oy2) = Point(outerRadius, endDegrees);
        var (ix2, iy2) = Point(innerRadius, endDegrees);
        var (ix1, iy1) = Point(innerRadius, startDegrees);
        return string.Create(CultureInfo.InvariantCulture,
            $"M{ox1:0.###} {oy1:0.###}A{outerRadius:0.###} {outerRadius:0.###} 0 {large} 1 {ox2:0.###} {oy2:0.###}L{ix2:0.###} {iy2:0.###}A{innerRadius:0.###} {innerRadius:0.###} 0 {large} 0 {ix1:0.###} {iy1:0.###}Z");
    }

    /// <summary>The geometry of a segment as it travels in a row: "ring,start,end".</summary>
    public static string Encode(SunburstSegment segment) =>
        string.Create(CultureInfo.InvariantCulture, $"{segment.Ring},{segment.StartDegrees:0.###},{segment.EndDegrees:0.###}");

    public static bool TryDecode(string? text, out int ring, out double start, out double end)
    {
        ring = 0;
        start = end = 0;
        var parts = (text ?? string.Empty).Split(',');
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ring)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out start)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out end);
    }
}
