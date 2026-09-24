using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// The tile-geometry conversions that run once for a map loaded from an older workspace.json, all landing on
/// v4 (explicit cells on a real CSS grid): v0 keeps its cells, v1 (Phase A logical px) converts back to them,
/// and v3 (which had no positions at all) replays the browser's own auto-placement over its stored order - so
/// whichever version a board comes from, it comes back looking like what was already on screen.
/// </summary>
public class MonitoringMapLayoutMigrationTests
{
    [Fact]
    public void Migrate_v2_cells_are_kept_and_gain_a_reading_order()
    {
        var map = new MonitoringMap { LayoutVersion = 2, Columns = 12, Rows = 8 };
        // Deliberately added out of reading order, so the assertion cannot pass by list order alone.
        var bottomLeft = new MonitoringMapTile { Column = 1, Row = 5, ColumnSpan = 3, RowSpan = 2, Kind = MonitoringMapTileKind.Element };
        var topRight = new MonitoringMapTile { Column = 7, Row = 1, ColumnSpan = 3, RowSpan = 2, Kind = MonitoringMapTileKind.Element };
        var topLeft = new MonitoringMapTile { Column = 1, Row = 1, ColumnSpan = 3, RowSpan = 2, Kind = MonitoringMapTileKind.Element };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { bottomLeft, topRight, topLeft } });

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(MonitoringMap.CurrentLayoutVersion, map.LayoutVersion);
        Assert.Equal(0, topLeft.Order);
        Assert.Equal(1, topRight.Order);
        Assert.Equal(2, bottomLeft.Order);
        // v4 renders from the cells again, so they must survive exactly as they were - v3 zeroed them.
        Assert.Equal((1, 1), (topLeft.Column, topLeft.Row));
        Assert.Equal((7, 1), (topRight.Column, topRight.Row));
        Assert.Equal((1, 5), (bottomLeft.Column, bottomLeft.Row));
        Assert.Equal(3, topLeft.ColumnSpan);
        Assert.Equal(2, topLeft.RowSpan);
    }

    [Fact]
    public void Migrate_v3_flow_lands_on_the_arrangement_it_was_already_drawn_as()
    {
        var map = new MonitoringMap { LayoutVersion = 3, Columns = 12, Rows = 8 };
        var first = new MonitoringMapTile { Order = 0, ColumnSpan = 6, RowSpan = 2, Kind = MonitoringMapTileKind.Element };
        var second = new MonitoringMapTile { Order = 1, ColumnSpan = 6, RowSpan = 2, Kind = MonitoringMapTileKind.Element };
        var third = new MonitoringMapTile { Order = 2, ColumnSpan = 6, RowSpan = 2, Kind = MonitoringMapTileKind.Element };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { first, second, third } });

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(MonitoringMap.CurrentLayoutVersion, map.LayoutVersion);
        // Two 6-wide tiles fill the first band; the third wraps - exactly what the browser was drawing.
        Assert.Equal((1, 1), (first.Column, first.Row));
        Assert.Equal((7, 1), (second.Column, second.Row));
        Assert.Equal(1, third.Column);
        Assert.True(third.Row > 1, "the third tile wrapped in the flow, so it must not land on the first band");
    }

    [Fact]
    public void Migrate_v0_grid_cells_keep_their_size_and_gain_an_order()
    {
        var map = new MonitoringMap { LayoutVersion = 0, Columns = 12, Rows = 8 };
        var tile = new MonitoringMapTile { Column = 1, Row = 1, ColumnSpan = 3, RowSpan = 2, Kind = MonitoringMapTileKind.Element };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { tile } });

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(MonitoringMap.CurrentLayoutVersion, map.LayoutVersion);
        Assert.Equal(0, tile.Order);
        Assert.Equal((1, 1), (tile.Column, tile.Row));
        Assert.Equal(3, tile.ColumnSpan);
        Assert.Equal(2, tile.RowSpan);
    }

    [Fact]
    public void Migrate_v1_logical_px_lands_on_the_same_size_the_cells_had()
    {
        // Default 16:9 map (1920x1080), TilePadding=16/OuterMargin=24, Columns=12/Rows=8:
        // cellWidth = (1920 - 48 - 11*16)/12 = 141.333..., cellHeight = (1080 - 48 - 7*16)/8 = 115.
        // The px rect below is exactly what the old v0->v1 step produced for a 3x2 cell tile, so this also
        // shows that a v0 -> v1 -> v4 round-trip recovers the original size AND its cell.
        var map = new MonitoringMap { LayoutVersion = 1, Columns = 12, Rows = 8 };
        var tile = new MonitoringMapTile { Column = 8, Row = 8, ColumnSpan = 472, RowSpan = 256, Kind = MonitoringMapTileKind.Element };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { tile } });

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(MonitoringMap.CurrentLayoutVersion, map.LayoutVersion);
        Assert.Equal(0, tile.Order);
        Assert.Equal((1, 1), (tile.Column, tile.Row));
        Assert.Equal(3, tile.ColumnSpan);
        Assert.Equal(2, tile.RowSpan);
    }

    [Fact]
    public void Migrate_v1_derives_ultrawide_height_from_the_aspect_ratio()
    {
        var map = new MonitoringMap { LayoutVersion = 1, AspectRatioWidth = 21, AspectRatioHeight = 9 };

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(1920, map.LogicalWidth);
        Assert.Equal(823, map.LogicalHeight);
    }

    [Fact]
    public void Migrate_is_idempotent()
    {
        var map = new MonitoringMap { LayoutVersion = 0, Columns = 12, Rows = 8 };
        var first = new MonitoringMapTile { Column = 2, Row = 3, ColumnSpan = 4, RowSpan = 2 };
        var second = new MonitoringMapTile { Column = 6, Row = 1, ColumnSpan = 4, RowSpan = 2 };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { first, second } });

        MonitoringMapLayoutMigration.MigrateToCells(map);
        var snapshot = map.Slides[0].Tiles.Select(tile => (tile.Order, tile.Column, tile.Row, tile.ColumnSpan, tile.RowSpan)).ToArray();

        // A second pass must be a no-op: the map is already on CurrentLayoutVersion, so nothing re-runs.
        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(snapshot, map.Slides[0].Tiles.Select(tile => (tile.Order, tile.Column, tile.Row, tile.ColumnSpan, tile.RowSpan)).ToArray());
    }

    [Fact]
    public void Migrate_enables_the_public_link_only_for_a_pre_v2_map()
    {
        var legacyMap = new MonitoringMap { LayoutVersion = 0, PublicEnabled = false };
        MonitoringMapLayoutMigration.MigrateToCells(legacyMap);
        Assert.True(legacyMap.PublicEnabled);

        var phaseAMap = new MonitoringMap { LayoutVersion = 1, PublicEnabled = false };
        MonitoringMapLayoutMigration.MigrateToCells(phaseAMap);
        Assert.True(phaseAMap.PublicEnabled);

        // v2 already had the opt-in toggle, so the re-placement step must not silently publish a board.
        var cellMap = new MonitoringMap { LayoutVersion = 2, PublicEnabled = false };
        MonitoringMapLayoutMigration.MigrateToCells(cellMap);
        Assert.False(cellMap.PublicEnabled);

        var alreadyMigratedMap = new MonitoringMap { LayoutVersion = MonitoringMap.CurrentLayoutVersion, PublicEnabled = false };
        MonitoringMapLayoutMigration.MigrateToCells(alreadyMigratedMap);
        Assert.False(alreadyMigratedMap.PublicEnabled);
    }

    [Fact]
    public void Migrate_converts_both_slide_tiles_and_legacy_tiles()
    {
        var map = new MonitoringMap { LayoutVersion = 0, Columns = 12, Rows = 8 };
        map.Tiles.Add(new MonitoringMapTile { Column = 1, Row = 1, ColumnSpan = 3, RowSpan = 2 });
        map.Slides.Add(new MonitoringMapSlide { Tiles = { new MonitoringMapTile { Column = 1, Row = 1, ColumnSpan = 3, RowSpan = 2 } } });

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(3, map.Tiles[0].ColumnSpan);
        Assert.Equal(3, map.Slides[0].Tiles[0].ColumnSpan);
    }
}
