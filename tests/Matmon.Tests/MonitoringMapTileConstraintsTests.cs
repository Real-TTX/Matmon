using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// <see cref="MonitoringMapTileConstraints"/> is the single min-span/clamp table shared by the store's
/// save-time normalization, the layout migration and (mirrored) the designer's JS - replacing two rival
/// tables that could silently drift apart (the store's grid-cell MapTileSizeLimits and the JS sizeLimits).
/// v2 works in grid CELLS (Column/Row/ColumnSpan/RowSpan), not px.
/// </summary>
public class MonitoringMapTileConstraintsTests
{
    [Fact]
    public void Clamp_enforces_the_kind_minimum_span()
    {
        var tile = new MonitoringMapTile { Kind = MonitoringMapTileKind.Graph, Column = 1, Row = 1, ColumnSpan = 1, RowSpan = 1 };

        MonitoringMapTileConstraints.Clamp(tile, columns: 12, rows: 6);

        var (minColumns, minRows, _, _) = MonitoringMapTileConstraints.For(MonitoringMapTileKind.Graph);
        Assert.Equal(minColumns, tile.ColumnSpan);
        Assert.Equal(minRows, tile.RowSpan);
    }

    [Fact]
    public void Clamp_keeps_the_tile_fully_inside_the_grid()
    {
        var tile = new MonitoringMapTile { Kind = MonitoringMapTileKind.Value, Column = 100, Row = 50, ColumnSpan = 2, RowSpan = 2 };

        MonitoringMapTileConstraints.Clamp(tile, columns: 12, rows: 6);

        Assert.InRange(tile.Column, 1, 12 - tile.ColumnSpan + 1);
        Assert.InRange(tile.Row, 1, 6 - tile.RowSpan + 1);
        Assert.True(tile.Column + tile.ColumnSpan - 1 <= 12);
        Assert.True(tile.Row + tile.RowSpan - 1 <= 6);
    }

    [Fact]
    public void Clamp_pulls_a_negative_position_back_onto_the_grid()
    {
        var tile = new MonitoringMapTile { Kind = MonitoringMapTileKind.Text, Column = -500, Row = -50, ColumnSpan = 2, RowSpan = 2 };

        MonitoringMapTileConstraints.Clamp(tile, columns: 12, rows: 6);

        Assert.Equal(1, tile.Column);
        Assert.Equal(1, tile.Row);
    }

    [Fact]
    public void Clamp_shrinks_the_minimum_when_the_grid_itself_is_smaller()
    {
        // A degenerate/very small grid must not leave the tile spanning more cells than the grid has, even
        // though that is below the kind's normal minimum - Clamp falls back to the grid size in that case.
        var tile = new MonitoringMapTile { Kind = MonitoringMapTileKind.Status, Column = 1, Row = 1, ColumnSpan = 4, RowSpan = 2 };

        MonitoringMapTileConstraints.Clamp(tile, columns: 1, rows: 1);

        Assert.Equal(1, tile.ColumnSpan);
        Assert.Equal(1, tile.RowSpan);
        Assert.Equal(1, tile.Column);
        Assert.Equal(1, tile.Row);
    }

    [Theory]
    [InlineData(MonitoringMapTileKind.Text)]
    [InlineData(MonitoringMapTileKind.Element)]
    [InlineData(MonitoringMapTileKind.Status)]
    [InlineData(MonitoringMapTileKind.Value)]
    [InlineData(MonitoringMapTileKind.Graph)]
    public void For_returns_a_default_span_that_is_at_least_the_minimum(MonitoringMapTileKind kind)
    {
        var (minColumns, minRows, defaultColumns, defaultRows) = MonitoringMapTileConstraints.For(kind);

        Assert.True(defaultColumns >= minColumns);
        Assert.True(defaultRows >= minRows);
    }

    [Theory]
    [InlineData(MonitoringMapTileKind.Text)]
    [InlineData(MonitoringMapTileKind.Element)]
    [InlineData(MonitoringMapTileKind.Status)]
    [InlineData(MonitoringMapTileKind.Value)]
    [InlineData(MonitoringMapTileKind.Graph)]
    public void For_never_returns_smaller_than_the_mockups_2x2_floor(MonitoringMapTileKind kind)
    {
        var (minColumns, minRows, _, _) = MonitoringMapTileConstraints.For(kind);

        Assert.True(minColumns >= 2);
        Assert.True(minRows >= 2);
    }
}
