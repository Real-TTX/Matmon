using Matmon.Host.Services;

namespace Matmon.Tests;

/// <summary>Two probe processes sharing one ProbeId (a stale container left running after an update) make every
/// other sensor result come from the wrong build - it looks like a flapping sensor, not a deployment problem.
/// These pin the detection down, since it is the only place that failure mode becomes visible.</summary>
public class ProbeDuplicateDetectionTests
{
    private static InMemoryProbeRegistry CreateRegistry(int heartbeatSeconds = 30) =>
        new(new MatmonRuntimeOptions { HeartbeatIntervalSeconds = heartbeatSeconds });

    private static ProbeHeartbeatRequest Beat(string host, string version) =>
        new("probe-milla", "Probe-Milla", AgentVersion: version, Host: host);

    [Fact]
    public void Single_process_beating_on_schedule_is_not_flagged()
    {
        var registry = CreateRegistry();
        var start = DateTimeOffset.UtcNow;

        var first = registry.Record(Beat("milla", "nightly-42"), start);
        var second = registry.Record(Beat("milla", "nightly-42"), start.AddSeconds(30));
        var third = registry.Record(Beat("milla", "nightly-42"), start.AddSeconds(60));

        Assert.Null(first.DuplicateWarning);
        Assert.Null(second.DuplicateWarning);
        Assert.Null(third.DuplicateWarning);
    }

    [Fact]
    public void Alternating_build_versions_flag_a_duplicate()
    {
        var registry = CreateRegistry();
        var start = DateTimeOffset.UtcNow;

        registry.Record(Beat("milla", "nightly-42"), start);
        var flagged = registry.Record(Beat("milla", "nightly-7"), start.AddSeconds(5));

        Assert.NotNull(flagged.DuplicateWarning);
        Assert.Contains("two processes", flagged.DuplicateWarning);
        Assert.Contains("nightly-7", flagged.DuplicateWarning);
    }

    [Fact]
    public void Beats_far_faster_than_the_interval_flag_a_duplicate()
    {
        var registry = CreateRegistry();
        var start = DateTimeOffset.UtcNow;

        // Same identity (same image on the same host), but twice the expected beat rate = a second process.
        registry.Record(Beat("milla", "nightly-42"), start);
        var flagged = registry.Record(Beat("milla", "nightly-42"), start.AddSeconds(5));

        Assert.NotNull(flagged.DuplicateWarning);
        Assert.Contains("faster than the configured interval", flagged.DuplicateWarning);
    }

    [Fact]
    public void Warning_stays_visible_across_the_beats_in_between()
    {
        var registry = CreateRegistry();
        var start = DateTimeOffset.UtcNow;

        registry.Record(Beat("milla", "nightly-42"), start);
        registry.Record(Beat("milla", "nightly-7"), start.AddSeconds(5)); // flags

        // The next beat on its own looks perfectly normal - the warning must not disappear right away.
        var afterNormalBeat = registry.Record(Beat("milla", "nightly-7"), start.AddSeconds(35));
        Assert.NotNull(afterNormalBeat.DuplicateWarning);
    }

    [Fact]
    public void Warning_clears_once_the_duplicate_is_gone()
    {
        var registry = CreateRegistry();
        var start = DateTimeOffset.UtcNow;

        registry.Record(Beat("milla", "nightly-42"), start);
        registry.Record(Beat("milla", "nightly-7"), start.AddSeconds(5)); // flags

        // Stale container removed: only one process keeps beating, well past the sticky window (6 intervals).
        var recovered = registry.Record(Beat("milla", "nightly-7"), start.AddSeconds(5 + 30 * 7));
        Assert.Null(recovered.DuplicateWarning);
    }

    [Fact]
    public void First_beat_after_startup_is_never_flagged()
    {
        var registry = CreateRegistry();

        var first = registry.Record(Beat("milla", "nightly-42"), DateTimeOffset.UtcNow);

        Assert.Null(first.DuplicateWarning);
    }
}
