namespace Matmon.Core.Domain;

/// <summary>
/// The v3 layout: a slide is an ordered FLOW of tiles across <see cref="MonitoringMap.Columns"/> columns, not
/// a set of x/y placements. The browser does the wrapping (CSS grid auto-placement), so there is no geometry
/// to compute here - what there is, is the one rule the whole model rests on: the order must be dense and
/// stable, or a drag-to-reorder turns into "the tile jumped somewhere else".
/// </summary>
public static class MonitoringMapFlow
{
    /// <summary>Renumbers <paramref name="tiles"/> to a dense 0..n-1 order, preserving their current relative
    /// order and breaking ties by the order they appear in the list. Mutates the tiles and returns them in
    /// flow order. Idempotent.</summary>
    public static IReadOnlyList<MonitoringMapTile> Normalize(IEnumerable<MonitoringMapTile> tiles)
    {
        var ordered = tiles
            .Select((tile, index) => (tile, index))
            .OrderBy(entry => entry.tile.Order)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.tile)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i;
        }

        return ordered;
    }

    /// <summary>Moves the tile at <paramref name="from"/> to <paramref name="to"/> in the flow and renumbers.
    /// Out-of-range indexes are clamped rather than throwing - a drag that ends past the last tile means
    /// "put it last", which is what the user just did with the mouse.</summary>
    public static IReadOnlyList<MonitoringMapTile> Move(IEnumerable<MonitoringMapTile> tiles, int from, int to)
    {
        var ordered = Normalize(tiles).ToList();
        if (ordered.Count == 0)
        {
            return ordered;
        }

        from = Math.Clamp(from, 0, ordered.Count - 1);
        to = Math.Clamp(to, 0, ordered.Count - 1);
        if (from == to)
        {
            return ordered;
        }

        var moved = ordered[from];
        ordered.RemoveAt(from);
        ordered.Insert(to, moved);

        // Renumber directly - NOT via Normalize, which sorts by the Order values the tiles still carry from
        // before the move and would therefore put the list straight back the way it was.
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i;
        }

        return ordered;
    }
}
