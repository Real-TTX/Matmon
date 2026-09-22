using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// The geo pin projection. Checked against real coordinates rather than against the formula it was written
/// from, because "it matches the maths I typed" proves nothing - the value of this test is that a redrawn
/// world map that is no longer equirectangular breaks it loudly.
/// </summary>
public class MonitoringMapGeoProjectionTests
{
    [Theory]
    // City, latitude, longitude, expected fraction across / down the map.
    [InlineData(0, 0, 0.5, 0.5)]                        // Null Island - the exact centre
    [InlineData(90, -180, 0.0, 0.0)]                    // top-left corner
    [InlineData(-90, 180, 1.0, 1.0)]                    // bottom-right corner
    [InlineData(52.52, 13.405, 0.5372, 0.2082)]         // Berlin
    [InlineData(-33.87, 151.21, 0.9200, 0.6882)]        // Sydney
    [InlineData(37.77, -122.42, 0.1600, 0.2902)]        // San Francisco
    public void Projects_coordinates_onto_the_equirectangular_map(double latitude, double longitude, double x, double y)
    {
        var (actualX, actualY) = MonitoringMapGeoProjection.ToFraction(latitude, longitude);

        Assert.Equal(x, actualX, 3);
        Assert.Equal(y, actualY, 3);
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(540, 180)]
    [InlineData(13.405, 13.405)]
    public void Longitude_wraps_instead_of_falling_off_the_map(double input, double expected)
    {
        Assert.Equal(expected, MonitoringMapGeoProjection.WrapLongitude(input), 6);
    }

    [Fact]
    public void A_wrapped_longitude_lands_in_the_same_place_as_its_equivalent()
    {
        var wrapped = MonitoringMapGeoProjection.ToFraction(0, 190);
        var plain = MonitoringMapGeoProjection.ToFraction(0, -170);

        Assert.Equal(plain.X, wrapped.X, 6);
    }

    [Fact]
    public void An_impossible_latitude_is_clamped_to_the_map_edge()
    {
        // Better a pin on the edge than a pin outside the tile, which on a wallboard just vanishes.
        var (_, y) = MonitoringMapGeoProjection.ToFraction(120, 0);

        Assert.Equal(0, y, 6);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(91d, false)]
    [InlineData(-91d, false)]
    [InlineData(double.NaN, false)]
    [InlineData(52.52, true)]
    public void Latitude_validation_rejects_what_cannot_be_placed(double? latitude, bool expected)
    {
        Assert.Equal(expected, MonitoringMapGeoProjection.IsValidLatitude(latitude));
    }

    [Fact]
    public void Non_finite_longitude_does_not_produce_a_non_finite_position()
    {
        Assert.Equal(0, MonitoringMapGeoProjection.WrapLongitude(double.NaN));
        Assert.Equal(0, MonitoringMapGeoProjection.WrapLongitude(double.PositiveInfinity));
        Assert.False(MonitoringMapGeoProjection.IsValidLongitude(double.NaN));
    }
}
