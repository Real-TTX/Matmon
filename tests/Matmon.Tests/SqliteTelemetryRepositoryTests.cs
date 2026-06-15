using Matmon.Core.Domain;
using Matmon.Core.Telemetry;

namespace Matmon.Tests;

public sealed class SqliteTelemetryRepositoryTests : IDisposable
{
    private static readonly Guid SensorA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SensorB = new("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Base = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;
    private readonly SqliteTelemetryRepository _repo;

    public SqliteTelemetryRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"matmon-telemetry-{Guid.NewGuid():N}.db");
        _repo = new SqliteTelemetryRepository(_dbPath);
    }

    private static SensorObservation Observation(Guid sensorId, DateTimeOffset ts, SensorState state, double? value)
    {
        return new SensorObservation
        {
            SensorId = sensorId,
            TimestampUtc = ts,
            State = state,
            Value = value,
            DefaultChannelKey = "cpu",
            Channels = new List<SensorChannelValue>
            {
                new() { Key = "cpu", Label = "CPU", Value = value, Unit = "%", IsDefault = true }
            },
            Duration = TimeSpan.FromMilliseconds(42),
            Message = "ok"
        };
    }

    [Fact]
    public void Append_and_get_latest_returns_newest_per_sensor()
    {
        _repo.AppendObservation(Observation(SensorA, Base, SensorState.Healthy, 10));
        _repo.AppendObservation(Observation(SensorA, Base.AddMinutes(5), SensorState.Warning, 20));
        _repo.AppendObservation(Observation(SensorB, Base, SensorState.Healthy, 1));

        var latest = _repo.GetLatestObservations();

        Assert.Equal(2, latest.Count);
        Assert.Equal(SensorState.Warning, latest[SensorA].State);
        Assert.Equal(20, latest[SensorA].Value);
        Assert.Equal(Base.AddMinutes(5), latest[SensorA].TimestampUtc);
    }

    [Fact]
    public void Append_preserves_channels_roundtrip()
    {
        _repo.AppendObservation(Observation(SensorA, Base, SensorState.Healthy, 73));

        var observation = Assert.Single(_repo.GetAllObservations());
        var channel = Assert.Single(observation.Channels);

        Assert.Equal("cpu", channel.Key);
        Assert.Equal(73, channel.Value);
        Assert.Equal("%", channel.Unit);
        Assert.True(channel.IsDefault);
        Assert.Equal(TimeSpan.FromMilliseconds(42), observation.Duration);
    }

    [Fact]
    public void GetObservations_returns_last_n_within_window_ascending()
    {
        for (var i = 0; i < 5; i++)
        {
            _repo.AppendObservation(Observation(SensorA, Base.AddMinutes(i), SensorState.Healthy, i));
        }

        var result = _repo.GetObservations(SensorA, Base, maxCount: 3);

        Assert.Equal(3, result.Count);
        Assert.Equal(new double?[] { 2, 3, 4 }, result.Select(o => o.Value));
    }

    [Fact]
    public void GetObservations_respects_window_lower_bound()
    {
        _repo.AppendObservation(Observation(SensorA, Base.AddHours(-2), SensorState.Healthy, 1));
        _repo.AppendObservation(Observation(SensorA, Base, SensorState.Healthy, 2));

        var result = _repo.GetObservations(SensorA, Base.AddHours(-1), maxCount: null);

        Assert.Equal(2, Assert.Single(result).Value);
    }

    [Fact]
    public void GetRecentObservationsBySensor_always_includes_latest_even_outside_window()
    {
        _repo.AppendObservation(Observation(SensorA, Base.AddDays(-10), SensorState.Healthy, 99));

        var recent = _repo.GetRecentObservationsBySensor(Base.AddHours(-1), maxPerSensor: 10);

        Assert.True(recent.ContainsKey(SensorA));
        Assert.Equal(99, Assert.Single(recent[SensorA]).Value);
    }

    [Fact]
    public void PruneObservations_removes_only_older_than_cutoff()
    {
        _repo.AppendObservation(Observation(SensorA, Base.AddDays(-10), SensorState.Healthy, 1));
        _repo.AppendObservation(Observation(SensorA, Base, SensorState.Healthy, 2));

        var removed = _repo.PruneObservations(SensorA, Base.AddDays(-1));

        Assert.Equal(1, removed);
        Assert.Equal(2, Assert.Single(_repo.GetAllObservations()).Value);
    }

    [Fact]
    public void Events_are_returned_newest_first_and_pruned_by_cutoff()
    {
        _repo.AppendEvent(new MonitoringEvent { TimestampUtc = Base.AddDays(-10), Message = "old" });
        _repo.AppendEvent(new MonitoringEvent { TimestampUtc = Base, Message = "new" });

        var events = _repo.GetEvents(10);
        Assert.Equal("new", events[0].Message);
        Assert.Equal("old", events[1].Message);

        var removed = _repo.PruneEvents(Base.AddDays(-1));
        Assert.Equal(1, removed);
        Assert.Equal("new", Assert.Single(_repo.GetAllEvents()).Message);
    }

    [Fact]
    public void Statistics_upsert_inserts_then_updates_same_bucket()
    {
        var bucket = new SensorStatisticsBucket
        {
            SensorId = SensorA,
            BucketStartUtc = Base,
            BucketMinutes = 60,
            DefaultChannelKey = "cpu",
            State = SensorState.Healthy,
            SampleCount = 1,
            Average = 10,
            Minimum = 10,
            Maximum = 10,
            LastValue = 10
        };
        _repo.UpsertStatisticsBucket(bucket);

        bucket.SampleCount = 2;
        bucket.Average = 15;
        bucket.Maximum = 20;
        bucket.LastValue = 20;
        bucket.State = SensorState.Warning;
        _repo.UpsertStatisticsBucket(bucket);

        var stored = _repo.GetStatisticsBucket(SensorA, 60, Base);
        Assert.NotNull(stored);
        Assert.Equal(2, stored!.SampleCount);
        Assert.Equal(15, stored.Average);
        Assert.Equal(20, stored.Maximum);
        Assert.Equal(SensorState.Warning, stored.State);
        Assert.Single(_repo.GetStatistics(SensorA));
    }

    [Fact]
    public void GetCounts_reflects_stored_rows()
    {
        _repo.AppendObservation(Observation(SensorA, Base, SensorState.Healthy, 1));
        _repo.AppendEvent(new MonitoringEvent { TimestampUtc = Base, Message = "e" });

        var counts = _repo.GetCounts();

        Assert.Equal(1, counts.Observations);
        Assert.Equal(1, counts.Events);
        Assert.Equal(0, counts.Statistics);
    }

    [Fact]
    public void DeleteObservations_with_null_removes_all()
    {
        _repo.AppendObservation(Observation(SensorA, Base, SensorState.Healthy, 1));
        _repo.AppendObservation(Observation(SensorB, Base, SensorState.Healthy, 2));

        var removed = _repo.DeleteObservations(olderThanUtc: null);

        Assert.Equal(2, removed);
        Assert.Empty(_repo.GetAllObservations());
    }

    [Fact]
    public void ReplaceAllObservations_clears_then_inserts()
    {
        _repo.AppendObservation(Observation(SensorA, Base, SensorState.Healthy, 1));

        _repo.ReplaceAllObservations(new[]
        {
            Observation(SensorB, Base, SensorState.Critical, 5),
            Observation(SensorB, Base.AddMinutes(1), SensorState.Healthy, 6)
        });

        var all = _repo.GetAllObservations();
        Assert.Equal(2, all.Count);
        Assert.All(all, o => Assert.Equal(SensorB, o.SensorId));
    }

    public void Dispose()
    {
        _repo.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                var path = _dbPath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup of the temp database files.
            }
        }
    }
}
