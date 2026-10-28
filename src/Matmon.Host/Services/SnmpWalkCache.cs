using System.Collections.Concurrent;

namespace Matmon.Host.Services;

/// <summary>One row of an SNMP walk, as the sensor editors show it.</summary>
public sealed record SnmpWalkEntry(string Oid, string Syntax, string Value, bool IsNumeric);

/// <summary>
/// Keeps the result of an SNMP walk on the server while someone picks rows from it. The editor form carries only the walk's
/// id and the OIDs that are ticked: it used to post every row back (six fields each), so a firewall walk of a few thousand
/// rows broke ASP.NET's form limit (an empty 400 from 170 rows on) and, over remote access, a body of several megabytes
/// ran into the tunnel's time limit. Entries live two hours and at most <see cref="MaxWalks"/> are kept (the oldest go);
/// a walk that is gone only means the list is not shown again - the ticked OIDs still arrive with the form.
/// </summary>
public sealed class SnmpWalkCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    public const int MaxWalks = 50;

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset StoredUtc, IReadOnlyList<SnmpWalkEntry> Rows)> _walks = new();
    private readonly TimeProvider _time;

    public SnmpWalkCache(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Keeps a walk and returns its id (random, so one editor cannot guess another's).</summary>
    public Guid Store(IReadOnlyList<SnmpWalkEntry> rows)
    {
        var now = _time.GetUtcNow();
        foreach (var stale in _walks.Where(walk => now - walk.Value.StoredUtc > Lifetime).Select(walk => walk.Key).ToList())
        {
            _walks.TryRemove(stale, out _);
        }
        while (_walks.Count >= MaxWalks && _walks.MinBy(walk => walk.Value.StoredUtc) is { Key: var oldest })
        {
            _walks.TryRemove(oldest, out _);
        }

        var id = Guid.NewGuid();
        _walks[id] = (now, rows.ToList());
        return id;
    }

    /// <summary>The walk, or null when it is unknown or older than <see cref="Lifetime"/>.</summary>
    public IReadOnlyList<SnmpWalkEntry>? Find(Guid id) =>
        _walks.TryGetValue(id, out var walk) && _time.GetUtcNow() - walk.StoredUtc <= Lifetime ? walk.Rows : null;
}
