namespace Matmon.Core.Domain;

/// <summary>
/// Places a latitude/longitude on the shipped world map (<c>wwwroot/brand/world-map.svg</c>).
/// <para>
/// That map is drawn in an EQUIRECTANGULAR projection with 1 SVG unit = 1 degree, which is the whole reason
/// it was hand-authored that way: the projection is then a straight linear rescale with no trigonometry, no
/// map library and no tile server. If the map is ever redrawn, this must move with it - the invariant is
/// documented in the SVG itself.
/// </para>
/// Pure and framework-free so it is unit-testable.
/// </summary>
public static class MonitoringMapGeoProjection
{
    /// <summary>Longitude/latitude to a position as a FRACTION of the map (0..1 from the left / top), which
    /// is what a CSS-positioned pin needs and what keeps the pin correct at any rendered size.</summary>
    public static (double X, double Y) ToFraction(double latitude, double longitude)
    {
        // Longitude wraps; latitude does not. Clamping a bad latitude keeps the pin on the map edge rather
        // than off the tile, which is the more useful failure for a wallboard.
        var wrappedLongitude = WrapLongitude(longitude);
        var clampedLatitude = Math.Clamp(latitude, -90, 90);

        return ((wrappedLongitude + 180) / 360, (90 - clampedLatitude) / 180);
    }

    /// <summary>Normalises any longitude into -180..180, so 190 and -170 land in the same place instead of
    /// one of them falling off the map.</summary>
    public static double WrapLongitude(double longitude)
    {
        if (double.IsNaN(longitude) || double.IsInfinity(longitude))
        {
            return 0;
        }

        // A longitude that is already in range is returned UNTOUCHED. Running it through the modulo costs a
        // float bit (13.405 came back as 13.404999999999999), and a coordinate that shifts a little on every
        // save is a coordinate that drifts.
        if (longitude is >= -180 and <= 180)
        {
            return longitude;
        }

        var wrapped = (longitude + 180) % 360;
        if (wrapped < 0)
        {
            wrapped += 360;
        }

        var result = wrapped - 180;

        // The antimeridian is BOTH edges of the map, so -180 and 180 are the same line and either answer is
        // defensible. A site entered as +180 (Fiji, Chatham Islands) jumping to the far LEFT of the map next
        // to the Bering Strait reads as a bug though, so a positive input keeps the right-hand edge.
        return result == -180 && longitude > 0 ? 180 : result;
    }

    public static bool IsValidLatitude(double? latitude) =>
        latitude is { } value && double.IsFinite(value) && value is >= -90 and <= 90;

    public static bool IsValidLongitude(double? longitude) =>
        longitude is { } value && double.IsFinite(value);
}
