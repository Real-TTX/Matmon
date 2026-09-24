using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// v4 puts a tile at an explicit cell, which is what makes empty space possible - and what makes the two
/// rules below worth pinning down: an unplaced tile must not land on top of one that is already there, and
/// the reading order the stacked phone view follows must always agree with the positions.
/// </summary>
public class MonitoringMapPlacementTests
{
    private static MonitoringMapTile Tile(string title, int column, int row, int columnSpan = 3, int rowSpan = 2) =>
        new() { Title = title, Column = column, Row = row, ColumnSpan = columnSpan, RowSpan = rowSpan };

    [Fact]
    public void Normalize_numbers_the_reading_order_from_the_positions()
    {
        var tiles = new[] { Tile("bottom", 1, 3), Tile("top right", 7, 1), Tile("top left", 1, 1) };

        var ordered = MonitoringMapPlacement.Normalize(tiles, 12);

        Assert.Equal(["top left", "top right", "bottom"], ordered.Select(tile => tile.Title));
        Assert.Equal([0, 1, 2], ordered.Select(tile => tile.Order));
    }

    [Fact]
    public void Normalize_leaves_a_placed_tile_exactly_where_it_is()
    {
        var tile = Tile("pinned", 5, 4);

        MonitoringMapPlacement.Normalize([tile], 12);

        Assert.Equal(5, tile.Column);
        Assert.Equal(4, tile.Row);
    }

    [Fact]
    public void Normalize_gives_an_unplaced_tile_the_first_free_cell()
    {
        var existing = Tile("existing", 1, 1, columnSpan: 4, rowSpan: 2);
        var fresh = new MonitoringMapTile { Title = "fresh", ColumnSpan = 4, RowSpan = 2 };

        MonitoringMapPlacement.Normalize([existing, fresh], 12);

        Assert.Equal(5, fresh.Column);
        Assert.Equal(1, fresh.Row);
    }

    [Fact]
    public void Normalize_pulls_a_tile_back_inside_a_narrower_grid()
    {
        var tile = Tile("wide", column: 10, row: 1, columnSpan: 6);

        MonitoringMapPlacement.Normalize([tile], 8);

        Assert.Equal(6, tile.ColumnSpan);
        Assert.Equal(3, tile.Column);
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var tiles = new[] { Tile("b", 7, 1), Tile("a", 1, 1), new MonitoringMapTile { Title = "c" } };

        var once = MonitoringMapPlacement.Normalize(tiles, 12).Select(t => (t.Title, t.Column, t.Row, t.Order)).ToArray();
        var twice = MonitoringMapPlacement.Normalize(tiles, 12).Select(t => (t.Title, t.Column, t.Row, t.Order)).ToArray();

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Overlap_is_allowed_because_free_placement_was_the_point()
    {
        var first = Tile("first", 1, 1, columnSpan: 6, rowSpan: 3);
        var second = Tile("second", 3, 2, columnSpan: 6, rowSpan: 3);

        MonitoringMapPlacement.Normalize([first, second], 12);

        Assert.Equal((3, 2), (second.Column, second.Row));
    }

    [Fact]
    public void PackFromOrder_reproduces_the_browsers_own_wrapping()
    {
        // Four 4-wide tiles across 12 columns: three fit on the first band, the fourth wraps. The row a tile
        // lands on is its band's TOP - the second band starts at row 1 + the first band's tallest row span.
        var tiles = new[]
        {
            new MonitoringMapTile { Title = "a", Order = 0, ColumnSpan = 4, RowSpan = 2 },
            new MonitoringMapTile { Title = "b", Order = 1, ColumnSpan = 4, RowSpan = 2 },
            new MonitoringMapTile { Title = "c", Order = 2, ColumnSpan = 4, RowSpan = 2 },
            new MonitoringMapTile { Title = "d", Order = 3, ColumnSpan = 4, RowSpan = 2 }
        };

        MonitoringMapPlacement.PackFromOrder(tiles, 12);

        Assert.Equal((1, 1), (tiles[0].Column, tiles[0].Row));
        Assert.Equal((5, 1), (tiles[1].Column, tiles[1].Row));
        Assert.Equal((9, 1), (tiles[2].Column, tiles[2].Row));
        Assert.Equal(1, tiles[3].Column);
        Assert.True(tiles[3].Row > 1, "the fourth tile has to wrap onto a later row, not sit on top of the first");
    }

    [Fact]
    public void PackFromOrder_never_overlaps_what_it_has_already_placed()
    {
        var tiles = Enumerable.Range(0, 9)
            .Select(i => new MonitoringMapTile { Title = $"t{i}", Order = i, ColumnSpan = 5, RowSpan = 2 })
            .ToArray();

        MonitoringMapPlacement.PackFromOrder(tiles, 12);

        foreach (var (a, b) in tiles.SelectMany(a => tiles.Select(b => (a, b))).Where(pair => pair.a != pair.b))
        {
            var overlaps = a.Column < b.Column + b.ColumnSpan
                && b.Column < a.Column + a.ColumnSpan
                && a.Row < b.Row + b.RowSpan
                && b.Row < a.Row + a.RowSpan;
            Assert.False(overlaps, $"{a.Title} and {b.Title} overlap");
        }
    }
}
