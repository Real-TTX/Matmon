using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// The tile-geometry conversions that run once for a map loaded from an older workspace.json, all landing on
/// v3 (the column FLOW): v0 (grid cells) and v1 (Phase A logical px) first become cells, and every version
/// then turns those cells into a reading ORDER - top row first, then left to right, which is the order a
/// person would have read the board in anyway, so a migrated board comes back looking like itself.
/// </summary>
public class MonitoringMapLayoutMigrationTests
{
    [Fact]
    public void Migrate_v2_cells_become_a_reading_order()
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
        // Width and height carry over; the cell coordinates are dead and are cleared so nothing trusts them.
        Assert.Equal(3, topLeft.ColumnSpan);
        Assert.Equal(2, topLeft.RowSpan);
        Assert.Equal(0, topLeft.Column);
        Assert.Equal(0, topLeft.Row);
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
        Assert.Equal(3, tile.ColumnSpan);
        Assert.Equal(2, tile.RowSpan);
    }

    [Fact]
    public void Migrate_v1_logical_px_lands_on_the_same_size_the_cells_had()
    {
        // Default 16:9 map (1920x1080), TilePadding=16/OuterMargin=24, Columns=12/Rows=8:
        // cellWidth = (1920 - 48 - 11*16)/12 = 141.333..., cellHeight = (1080 - 48 - 7*16)/8 = 115.
        // The px rect below is exactly what the old v0->v1 step produced for a 3x2 cell tile, so this also
        // shows that a v0 -> v1 -> v3 round-trip recovers the original size.
        var map = new MonitoringMap { LayoutVersion = 1, Columns = 12, Rows = 8 };
        var tile = new MonitoringMapTile { Column = 8, Row = 8, ColumnSpan = 472, RowSpan = 256, Kind = MonitoringMapTileKind.Element };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { tile } });

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(MonitoringMap.CurrentLayoutVersion, map.LayoutVersion);
        Assert.Equal(0, tile.Order);
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
        var snapshot = map.Slides[0].Tiles.Select(tile => (tile.Order, tile.ColumnSpan, tile.RowSpan)).ToArray();

        // A second pass must be a no-op: the map is already on CurrentLayoutVersion, so nothing re-runs.
        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(snapshot, map.Slides[0].Tiles.Select(tile => (tile.Order, tile.ColumnSpan, tile.RowSpan)).ToArray());
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

        // v2 already had the opt-in toggle, so the cells-to-flow step must not silently publish a board.
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
