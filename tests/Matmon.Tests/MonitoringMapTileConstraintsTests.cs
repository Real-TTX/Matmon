using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// <see cref="MonitoringMapTileConstraints"/> is the single min-span/clamp table shared by the store's
/// save-time normalization, the layout migration and (mirrored) the designer's JS - replacing two rival
/// tables that could silently drift apart (the store's grid-cell MapTileSizeLimits and the JS sizeLimits).
/// v3 is a FLOW: a tile has a width in columns and a height in row units, and no position at all - so there
/// is nothing left to clamp it "into the grid", only a width to cap and a height to bound.
/// </summary>
public class MonitoringMapTileConstraintsTests
{
    [Fact]
    public void Clamp_enforces_the_kind_minimum_span()
    {
        var tile = new MonitoringMapTile { Kind = MonitoringMapTileKind.Graph, ColumnSpan = 1, RowSpan = 1 };

        MonitoringMapTileConstraints.Clamp(tile, columns: 12, rows: 6);

        var (minColumns, minRows, _, _) = MonitoringMapTileConstraints.For(MonitoringMapTileKind.Graph);
        Assert.Equal(minColumns, tile.ColumnSpan);
        Assert.Equal(minRows, tile.RowSpan);
    }

    [Fact]
    public void Clamp_caps_the_width_at_the_slide_width()
    {
        var tile = new MonitoringMapTile { Kind = MonitoringMapTileKind.Value, ColumnSpan = 100, RowSpan = 2 };

        MonitoringMapTileConstraints.Clamp(tile, columns: 12, rows: 6);

        Assert.Equal(12, tile.ColumnSpan);
    }

    [Fact]
    public void Clamp_shrinks_the_minimum_when_the_slide_itself_is_narrower()
    {
        // A one-column slide must not leave a tile spanning four columns, even though that is below the
        // kind's normal minimum - the slide width wins.
        var tile = new MonitoringMapTile { Kind = MonitoringMapTileKind.Status, ColumnSpan = 4, RowSpan = 2 };

        MonitoringMapTileConstraints.Clamp(tile, columns: 1, rows: 1);

        Assert.Equal(1, tile.ColumnSpan);
    }

    [Fact]
    public void Clamp_lets_a_tile_be_taller_than_one_screen_but_not_unbounded()
    {
        // A long sensor list is a legitimate thing to scroll to, so the screen height (rows) is NOT a cap -
        // but one runaway tile would shrink every other tile on a scaled wallboard to nothing, so there is
        // still a ceiling.
        var tall = new MonitoringMapTile { Kind = MonitoringMapTileKind.SensorList, ColumnSpan = 4, RowSpan = 10 };
        MonitoringMapTileConstraints.Clamp(tall, columns: 12, rows: 6);
        Assert.Equal(10, tall.RowSpan);

        var runaway = new MonitoringMapTile { Kind = MonitoringMapTileKind.SensorList, ColumnSpan = 4, RowSpan = 9999 };
        MonitoringMapTileConstraints.Clamp(runaway, columns: 12, rows: 6);
        Assert.Equal(MonitoringMapTileConstraints.MaxRowSpan, runaway.RowSpan);
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
