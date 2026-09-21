namespace Matmon.Core.Domain;

/// <summary>
/// One-time, idempotent conversion of a map's tile geometry onto the current v2 cell scheme
/// (<see cref="MonitoringMapTile.Column"/>/<see cref="MonitoringMapTile.Row"/>/
/// <see cref="MonitoringMapTile.ColumnSpan"/>/<see cref="MonitoringMapTile.RowSpan"/>). Two starting points,
/// both guarded on <see cref="MonitoringMap.LayoutVersion"/> so this is a no-op past
/// <see cref="MonitoringMap.CurrentLayoutVersion"/>:
/// <list type="bullet">
/// <item>v0 (the original grid-cell scheme) - the tile's raw fields already ARE 1-based cell coordinates
/// (see the <c>[JsonPropertyName]</c> aliases on <see cref="MonitoringMapTile.Column"/> etc. - the JSON key
/// never changed), so this is essentially an identity pass, just clamped onto the kind's minimum span.</item>
/// <item>v1 (the Phase A free logical-px canvas) - the same raw fields instead hold a px rect, which is
/// converted back to the nearest cell via <see cref="MonitoringMapGeometry.CellRectFromPixels"/> - the exact
/// inverse of the formula the renderer uses.</item>
/// </list>
/// Pure and framework-free so it is unit-testable without a store.
/// </summary>
public static class MonitoringMapLayoutMigration
{
    /// <summary>Converts <paramref name="map"/> in place. A no-op once the map is already on
    /// <see cref="MonitoringMap.CurrentLayoutVersion"/> (idempotent - safe to call on every load/save).</summary>
    public static void MigrateToCells(MonitoringMap map)
    {
        if (map.LayoutVersion >= MonitoringMap.CurrentLayoutVersion)
        {
            return;
        }

        var aspect = map.EffectiveAspect();
        var (logicalWidth, logicalHeight) = MonitoringMap.LogicalSizeFor(aspect.Width, aspect.Height);
        map.LogicalWidth = logicalWidth;
        map.LogicalHeight = logicalHeight;

        // Only a v1 map's raw fields hold px - a v0 map's fields already are cell coordinates (see the
        // JsonPropertyName aliasing on MonitoringMapTile), so no conversion is needed for it, just clamping.
        var fromLogicalPx = map.LayoutVersion == 1;

        foreach (var slide in map.Slides)
        {
            MigrateTiles(slide.Tiles, map, fromLogicalPx);
        }

        // The legacy single-board Tiles list mirrors slide 1 but is a separate set of tile instances (cloned at
        // save time), so it needs the same conversion applied independently.
        MigrateTiles(map.Tiles, map, fromLogicalPx);

        // Both v0 and v1 predate the opt-in public link (it was always live); a fresh v2 map defaults
        // PublicEnabled to false. Since this method only runs for a v0/v1 map (guarded above), setting it
        // unconditionally here is exactly "only for a migrated map".
        map.PublicEnabled = true;
        map.LayoutVersion = MonitoringMap.CurrentLayoutVersion;
    }

    private static void MigrateTiles(List<MonitoringMapTile> tiles, MonitoringMap map, bool fromLogicalPx)
    {
        foreach (var tile in tiles)
        {
            if (fromLogicalPx)
            {
                // v1 (Phase A logical px) -> v2 (cells): invert the SAME cell<->px formula the renderer uses,
                // rounding to the nearest whole cell. At this point Column/Row/ColumnSpan/RowSpan still hold
                // the raw v1 px rect (deserialized via the shared "x"/"y"/"width"/"height" JSON keys) - they
                // are overwritten here with the real cell values.
                var (column, row, columnSpan, rowSpan) = MonitoringMapGeometry.CellRectFromPixels(
                    map, tile.Column, tile.Row, tile.ColumnSpan, tile.RowSpan);
                tile.Column = column;
                tile.Row = row;
                tile.ColumnSpan = columnSpan;
                tile.RowSpan = rowSpan;
            }
            // else v0: Column/Row/ColumnSpan/RowSpan already are the 1-based cell coordinates - identity.

            // A legacy tile that hung off the edge of its grid (or was smaller than the new kind minimum)
            // is clamped into the grid rather than rejected - see MonitoringMapTileConstraints.Clamp.
            MonitoringMapTileConstraints.Clamp(tile, map.Columns, map.Rows);
        }
    }
}
