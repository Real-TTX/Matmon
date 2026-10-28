using Matmon.Core.Telemetry;

namespace Matmon.Tests;

public sealed class StateTimelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_check_holds_until_the_next_and_equal_neighbours_merge()
    {
        var end = Start.AddHours(10);
        var segments = StateTimeline.Segments(
        [
            (Start.AddHours(1), "ok"),
            (Start.AddHours(2), "ok"),
            (Start.AddHours(5), "error"),
            (Start.AddHours(6), "ok")
        ], Start, end);

        // Nothing is guessed for the hour before the first check.
        Assert.Equal(3, segments.Count);
        Assert.Equal(new StateSegment(10, 40, "ok"), segments[0]);
        Assert.Equal(new StateSegment(50, 10, "error"), segments[1]);
        Assert.Equal(new StateSegment(60, 40, "ok"), segments[2]);
    }

    [Fact]
    public void A_check_before_the_window_starts_it_and_the_format_is_invariant()
    {
        var segments = StateTimeline.Segments([(Start.AddHours(-1), "warning")], Start, Start.AddHours(3));

        Assert.Equal(new StateSegment(0, 100, "warning"), Assert.Single(segments));
        Assert.Equal("0,33.33,ok", StateTimeline.Format([new StateSegment(0, 33.3333, "ok")]));
    }
}
