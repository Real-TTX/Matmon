using Matmon.Core.Domain;
using Matmon.Host.Services;

namespace Matmon.Tests;

/// <summary>The secondary probe's store-and-forward buffer: retention trims by age, the hard cap drops the
/// oldest, retention 0 clears everything, and peek/remove drain oldest-first (FIFO) for a reconnect flush.</summary>
public sealed class OfflineObservationBufferTests
{
    private static ProbeSensorObservationReport ReportAt(DateTimeOffset at) =>
        new(Guid.NewGuid(), SensorExecutionResult.Healthy(TimeSpan.Zero, "ok"), at);

    [Fact]
    public void Prune_drops_observations_past_the_retention_window()
    {
        var now = DateTimeOffset.UtcNow;
        var buffer = new OfflineObservationBuffer();
        buffer.Add(ReportAt(now.AddDays(-10))); // stale
        buffer.Add(ReportAt(now.AddDays(-3)));  // kept
        buffer.Add(ReportAt(now.AddMinutes(-1))); // kept

        var dropped = buffer.Prune(now, TimeSpan.FromDays(7), maxCount: 0);

        Assert.Equal(1, dropped);
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void Prune_caps_to_max_count_dropping_the_oldest()
    {
        var now = DateTimeOffset.UtcNow;
        var buffer = new OfflineObservationBuffer();
        for (var i = 0; i < 5; i++)
        {
            buffer.Add(ReportAt(now.AddMinutes(-i))); // index 0 is the oldest we add first
        }

        var dropped = buffer.Prune(now, TimeSpan.FromDays(7), maxCount: 3);

        Assert.Equal(2, dropped);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Prune_with_non_positive_retention_clears_everything()
    {
        var now = DateTimeOffset.UtcNow;
        var buffer = new OfflineObservationBuffer();
        buffer.Add(ReportAt(now));
        buffer.Add(ReportAt(now));

        var dropped = buffer.Prune(now, TimeSpan.Zero, maxCount: 100);

        Assert.Equal(2, dropped);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Peek_and_remove_drain_oldest_first()
    {
        var now = DateTimeOffset.UtcNow;
        var oldest = ReportAt(now.AddMinutes(-3));
        var middle = ReportAt(now.AddMinutes(-2));
        var newest = ReportAt(now.AddMinutes(-1));
        var buffer = new OfflineObservationBuffer();
        buffer.Add(oldest);
        buffer.Add(middle);
        buffer.Add(newest);

        var chunk = buffer.PeekOldest(2);
        Assert.Equal(new[] { oldest.SensorId, middle.SensorId }, chunk.Select(r => r.SensorId));

        buffer.RemoveOldest(2);
        Assert.Equal(1, buffer.Count);
        Assert.Equal(new[] { newest.SensorId }, buffer.PeekOldest(10).Select(r => r.SensorId));
    }
}
