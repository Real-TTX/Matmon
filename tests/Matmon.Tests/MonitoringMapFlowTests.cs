using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// v3 lays a slide out as an ordered flow rather than as x/y placements, so the ORDER is the layout. These
/// cover the one rule the whole model rests on: it stays dense and stable, otherwise a drag-to-reorder reads
/// as "the tile jumped somewhere else".
/// </summary>
public class MonitoringMapFlowTests
{
    private static MonitoringMapTile Tile(string title, int order) =>
        new() { Title = title, Order = order };

    [Fact]
    public void Normalize_makes_the_order_dense_without_reshuffling()
    {
        var tiles = new[] { Tile("c", 40), Tile("a", 5), Tile("b", 17) };

        var ordered = MonitoringMapFlow.Normalize(tiles);

        Assert.Equal(new[] { "a", "b", "c" }, ordered.Select(tile => tile.Title));
        Assert.Equal(new[] { 0, 1, 2 }, ordered.Select(tile => tile.Order));
    }

    [Fact]
    public void Normalize_keeps_list_order_for_ties()
    {
        // Every tile at 0 is what a freshly imported / hand-edited board looks like; it must come back in the
        // order it was written, not in some hash order.
        var tiles = new[] { Tile("first", 0), Tile("second", 0), Tile("third", 0) };

        var ordered = MonitoringMapFlow.Normalize(tiles);

        Assert.Equal(new[] { "first", "second", "third" }, ordered.Select(tile => tile.Title));
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var tiles = new[] { Tile("a", 3), Tile("b", 1) };

        var once = MonitoringMapFlow.Normalize(tiles).Select(tile => (tile.Title, tile.Order)).ToArray();
        var twice = MonitoringMapFlow.Normalize(tiles).Select(tile => (tile.Title, tile.Order)).ToArray();

        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData(0, 2, new[] { "b", "c", "a" })]
    [InlineData(2, 0, new[] { "c", "a", "b" })]
    [InlineData(1, 1, new[] { "a", "b", "c" })]
    public void Move_reorders_and_renumbers(int from, int to, string[] expected)
    {
        var tiles = new[] { Tile("a", 0), Tile("b", 1), Tile("c", 2) };

        var ordered = MonitoringMapFlow.Move(tiles, from, to);

        Assert.Equal(expected, ordered.Select(tile => tile.Title));
        Assert.Equal(new[] { 0, 1, 2 }, ordered.Select(tile => tile.Order));
    }

    [Fact]
    public void Move_clamps_instead_of_throwing()
    {
        // A drag that ends past the last tile means "put it last" - that is what the user just did with the
        // mouse, and throwing there would lose the edit.
        var tiles = new[] { Tile("a", 0), Tile("b", 1) };

        var ordered = MonitoringMapFlow.Move(tiles, 0, 99);

        Assert.Equal(new[] { "b", "a" }, ordered.Select(tile => tile.Title));
    }

    [Fact]
    public void Move_on_an_empty_slide_is_harmless()
    {
        var ordered = MonitoringMapFlow.Move([], 0, 3);

        Assert.Empty(ordered);
    }
}
