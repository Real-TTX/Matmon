namespace Matmon.Core.Domain;

/// <summary>
/// The v4 layout: a tile sits at an explicit grid CELL (<see cref="MonitoringMapTile.Column"/>/
/// <see cref="MonitoringMapTile.Row"/>, 1-based) and spans <see cref="MonitoringMapTile.ColumnSpan"/> x
/// <see cref="MonitoringMapTile.RowSpan"/> of them. You put a widget where you want it and empty space stays
/// empty - which v3's pure flow could not express, because a flow has no holes.
///
/// Two things this deliberately does NOT do:
/// <list type="bullet">
/// <item>It does not forbid overlap. Free placement and guaranteed non-overlap are the same trade-off seen
/// from two sides, and free placement is the one that was asked for; the designer shows the grid so an
/// overlap is a visible choice rather than an accident.</item>
/// <item>It does not re-pack a board to fit a narrower grid beyond clamping a tile back inside it. A board is
/// laid out for its own column count; a phone gets the stacked one-column reading list instead, in
/// <see cref="MonitoringMapTile.Order"/> sequence.</item>
/// </list>
/// <see cref="MonitoringMapTile.Order"/> survives as exactly that reading order - derived here from the
/// positions, never edited by hand - so the stacked view and the DOM agree without re-sorting in CSS.
/// Pure and framework-free so it is unit-testable without a store.
/// </summary>
public static class MonitoringMapPlacement
{
    /// <summary>Puts every tile at a legal cell and renumbers <see cref="MonitoringMapTile.Order"/> to the
    /// reading order (top row first, then left to right). A tile with no position yet (column or row &lt;= 0,
    /// which is what a freshly created one carries) is given the first free spot instead of landing on top of
    /// whatever is at 1,1. Mutates the tiles and returns them in reading order. Idempotent.</summary>
    public static IReadOnlyList<MonitoringMapTile> Normalize(IEnumerable<MonitoringMapTile> tiles, int columns)
    {
        columns = Math.Max(1, columns);

        // Placed tiles keep their spot and are processed first, so an unplaced one can see them and avoid
        // them. Ties break on input order, which is the order the caller (and the DOM) already had.
        var ordered = tiles
            .Select((tile, index) => (tile, index))
            .OrderBy(entry => IsPlaced(entry.tile) ? 0 : 1)
            .ThenBy(entry => entry.tile.Row)
            .ThenBy(entry => entry.tile.Column)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.tile)
            .ToList();

        var placed = new List<MonitoringMapTile>(ordered.Count);
        foreach (var tile in ordered)
        {
            tile.ColumnSpan = Math.Clamp(tile.ColumnSpan <= 0 ? 1 : tile.ColumnSpan, 1, columns);
            tile.RowSpan = Math.Max(1, tile.RowSpan);

            if (IsPlaced(tile))
            {
                tile.Column = Math.Clamp(tile.Column, 1, columns - tile.ColumnSpan + 1);
                tile.Row = Math.Max(1, tile.Row);
            }
            else
            {
                var (column, row) = FindFreeSpot(placed, columns, tile.ColumnSpan, tile.RowSpan);
                tile.Column = column;
                tile.Row = row;
            }

            placed.Add(tile);
        }

        var reading = placed
            .Select((tile, index) => (tile, index))
            .OrderBy(entry => entry.tile.Row)
            .ThenBy(entry => entry.tile.Column)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.tile)
            .ToList();

        for (var i = 0; i < reading.Count; i++)
        {
            reading[i].Order = i;
        }

        return reading;
    }

    /// <summary>The first cell, scanning left to right then down, where a <paramref name="columnSpan"/> x
    /// <paramref name="rowSpan"/> tile fits without touching any of <paramref name="placed"/>. Always returns
    /// a spot - past the last occupied row there is always room.</summary>
    public static (int Column, int Row) FindFreeSpot(
        IEnumerable<MonitoringMapTile> placed,
        int columns,
        int columnSpan,
        int rowSpan)
    {
        columns = Math.Max(1, columns);
        columnSpan = Math.Clamp(columnSpan, 1, columns);
        rowSpan = Math.Max(1, rowSpan);

        var occupied = placed.ToList();
        var lastRow = occupied.Count == 0 ? 0 : occupied.Max(tile => tile.Row + Math.Max(1, tile.RowSpan) - 1);

        for (var row = 1; row <= lastRow + 1; row++)
        {
            for (var column = 1; column <= columns - columnSpan + 1; column++)
            {
                if (!occupied.Any(other => Intersects(other, column, row, columnSpan, rowSpan)))
                {
                    return (column, row);
                }
            }
        }

        return (1, lastRow + 1);
    }

    /// <summary>v3 (a pure flow) -&gt; v4 (cells): replays the browser's own auto-placement over the stored
    /// <see cref="MonitoringMapTile.Order"/>, so a board migrates to exactly the arrangement it was already
    /// being rendered as. Row-first with a cursor that never goes backwards - CSS grid without
    /// <c>dense</c> - because a board that re-flowed into the holes on upgrade would not look like itself.</summary>
    public static IReadOnlyList<MonitoringMapTile> PackFromOrder(IEnumerable<MonitoringMapTile> tiles, int columns)
    {
        columns = Math.Max(1, columns);

        var ordered = tiles
            .Select((tile, index) => (tile, index))
            .OrderBy(entry => entry.tile.Order)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.tile)
            .ToList();

        var placed = new List<MonitoringMapTile>(ordered.Count);
        var cursorColumn = 1;
        var cursorRow = 1;

        foreach (var tile in ordered)
        {
            var columnSpan = Math.Clamp(tile.ColumnSpan <= 0 ? 1 : tile.ColumnSpan, 1, columns);
            var rowSpan = Math.Max(1, tile.RowSpan);

            while (true)
            {
                if (cursorColumn + columnSpan - 1 > columns)
                {
                    cursorColumn = 1;
                    cursorRow++;
                    continue;
                }

                if (placed.Any(other => Intersects(other, cursorColumn, cursorRow, columnSpan, rowSpan)))
                {
                    cursorColumn++;
                    continue;
                }

                break;
            }

            tile.Column = cursorColumn;
            tile.Row = cursorRow;
            tile.ColumnSpan = columnSpan;
            tile.RowSpan = rowSpan;
            placed.Add(tile);

            cursorColumn += columnSpan;
        }

        return Normalize(placed, columns);
    }

    private static bool IsPlaced(MonitoringMapTile tile) => tile.Column > 0 && tile.Row > 0;

    private static bool Intersects(MonitoringMapTile tile, int column, int row, int columnSpan, int rowSpan)
    {
        var tileColumnSpan = Math.Max(1, tile.ColumnSpan);
        var tileRowSpan = Math.Max(1, tile.RowSpan);

        return tile.Column < column + columnSpan
            && column < tile.Column + tileColumnSpan
            && tile.Row < row + rowSpan
            && row < tile.Row + tileRowSpan;
    }
}
