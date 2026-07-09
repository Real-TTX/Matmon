using System.Reflection;
using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// Guards the "clone dropped a field" bug class: MonitoringMap.Clone runs on every read of a map, so a
/// field omitted there silently resets to its default (this is exactly how WallboardFit fell back to Fit
/// and a "stretch" wallboard stopped stretching, and how tile IconKey/ShowCard were lost on display).
/// The reflection tests fail automatically the day someone adds a scalar field and forgets to clone it.
/// </summary>
public class MonitoringMapCloneTests
{
    [Fact]
    public void MapClone_preserves_wallboard_aspect_pagination_and_tile_extras()
    {
        var tile = new MonitoringMapTile
        {
            Id = Guid.NewGuid(),
            Kind = MonitoringMapTileKind.Value,
            Title = "CPU",
            IconKey = "cpu",
            ShowCard = false,
            VisualType = MonitoringMapTileVisualType.Gauge
        };
        var map = new MonitoringMap
        {
            Id = Guid.NewGuid(),
            Name = "Ops Wall",
            AspectRatioWidth = 21,
            AspectRatioHeight = 9,
            WallboardFit = MonitoringMapWallboardFit.Stretch,
            AutoRotateSeconds = 30,
            PaginationMode = MonitoringMapPaginationMode.OverlayAlways
        };
        map.Slides.Add(new MonitoringMapSlide { Name = "S1", Tiles = { tile } });

        var clone = map.Clone();

        Assert.Equal(MonitoringMapWallboardFit.Stretch, clone.WallboardFit);
        Assert.Equal(21, clone.AspectRatioWidth);
        Assert.Equal(9, clone.AspectRatioHeight);
        Assert.Equal(MonitoringMapPaginationMode.OverlayAlways, clone.PaginationMode);

        var clonedTile = Assert.Single(Assert.Single(clone.Slides).Tiles);
        Assert.Equal("cpu", clonedTile.IconKey);
        Assert.False(clonedTile.ShowCard);
        Assert.NotSame(tile, clonedTile);
    }

    [Fact]
    public void MapClone_is_detached()
    {
        var map = new MonitoringMap { WallboardFit = MonitoringMapWallboardFit.Stretch };
        map.Slides.Add(new MonitoringMapSlide { Tiles = { new MonitoringMapTile { IconKey = "a" } } });

        var clone = map.Clone();
        clone.WallboardFit = MonitoringMapWallboardFit.Fit;
        clone.Slides.Clear();

        Assert.Equal(MonitoringMapWallboardFit.Stretch, map.WallboardFit);
        Assert.Single(map.Slides);
    }

    [Fact]
    public void MapClone_copies_every_scalar_property()
    {
        AssertEveryScalarSurvivesClone(new MonitoringMap(), map => map.Clone());
    }

    [Fact]
    public void TileClone_copies_every_scalar_property()
    {
        AssertEveryScalarSurvivesClone(new MonitoringMapTile(), tile => tile.Clone());
    }

    private static void AssertEveryScalarSurvivesClone<T>(T instance, Func<T, T> clone)
    {
        var scalars = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property is { CanRead: true, CanWrite: true } && IsScalar(property.PropertyType))
            .ToArray();
        Assert.NotEmpty(scalars);

        foreach (var property in scalars)
        {
            property.SetValue(instance, NonDefault(property.PropertyType, property.GetValue(instance)));
        }

        var cloned = clone(instance);

        foreach (var property in scalars)
        {
            Assert.Equal(property.GetValue(instance), property.GetValue(cloned));
        }
    }

    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsEnum
            || underlying == typeof(bool)
            || underlying == typeof(int)
            || underlying == typeof(long)
            || underlying == typeof(double)
            || underlying == typeof(string)
            || underlying == typeof(Guid)
            || underlying == typeof(DateTimeOffset);
    }

    // Returns a value guaranteed to differ from <paramref name="current"/> for the discrete types, so a
    // dropped field (which would reset to its default = current) is actually detected.
    private static object NonDefault(Type type, object? current)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsEnum)
        {
            var values = Enum.GetValues(underlying).Cast<object>().ToArray();
            return values.FirstOrDefault(value => !Equals(value, current)) ?? values[0];
        }

        if (underlying == typeof(bool))
        {
            return !(current as bool? ?? false);
        }

        if (underlying == typeof(int))
        {
            return (current as int?) == 97 ? 98 : 97;
        }

        if (underlying == typeof(long))
        {
            return (current as long?) == 97L ? 98L : 97L;
        }

        if (underlying == typeof(double))
        {
            return (current as double?) == 97d ? 98d : 97d;
        }

        if (underlying == typeof(string))
        {
            return "clone-parity";
        }

        if (underlying == typeof(Guid))
        {
            return Guid.NewGuid();
        }

        if (underlying == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
        }

        throw new NotSupportedException(underlying.FullName);
    }
}
