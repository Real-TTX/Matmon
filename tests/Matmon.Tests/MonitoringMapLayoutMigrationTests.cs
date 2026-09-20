using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// Covers the v0 (grid-cell) -> v1 (logical-px) tile geometry conversion that runs once for every map loaded
/// from an older workspace.json. The formula (cellWidth/cellHeight from Columns/Rows, half-gap inset, then
/// Constraints.Clamp) is pure/deterministic, so the expected px rects here are hand-computed - see the plan's
/// "Kern-Architektur" note for the derivation.
/// </summary>
public class MonitoringMapLayoutMigrationTests
{
    [Fact]
    public void MigrateToLogical_converts_a_grid_cell_tile_to_the_expected_px_rect()
    {
        // Default 16:9 map (no explicit aspect ratio -> FullHd1080 preset, which reduces to the same 16:9
        // ratio), default 12x8 grid. cellWidth = 1920/12 = 160, cellHeight = 1080/8 = 135.
        var map = new MonitoringMap { Columns = 12, Rows = 8 };
        var tile = new MonitoringMapTile { X = 1, Y = 1, Width = 3, Height = 2, Kind = MonitoringMapTileKind.Element };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { tile } });

        MonitoringMapLayoutMigration.MigrateToLogical(map);

        Assert.Equal(1920, map.LogicalWidth);
        Assert.Equal(1080, map.LogicalHeight);
        Assert.Equal(1, map.LayoutVersion);

        // Raw:     x = round((1-1)*160) + gap/2 = 6;  width  = round(3*160) - gap = 468
        //          y = round((1-1)*135) + gap/2 = 6;  height = round(2*135) - gap = 258
        // Snapped onto the 8px design grid (the canonical form the editor + store also produce):
        //          6 -> 8, 468 -> 472, 258 -> 256
        Assert.Equal(8, tile.X);
        Assert.Equal(8, tile.Y);
        Assert.Equal(472, tile.Width);
        Assert.Equal(256, tile.Height);
    }

    [Fact]
    public void MigrateToLogical_leaves_every_tile_on_the_snap_grid()
    {
        // The regression this guards: a migrated rect that is off-grid renders one way on the wallboard and,
        // the instant the designer loads it (which snaps), another - so opening + saving silently moved tiles.
        var map = new MonitoringMap { Columns = 12, Rows = 8 };
        map.Slides.Add(new MonitoringMapSlide
        {
            Tiles =
            {
                new MonitoringMapTile { X = 1, Y = 1, Width = 3, Height = 2 },
                new MonitoringMapTile { X = 5, Y = 4, Width = 4, Height = 3 },
                new MonitoringMapTile { X = 9, Y = 7, Width = 2, Height = 1 }
            }
        });

        MonitoringMapLayoutMigration.MigrateToLogical(map);

        foreach (var tile in map.Slides[0].Tiles)
        {
            Assert.Equal(0, tile.X % MonitoringMapTileConstraints.SnapGrid);
            Assert.Equal(0, tile.Y % MonitoringMapTileConstraints.SnapGrid);
            Assert.Equal(0, tile.Width % MonitoringMapTileConstraints.SnapGrid);
            Assert.Equal(0, tile.Height % MonitoringMapTileConstraints.SnapGrid);
        }
    }

    [Fact]
    public void MigrateToLogical_derives_ultrawide_height_from_the_aspect_ratio()
    {
        var map = new MonitoringMap { AspectRatioWidth = 21, AspectRatioHeight = 9 };

        MonitoringMapLayoutMigration.MigrateToLogical(map);

        Assert.Equal(1920, map.LogicalWidth);
        Assert.Equal(823, map.LogicalHeight);
    }

    [Fact]
    public void MigrateToLogical_is_idempotent()
    {
        var map = new MonitoringMap { Columns = 12, Rows = 8 };
        var tile = new MonitoringMapTile { X = 2, Y = 3, Width = 4, Height = 2 };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { tile } });

        MonitoringMapLayoutMigration.MigrateToLogical(map);
        var (x, y, width, height) = (tile.X, tile.Y, tile.Width, tile.Height);
        var (logicalWidth, logicalHeight) = (map.LogicalWidth, map.LogicalHeight);

        // A second pass must be a no-op: the map is already on CurrentLayoutVersion, so re-running the (now
        // px-based) formula on already-migrated px values must NOT happen.
        MonitoringMapLayoutMigration.MigrateToLogical(map);

        Assert.Equal(x, tile.X);
        Assert.Equal(y, tile.Y);
        Assert.Equal(width, tile.Width);
        Assert.Equal(height, tile.Height);
        Assert.Equal(logicalWidth, map.LogicalWidth);
        Assert.Equal(logicalHeight, map.LogicalHeight);
    }

    [Fact]
    public void MigrateToLogical_clamps_a_tile_that_hung_off_the_legacy_grid()
    {
        // X=50 on a 12-column grid is far off-grid (a corrupt/hand-edited workspace.json, or a tile that was
        // never properly clamped pre-migration). The raw formula would place it thousands of px past the
        // canvas edge; Constraints.Clamp must pull it back in bounds.
        var map = new MonitoringMap { Columns = 12, Rows = 8 };
        var tile = new MonitoringMapTile { X = 50, Y = 1, Width = 3, Height = 2 };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { tile } });

        MonitoringMapLayoutMigration.MigrateToLogical(map);

        Assert.InRange(tile.X, 0, map.LogicalWidth - tile.Width);
        Assert.InRange(tile.Y, 0, map.LogicalHeight - tile.Height);
    }

    [Fact]
    public void MigrateToLogical_enables_the_public_link_only_for_a_v0_map()
    {
        var legacyMap = new MonitoringMap { LayoutVersion = 0, PublicEnabled = false };
        MonitoringMapLayoutMigration.MigrateToLogical(legacyMap);
        Assert.True(legacyMap.PublicEnabled);

        var alreadyMigratedMap = new MonitoringMap { LayoutVersion = MonitoringMap.CurrentLayoutVersion, PublicEnabled = false };
        MonitoringMapLayoutMigration.MigrateToLogical(alreadyMigratedMap);
        Assert.False(alreadyMigratedMap.PublicEnabled);
    }

    [Fact]
    public void MigrateToLogical_converts_both_slide_tiles_and_legacy_tiles()
    {
        var map = new MonitoringMap { Columns = 12, Rows = 8 };
        map.Tiles.Add(new MonitoringMapTile { X = 1, Y = 1, Width = 3, Height = 2 });
        map.Slides.Add(new MonitoringMapSlide { Tiles = { new MonitoringMapTile { X = 1, Y = 1, Width = 3, Height = 2 } } });

        MonitoringMapLayoutMigration.MigrateToLogical(map);

        Assert.Equal(472, map.Tiles[0].Width);
        Assert.Equal(472, map.Slides[0].Tiles[0].Width);
    }
}
