using Matmon.Core.Domain;
using Matmon.Probe;

namespace Matmon.Tests;

/// <summary>
/// The offline buffer on disk: what a probe buffered while its primary was away is still there after the probe itself
/// restarted, a flush only moves a counter (the file is compacted later), and a line torn by a crash mid-write does
/// not cost the rest.
/// </summary>
public sealed class OfflineObservationBufferPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-buffer-" + Guid.NewGuid().ToString("N"));

    public OfflineObservationBufferPersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private string File(string name = "offline.jsonl") => Path.Combine(_dir, name);

    private static ProbeSensorObservationReport Report(DateTimeOffset at, string message = "ok") =>
        new(Guid.NewGuid(), SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(12), message), at);

    [Fact]
    public void What_was_buffered_survives_a_restart_with_its_times_and_results()
    {
        var at = new DateTimeOffset(2026, 10, 23, 1, 2, 3, TimeSpan.Zero);
        var first = Report(at, "timed out");
        var buffer = new OfflineObservationBuffer(File());
        buffer.Add(first);
        buffer.AddRange([Report(at.AddMinutes(1)), Report(at.AddMinutes(2))]);

        var restarted = new OfflineObservationBuffer(File());

        Assert.Equal(3, restarted.Count);
        var loaded = restarted.PeekOldest(1).Single();
        Assert.Equal(first.SensorId, loaded.SensorId);
        Assert.Equal(at, loaded.TimestampUtc);
        Assert.Equal(SensorState.Critical, loaded.Result.State);
        Assert.Equal("timed out", loaded.Result.Message);
    }

    [Fact]
    public void Flushed_reports_do_not_come_back_after_a_restart()
    {
        var now = DateTimeOffset.UtcNow;
        var buffer = new OfflineObservationBuffer(File());
        var reports = Enumerable.Range(0, 5).Select(i => Report(now.AddSeconds(i))).ToList();
        buffer.AddRange(reports);

        buffer.RemoveOldest(2);
        var restarted = new OfflineObservationBuffer(File());

        Assert.Equal(reports.Skip(2).Select(r => r.SensorId), restarted.PeekOldest(10).Select(r => r.SensorId));
    }

    [Fact]
    public void An_emptied_buffer_leaves_no_file_and_pruning_rewrites_it()
    {
        var now = DateTimeOffset.UtcNow;
        var buffer = new OfflineObservationBuffer(File());
        buffer.AddRange([Report(now.AddDays(-10)), Report(now)]);

        Assert.Equal(1, buffer.Prune(now, TimeSpan.FromDays(7), maxCount: 0));
        Assert.Single(new OfflineObservationBuffer(File()).PeekOldest(10));

        buffer.RemoveOldest(1);
        Assert.False(System.IO.File.Exists(File()));
    }

    [Fact]
    public void A_line_torn_by_a_crash_is_skipped_and_the_rest_loads()
    {
        var now = DateTimeOffset.UtcNow;
        var buffer = new OfflineObservationBuffer(File());
        buffer.AddRange([Report(now), Report(now.AddSeconds(1))]);
        System.IO.File.AppendAllText(File(), "{\"sensorId\":\"8b0c");

        var restarted = new OfflineObservationBuffer(File());

        Assert.Equal(2, restarted.Count);
        Assert.DoesNotContain("8b0c", System.IO.File.ReadAllText(File())); // compacted on load
    }

    [Fact]
    public void Without_a_path_it_stays_in_memory()
    {
        var buffer = new OfflineObservationBuffer();
        buffer.Add(Report(DateTimeOffset.UtcNow));

        Assert.Null(buffer.FilePath);
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
