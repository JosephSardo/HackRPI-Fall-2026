using System;
using System.Globalization;

namespace PathFinder.Loading;

/// <summary>The circle a map is loaded from: a center in degrees and a radius in metres.</summary>
internal readonly struct SearchArea
{
    public SearchArea(double latitude, double longitude, double radiusMeters)
    {
        if (latitude is < -90 or > 90) throw new ArgumentOutOfRangeException(nameof(latitude));
        if (longitude is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(longitude));
        if (radiusMeters <= 0) throw new ArgumentOutOfRangeException(nameof(radiusMeters));

        Center = new LatLon(latitude, longitude);
        RadiusMeters = radiusMeters;
    }

    public LatLon Center { get; }
    public double RadiusMeters { get; }

    public bool Contains(LatLon point) => GeoMath.DistanceMeters(Center, point) <= RadiusMeters;

    /// <summary>
    /// Metres east (X) and north (Y) of the center, using a flat projection around it: accurate
    /// to well under 1% over a few kilometres, which is all a game map covers.
    /// </summary>
    public (double X, double Y) ToMetres(LatLon point)
    {
        const double rad = Math.PI / 180;
        return ((point.Lon - Center.Lon) * rad * GeoMath.EarthRadiusMeters * Math.Cos(Center.Lat * rad),
                (point.Lat - Center.Lat) * rad * GeoMath.EarthRadiusMeters);
    }

    /// <summary>A readable name for a map of this area, e.g. "42.7284, -73.6918 (500 m)".</summary>
    public string Name => string.Create(CultureInfo.InvariantCulture,
        $"{Center.Lat:0.0000}, {Center.Lon:0.0000} ({RadiusMeters:0} m)");
}
