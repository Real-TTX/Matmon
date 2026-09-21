using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// Covers both tile-geometry conversions that run once for a map loaded from an older workspace.json:
/// v0 (grid cells, 1-based Columns x Rows) -> v2 is essentially identity (the fields are already cell
/// coordinates - see the JsonPropertyName aliasing on MonitoringMapTile), just clamped to the kind's minimum
/// span; v1 (Phase A logical px) -> v2 inverts MonitoringMapGeometry.PixelRect to find the nearest cell. Both
/// are pure/deterministic, so the expected rects here are hand-computed - see the plan's "Kern-Architektur"
/// note for the derivation.
/// </summary>
public class MonitoringMapLayoutMigrationTests
{
    [Fact]
    public void MigrateToCells_v0_grid_cell_tile_is_essentially_identity()
    {
        // v0 tiles already ARE 1-based cell coordinates under the current field names - migrating from v0
        // is just a clamp, no math.
        var map = new MonitoringMap { LayoutVersion = 0, Columns = 12, Rows = 8 };
        var tile = new MonitoringMapTile { Column = 1, Row = 1, ColumnSpan = 3, RowSpan = 2, Kind = MonitoringMapTileKind.Element };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { tile } });

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(1920, map.LogicalWidth);
        Assert.Equal(1080, map.LogicalHeight);
        Assert.Equal(MonitoringMap.CurrentLayoutVersion, map.LayoutVersion);
        Assert.Equal(1, tile.Column);
        Assert.Equal(1, tile.Row);
        Assert.Equal(3, tile.ColumnSpan);
        Assert.Equal(2, tile.RowSpan);
    }

    [Fact]
    public void MigrateToCells_v0_clamps_a_tile_that_hung_off_the_legacy_grid()
    {
        // Column=50 on a 12-column grid is far off-grid (a corrupt/hand-edited workspace.json, or a tile that
        // was never properly clamped pre-migration). Constraints.Clamp must pull it back in bounds.
        var map = new MonitoringMap { LayoutVersion = 0, Columns = 12, Rows = 8 };
        var tile = new MonitoringMapTile { Column = 50, Row = 1, ColumnSpan = 3, RowSpan = 2 };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { tile } });

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.InRange(tile.Column, 1, map.Columns - tile.ColumnSpan + 1);
        Assert.InRange(tile.Row, 1, map.Rows - tile.RowSpan + 1);
    }

    [Fact]
    public void MigrateToCells_v1_converts_a_logical_px_tile_to_the_nearest_cell_rect()
    {
        // Default 16:9 map (1920x1080), default TilePadding=16/OuterMargin=24, Columns=12/Rows=8 (whatever the
        // map happened to carry from before - Phase A never touched them).
        // cellWidth = (1920 - 48 - 11*16) / 12 = 1696/12 = 141.333..., cellHeight = (1080 - 48 - 7*16)/8 = 115.
        // A tile at px (8, 8, 472, 256) - deliberately the same rect the OLD v0->v1 migration used to produce
        // for a (Column=1, Row=1, ColumnSpan=3, RowSpan=2) v0 tile, so this test also demonstrates that a
        // v0 -> v1 -> v2 round-trip recovers the original cell rect.
        var map = new MonitoringMap { LayoutVersion = 1, Columns = 12, Rows = 8 };
        var tile = new MonitoringMapTile { Column = 8, Row = 8, ColumnSpan = 472, RowSpan = 256, Kind = MonitoringMapTileKind.Element };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { tile } });

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(MonitoringMap.CurrentLayoutVersion, map.LayoutVersion);
        Assert.Equal(1, tile.Column);
        Assert.Equal(1, tile.Row);
        Assert.Equal(3, tile.ColumnSpan);
        Assert.Equal(2, tile.RowSpan);
    }

    [Fact]
    public void MigrateToCells_v1_derives_ultrawide_height_from_the_aspect_ratio()
    {
        var map = new MonitoringMap { LayoutVersion = 1, AspectRatioWidth = 21, AspectRatioHeight = 9 };

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(1920, map.LogicalWidth);
        Assert.Equal(823, map.LogicalHeight);
    }

    [Fact]
    public void MigrateToCells_is_idempotent()
    {
        var map = new MonitoringMap { LayoutVersion = 0, Columns = 12, Rows = 8 };
        var tile = new MonitoringMapTile { Column = 2, Row = 3, ColumnSpan = 4, RowSpan = 2 };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { tile } });

        MonitoringMapLayoutMigration.MigrateToCells(map);
        var (column, row, columnSpan, rowSpan) = (tile.Column, tile.Row, tile.ColumnSpan, tile.RowSpan);
        var (logicalWidth, logicalHeight) = (map.LogicalWidth, map.LogicalHeight);

        // A second pass must be a no-op: the map is already on CurrentLayoutVersion, so re-running the
        // migration on already-migrated cell values must NOT happen.
        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(column, tile.Column);
        Assert.Equal(row, tile.Row);
        Assert.Equal(columnSpan, tile.ColumnSpan);
        Assert.Equal(rowSpan, tile.RowSpan);
        Assert.Equal(logicalWidth, map.LogicalWidth);
        Assert.Equal(logicalHeight, map.LogicalHeight);
    }

    [Fact]
    public void MigrateToCells_enables_the_public_link_only_for_an_older_map()
    {
        var legacyMap = new MonitoringMap { LayoutVersion = 0, PublicEnabled = false };
        MonitoringMapLayoutMigration.MigrateToCells(legacyMap);
        Assert.True(legacyMap.PublicEnabled);

        var phaseAMap = new MonitoringMap { LayoutVersion = 1, PublicEnabled = false };
        MonitoringMapLayoutMigration.MigrateToCells(phaseAMap);
        Assert.True(phaseAMap.PublicEnabled);

        var alreadyMigratedMap = new MonitoringMap { LayoutVersion = MonitoringMap.CurrentLayoutVersion, PublicEnabled = false };
        MonitoringMapLayoutMigration.MigrateToCells(alreadyMigratedMap);
        Assert.False(alreadyMigratedMap.PublicEnabled);
    }

    [Fact]
    public void MigrateToCells_converts_both_slide_tiles_and_legacy_tiles()
    {
        var map = new MonitoringMap { LayoutVersion = 0, Columns = 12, Rows = 8 };
        map.Tiles.Add(new MonitoringMapTile { Column = 1, Row = 1, ColumnSpan = 3, RowSpan = 2 });
        map.Slides.Add(new MonitoringMapSlide { Tiles = { new MonitoringMapTile { Column = 1, Row = 1, ColumnSpan = 3, RowSpan = 2 } } });

        MonitoringMapLayoutMigration.MigrateToCells(map);

        Assert.Equal(3, map.Tiles[0].ColumnSpan);
        Assert.Equal(3, map.Slides[0].Tiles[0].ColumnSpan);
    }
}
