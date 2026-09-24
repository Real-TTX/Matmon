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

        // A v3 board has no positions at all - only the flow order the browser was laying out. Replaying
        // that same auto-placement turns it into cells that look exactly like what was already on screen.
        if (map.LayoutVersion == 3)
        {
            PackSlides(map);
            map.LayoutVersion = MonitoringMap.CurrentLayoutVersion;
            return;
        }

        // A v2 board is already in cells - they only need normalising onto the current rules.
        if (map.LayoutVersion == 2)
        {
            PlaceSlides(map);
            map.LayoutVersion = MonitoringMap.CurrentLayoutVersion;
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

        // ...and then the same normalisation a v2 board takes, so every version lands on v4.
        PlaceSlides(map);

        // Both v0 and v1 predate the opt-in public link (it was always live); a fresh map defaults
        // PublicEnabled to false, so this restores what those boards actually did. Only reached for a v0/v1
        // map (the v2 branch below returns before this).
        map.PublicEnabled = true;
        map.LayoutVersion = MonitoringMap.CurrentLayoutVersion;
    }

    /// <summary>v0/v2 (cells) -> v4. The coordinates carry over as they are; widths are unchanged and heights
    /// keep their number, only changing meaning (grid rows spanned -> row units tall), which is the same
    /// thing measured the same way. Placement rules (clamp into the grid, reading order) are applied by the
    /// same code a save runs through, so a migrated board obeys the same invariants as an edited one.</summary>
    private static void PlaceSlides(MonitoringMap map)
    {
        foreach (var slide in map.Slides)
        {
            ClampTiles(slide.Tiles, map);
            MonitoringMapPlacement.Normalize(slide.Tiles, map.Columns);
        }

        ClampTiles(map.Tiles, map);
        MonitoringMapPlacement.Normalize(map.Tiles, map.Columns);
    }

    /// <summary>v3 (flow) -> v4 (cells). See <see cref="MonitoringMapPlacement.PackFromOrder"/>: the stored
    /// order is replayed through the browser's own auto-placement, so the board lands on the arrangement it
    /// was already being drawn as rather than on a re-flowed one.</summary>
    private static void PackSlides(MonitoringMap map)
    {
        foreach (var slide in map.Slides)
        {
            ClampTiles(slide.Tiles, map);
            MonitoringMapPlacement.PackFromOrder(slide.Tiles, map.Columns);
        }

        ClampTiles(map.Tiles, map);
        MonitoringMapPlacement.PackFromOrder(map.Tiles, map.Columns);
    }

    private static void ClampTiles(List<MonitoringMapTile> tiles, MonitoringMap map)
    {
        foreach (var tile in tiles)
        {
            MonitoringMapTileConstraints.Clamp(tile, map.Columns, map.Rows);
        }
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
