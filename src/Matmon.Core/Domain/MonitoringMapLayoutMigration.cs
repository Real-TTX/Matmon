namespace Matmon.Core.Domain;

/// <summary>
/// One-time, idempotent conversion of a map's tile geometry from the legacy v0 grid-cell scheme (1-based
/// Columns x Rows cells) to the v1 free logical-px canvas (<see cref="MonitoringMap.LogicalWidth"/> x
/// <see cref="MonitoringMap.LogicalHeight"/>) that the WYSIWYG editor/viewer/public-wallboard all render
/// identically. Pure and framework-free so it is unit-testable without a store.
/// </summary>
public static class MonitoringMapLayoutMigration
{
    /// <summary>The legacy CSS grid's cell gap, preserved as logical px so migrated tiles keep the same visual
    /// spacing they had under the old layout instead of touching their neighbours.</summary>
    private const int Gap = 12;

    /// <summary>Converts <paramref name="map"/> in place. A no-op once the map is already on
    /// <see cref="MonitoringMap.CurrentLayoutVersion"/> (idempotent - safe to call on every load/save).</summary>
    public static void MigrateToLogical(MonitoringMap map)
    {
        if (map.LayoutVersion >= MonitoringMap.CurrentLayoutVersion)
        {
            return;
        }

        var aspect = map.EffectiveAspect();
        var (logicalWidth, logicalHeight) = MonitoringMap.LogicalSizeFor(aspect.Width, aspect.Height);
        map.LogicalWidth = logicalWidth;
        map.LogicalHeight = logicalHeight;

        var columns = Math.Max(1, map.Columns);
        var rows = Math.Max(1, map.Rows);
        var cellWidth = (double)logicalWidth / columns;
        var cellHeight = (double)logicalHeight / rows;

        foreach (var slide in map.Slides)
        {
            MigrateTiles(slide.Tiles, cellWidth, cellHeight, logicalWidth, logicalHeight);
        }

        // The legacy single-board Tiles list mirrors slide 1 but is a separate set of tile instances (cloned at
        // save time), so it needs the same conversion applied independently.
        MigrateTiles(map.Tiles, cellWidth, cellHeight, logicalWidth, logicalHeight);

        // A v0 map always had a live anonymous link (the feature was not opt-in yet); a v1 map created fresh
        // defaults PublicEnabled to false. Since this method only runs for a v0 map (guarded above), setting
        // it unconditionally here is exactly "only for v0 maps".
        map.PublicEnabled = true;
        map.LayoutVersion = MonitoringMap.CurrentLayoutVersion;
    }

    private static void MigrateTiles(List<MonitoringMapTile> tiles, double cellWidth, double cellHeight, int canvasWidth, int canvasHeight)
    {
        foreach (var tile in tiles)
        {
            var x = (int)Math.Round((tile.X - 1) * cellWidth, MidpointRounding.AwayFromZero) + Gap / 2;
            var y = (int)Math.Round((tile.Y - 1) * cellHeight, MidpointRounding.AwayFromZero) + Gap / 2;
            var width = (int)Math.Round(tile.Width * cellWidth, MidpointRounding.AwayFromZero) - Gap;
            var height = (int)Math.Round(tile.Height * cellHeight, MidpointRounding.AwayFromZero) - Gap;

            // Snap onto the design grid, exactly as the designer and the store's save-time normalization do.
            // Without this the migrated rect (e.g. 6/468 - the half-gap is not a multiple of SnapGrid) differs
            // from what the editor shows the moment it loads, so merely opening and saving a migrated map would
            // silently shift every tile by a few px.
            tile.X = MonitoringMapTileConstraints.Snap(x);
            tile.Y = MonitoringMapTileConstraints.Snap(y);
            tile.Width = MonitoringMapTileConstraints.Snap(width);
            tile.Height = MonitoringMapTileConstraints.Snap(height);

            // A legacy tile that hung off the edge of its grid (or was smaller than the new kind minimum)
            // is clamped into the canvas rather than rejected - see MonitoringMapTileConstraints.Clamp.
            MonitoringMapTileConstraints.Clamp(tile, canvasWidth, canvasHeight);
        }
    }
}
