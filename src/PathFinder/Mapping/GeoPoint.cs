using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Mapsui;
using Mapsui.Projections;

namespace PathFinder.Mapping;

/// <summary>A position on Earth in degrees. The rest of the app talks in these, never in map units.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public override string ToString() =>
        $"{Math.Abs(Latitude):0.0000}° {(Latitude >= 0 ? "N" : "S")}, {Math.Abs(Longitude):0.0000}° {(Longitude >= 0 ? "E" : "W")}";

    /// <summary>"latitude, longitude" in decimal degrees, e.g. "42.728400, -73.691800". <see cref="TryParse"/> reads it back.</summary>
    public string ToCoordinateString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.000000}, {Longitude:0.000000}");

    /// <summary>
    /// Parses "latitude, longitude" in decimal degrees, e.g. "42.7284, -73.6918" (the format
    /// Google Maps copies). A space works as the separator too. Always uses '.' for decimals.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out GeoPoint? point)
    {
        point = null;
        var parts = text?.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 }) return false;

        const NumberStyles style = NumberStyles.Float;
        if (!double.TryParse(parts[0], style, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(parts[1], style, CultureInfo.InvariantCulture, out var lon))
            return false;

        if (lat is < -90 or > 90 || lon is < -180 or > 180) return false;

        point = new GeoPoint(lat, lon);
        return true;
    }
}

/// <summary>
/// Conversions between <see cref="GeoPoint"/> and Mapsui's world units (Web Mercator meters),
/// and between zoom levels and Mapsui resolutions (map meters per screen pixel).
/// </summary>
public static class WebMercator
{
    /// <summary>Resolution at zoom level 0, where the whole world fits in one 256px tile.</summary>
    const double ResolutionAtZoom0 = 2 * Math.PI * 6_378_137 / 256;

    /// <summary>Web Mercator can't show the poles; latitudes beyond this are off the map.</summary>
    public const double MaxLatitude = 85.05112878;

    public static MPoint ToWorld(GeoPoint p)
    {
        var (x, y) = SphericalMercator.FromLonLat(p.Longitude, Math.Clamp(p.Latitude, -MaxLatitude, MaxLatitude));
        return new MPoint(x, y);
    }

    public static GeoPoint ToGeo(double worldX, double worldY)
    {
        var (lon, lat) = SphericalMercator.ToLonLat(worldX, worldY);
        return new GeoPoint(lat, lon);
    }

    public static GeoPoint ToGeo(MPoint world) => ToGeo(world.X, world.Y);

    /// <summary>Zoom 0 = whole world, ~17 = street level, 19 = the satellite imagery's limit. Fractions are fine.</summary>
    public static double ResolutionFromZoom(double zoom) => ResolutionAtZoom0 / Math.Pow(2, zoom);

    public static double ZoomFromResolution(double resolution) => Math.Log2(ResolutionAtZoom0 / resolution);
}
