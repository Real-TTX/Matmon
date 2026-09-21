using Matmon.Core.Domain;
using Matmon.Core.Telemetry;

namespace Matmon.Tests;

/// <summary>
/// The windowed statistics read. Worth its own test because <c>bucket_start</c> is stored as Unix
/// MILLISECONDS, not as text: binding the cutoff as an ISO string compiles, runs, and quietly returns
/// nothing - the wallboard tile would just look like the sensor had no history.
/// </summary>
public sealed class TelemetryStatisticsWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-stats-window-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A still-open SQLite handle on Windows must not fail the test run.
        }
    }

    private SqliteTelemetryRepository NewRepository()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
    }

    private static SensorStatisticsBucket Bucket(Guid sensorId, DateTimeOffset start, string channel = "default") => new()
    {
        SensorId = sensorId,
        BucketMinutes = 60,
        BucketStartUtc = start,
        DefaultChannelKey = channel,
        HealthyCount = 1,
        SampleCount = 1
    };

    [Fact]
    public void Returns_only_buckets_at_or_after_the_cutoff()
    {
        using var repository = NewRepository();
        var sensorId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        repository.UpsertStatisticsBucket(Bucket(sensorId, now.AddDays(-30)));
        repository.UpsertStatisticsBucket(Bucket(sensorId, now.AddDays(-2)));
        repository.UpsertStatisticsBucket(Bucket(sensorId, now.AddHours(-1)));

        var windowed = repository.GetStatistics(sensorId, now.AddDays(-7));

        Assert.Equal(2, windowed.Count);
        Assert.All(windowed, bucket => Assert.True(bucket.BucketStartUtc >= now.AddDays(-7)));
        // Sanity: the unwindowed read still sees all three, so the filter is the query's doing and not a
        // write that never landed.
        Assert.Equal(3, repository.GetStatistics(sensorId).Count);
    }

    [Fact]
    public void Does_not_leak_another_sensors_buckets()
    {
        using var repository = NewRepository();
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        repository.UpsertStatisticsBucket(Bucket(mine, now.AddHours(-1)));
        repository.UpsertStatisticsBucket(Bucket(other, now.AddHours(-1)));

        Assert.Single(repository.GetStatistics(mine, now.AddDays(-1)));
    }

    [Fact]
    public void Keeps_every_channel_of_a_window()
    {
        // The per-channel rows must all come back - SensorUptime is what dedupes them, not the query.
        using var repository = NewRepository();
        var sensorId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddHours(-1);

        repository.UpsertStatisticsBucket(Bucket(sensorId, start, "cpu"));
        repository.UpsertStatisticsBucket(Bucket(sensorId, start, "mem"));

        Assert.Equal(2, repository.GetStatistics(sensorId, start.AddMinutes(-5)).Count);
    }
}
