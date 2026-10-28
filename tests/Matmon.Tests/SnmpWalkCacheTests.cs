using Matmon.Host.Services;

namespace Matmon.Tests;

/// <summary>The SNMP walk stays on the server while rows are picked; the editor posts only its id and the ticked OIDs.</summary>
public sealed class SnmpWalkCacheTests
{
    private static IReadOnlyList<SnmpWalkEntry> Walk(int rows) =>
        Enumerable.Range(0, rows).Select(i => new SnmpWalkEntry($"1.3.6.1.4.1.12356.1.{i}.0", "Counter32", i.ToString(), true)).ToList();

    [Fact]
    public void A_stored_walk_comes_back_under_its_id()
    {
        var cache = new SnmpWalkCache();
        var id = cache.Store(Walk(2000));
        Assert.Equal(2000, cache.Find(id)!.Count);
        Assert.Null(cache.Find(Guid.NewGuid()));
    }

    [Fact]
    public void A_walk_is_gone_after_its_lifetime()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new SnmpWalkCache(time);
        var id = cache.Store(Walk(3));
        time.Advance(SnmpWalkCache.Lifetime - TimeSpan.FromMinutes(1));
        Assert.NotNull(cache.Find(id));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(cache.Find(id));
    }

    [Fact]
    public void Only_the_newest_walks_are_kept()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new SnmpWalkCache(time);
        var first = cache.Store(Walk(1));
        for (var i = 0; i < SnmpWalkCache.MaxWalks; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            cache.Store(Walk(1));
        }
        Assert.Null(cache.Find(first));
    }
}

file sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
