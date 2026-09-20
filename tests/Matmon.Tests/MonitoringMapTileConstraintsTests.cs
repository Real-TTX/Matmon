using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// <see cref="MonitoringMapTileConstraints"/> is the single min-size/clamp table shared by the store's
/// save-time normalization, the layout migration and (mirrored) the designer's JS - replacing two rival
/// tables that could silently drift apart (the store's grid-cell MapTileSizeLimits and the JS sizeLimits).
/// </summary>
public class MonitoringMapTileConstraintsTests
{
    [Fact]
    public void Clamp_enforces_the_kind_minimum_size()
    {
        var tile = new MonitoringMapTile { Kind = MonitoringMapTileKind.Graph, X = 0, Y = 0, Width = 10, Height = 10 };

        MonitoringMapTileConstraints.Clamp(tile, canvasWidth: 1920, canvasHeight: 1080);

        var (minWidth, minHeight, _, _) = MonitoringMapTileConstraints.For(MonitoringMapTileKind.Graph);
        Assert.Equal(minWidth, tile.Width);
        Assert.Equal(minHeight, tile.Height);
    }

    [Fact]
    public void Clamp_keeps_the_tile_fully_inside_the_canvas()
    {
        var tile = new MonitoringMapTile { Kind = MonitoringMapTileKind.Value, X = 1900, Y = 1070, Width = 240, Height = 160 };

        MonitoringMapTileConstraints.Clamp(tile, canvasWidth: 1920, canvasHeight: 1080);

        Assert.InRange(tile.X, 0, 1920 - tile.Width);
        Assert.InRange(tile.Y, 0, 1080 - tile.Height);
        Assert.True(tile.X + tile.Width <= 1920);
        Assert.True(tile.Y + tile.Height <= 1080);
    }

    [Fact]
    public void Clamp_pulls_a_negative_position_back_onto_the_canvas()
    {
        var tile = new MonitoringMapTile { Kind = MonitoringMapTileKind.Text, X = -500, Y = -50, Width = 320, Height = 120 };

        MonitoringMapTileConstraints.Clamp(tile, canvasWidth: 1920, canvasHeight: 1080);

        Assert.Equal(0, tile.X);
        Assert.Equal(0, tile.Y);
    }

    [Fact]
    public void Clamp_shrinks_the_minimum_when_the_canvas_itself_is_smaller()
    {
        // A degenerate/very small canvas must not leave the tile wider than the canvas even though that is
        // below the kind's normal minimum - Clamp falls back to the canvas size in that case.
        var tile = new MonitoringMapTile { Kind = MonitoringMapTileKind.Status, X = 0, Y = 0, Width = 400, Height = 160 };

        MonitoringMapTileConstraints.Clamp(tile, canvasWidth: 100, canvasHeight: 60);

        Assert.Equal(100, tile.Width);
        Assert.Equal(60, tile.Height);
        Assert.Equal(0, tile.X);
        Assert.Equal(0, tile.Y);
    }

    [Theory]
    [InlineData(MonitoringMapTileKind.Text)]
    [InlineData(MonitoringMapTileKind.Element)]
    [InlineData(MonitoringMapTileKind.Status)]
    [InlineData(MonitoringMapTileKind.Value)]
    [InlineData(MonitoringMapTileKind.Graph)]
    public void For_returns_a_default_size_that_is_at_least_the_minimum(MonitoringMapTileKind kind)
    {
        var (minWidth, minHeight, defaultWidth, defaultHeight) = MonitoringMapTileConstraints.For(kind);

        Assert.True(defaultWidth >= minWidth);
        Assert.True(defaultHeight >= minHeight);
    }
}
