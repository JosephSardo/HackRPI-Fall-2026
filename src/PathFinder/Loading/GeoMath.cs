using System;

namespace PathFinder.Loading;

/// <summary>A position in degrees, as OpenStreetMap gives it.</summary>
internal readonly record struct LatLon(double Lat, double Lon);

internal static class GeoMath
{
    public const double EarthRadiusMeters = 6_371_000;

    public static double ToRadians(double degrees) => degrees * Math.PI / 180;

    /// <summary>Great-circle distance in metres (haversine formula).</summary>
    public static double DistanceMeters(LatLon a, LatLon b)
    {
        var dLat = ToRadians(b.Lat - a.Lat);
        var dLon = ToRadians(b.Lon - a.Lon);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(a.Lat)) * Math.Cos(ToRadians(b.Lat)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Sqrt(h));
    }
}
