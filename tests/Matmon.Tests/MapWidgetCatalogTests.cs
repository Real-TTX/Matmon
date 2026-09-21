using Matmon.Core.Domain;
using Matmon.Host.Ui;

namespace Matmon.Tests;

/// <summary>
/// Invariants of the map editor's widget palette and its layout templates. These are all things a reviewer
/// cannot see by reading the table: a template slot that is one row too short for a Graph gets silently
/// clamped at runtime and then overlaps its neighbour (which is exactly what happened while authoring the
/// "Overview" layout), and a typo in an icon key just renders nothing.
/// </summary>
public class MapWidgetCatalogTests
{
    [Fact]
    public void Widget_keys_are_unique()
    {
        var duplicates = MapWidgetCatalog.All
            .GroupBy(widget => widget.Key)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_widget_uses_a_real_icon()
    {
        var known = MatmonIcons.Keys.ToHashSet();
        var missing = MapWidgetCatalog.All
            .Where(widget => !known.Contains(widget.IconKey))
            .Select(widget => $"{widget.Key} -> {widget.IconKey}")
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_widget_has_a_label_and_a_description()
    {
        Assert.All(MapWidgetCatalog.All, widget =>
        {
            Assert.False(string.IsNullOrWhiteSpace(widget.Label));
            Assert.False(string.IsNullOrWhiteSpace(widget.Description));
        });
    }

    [Fact]
    public void Layout_slots_reference_a_widget_that_exists()
    {
        var unknown = MapWidgetCatalog.LayoutTemplates
            .SelectMany(template => template.Slots.Select(slot => (template.Key, slot.WidgetKey)))
            .Where(pair => MapWidgetCatalog.Find(pair.WidgetKey) is null)
            .Select(pair => $"{pair.Key} -> {pair.WidgetKey}")
            .ToArray();

        Assert.Empty(unknown);
    }

    [Fact]
    public void Layout_slots_fit_inside_the_grid_the_template_declares()
    {
        var overflowing = MapWidgetCatalog.LayoutTemplates
            .SelectMany(template => template.Slots.Select(slot => (template, slot)))
            .Where(pair => pair.slot.Column < 1
                || pair.slot.Row < 1
                || pair.slot.Column + pair.slot.ColumnSpan - 1 > pair.template.MinColumns
                || pair.slot.Row + pair.slot.RowSpan - 1 > pair.template.MinRows)
            .Select(pair => $"{pair.template.Key}: {pair.slot.WidgetKey} at {pair.slot.Column},{pair.slot.Row} {pair.slot.ColumnSpan}x{pair.slot.RowSpan}")
            .ToArray();

        Assert.Empty(overflowing);
    }

    [Fact]
    public void Layout_slots_honour_the_tile_kind_minimum_span()
    {
        // A slot smaller than MonitoringMapTileConstraints allows is not rejected at runtime - it is clamped
        // UP, which pushes the tile into whatever sits next to it. So the template has to be right here.
        var tooSmall = new List<string>();
        foreach (var template in MapWidgetCatalog.LayoutTemplates)
        {
            foreach (var slot in template.Slots)
            {
                var widget = MapWidgetCatalog.Find(slot.WidgetKey);
                if (widget is null)
                {
                    continue;
                }

                var (minColumns, minRows, _, _) = MonitoringMapTileConstraints.For(widget.Kind);
                if (slot.ColumnSpan < minColumns || slot.RowSpan < minRows)
                {
                    tooSmall.Add($"{template.Key}: {slot.WidgetKey} is {slot.ColumnSpan}x{slot.RowSpan}, minimum is {minColumns}x{minRows}");
                }
            }
        }

        Assert.Empty(tooSmall);
    }

    [Fact]
    public void Layout_slots_do_not_overlap()
    {
        var overlaps = new List<string>();
        foreach (var template in MapWidgetCatalog.LayoutTemplates)
        {
            var occupied = new HashSet<(int Column, int Row)>();
            foreach (var slot in template.Slots)
            {
                for (var column = slot.Column; column < slot.Column + slot.ColumnSpan; column++)
                {
                    for (var row = slot.Row; row < slot.Row + slot.RowSpan; row++)
                    {
                        if (!occupied.Add((column, row)))
                        {
                            overlaps.Add($"{template.Key}: cell {column},{row} is used twice");
                        }
                    }
                }
            }
        }

        Assert.Empty(overlaps);
    }

    [Fact]
    public void Every_tile_kind_is_reachable_from_the_palette()
    {
        // If a kind exists but nothing in the palette creates it, it is dead weight the user can never place.
        var offered = MapWidgetCatalog.All.Select(widget => widget.Kind).ToHashSet();
        var unreachable = Enum.GetValues<MonitoringMapTileKind>().Where(kind => !offered.Contains(kind)).ToArray();

        Assert.Empty(unreachable);
    }
}
