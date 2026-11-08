using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PathFinder.Loading;
using PathFinder.Models;
using PathFinder.Services;

namespace PathFinder.Network;

/// <summary>A demo spot: a lobby preset, with a real map of it saved in the app (Assets/Maps/<c>File</c>).</summary>
public sealed record DemoMap(string Name, double Latitude, double Longitude, double RadiusMeters, string File);

/// <summary>
/// Real OpenStreetMap maps bundled with the app (as <see cref="MapData"/> JSON embedded from Assets/Maps),
/// so demos work without the internet. The lobby offers them as one-click presets; the host plays a preset
/// from its bundled copy straight away, and uses the nearest one if downloading a map fails.
/// To refresh a file, build the map with <see cref="MapBuilder.BuildAsync"/> and save <see cref="MapDataConverter.ToData"/> as JSON.
/// </summary>
public static class DemoMaps
{
    /// <summary>How close a bundled map's centre must be to the requested location to stand in for it.</summary>
    public const double NearbyMeters = 1000;

    public static IReadOnlyList<DemoMap> All { get; } =
    [
        new("RPI campus", 42.7298, -73.6789, 700, "rpi-campus.json"),
        new("Downtown Troy", 42.7312, -73.6874, 600, "downtown-troy.json"),
        new("Downtown Albany", 42.6505, -73.7545, 500, "downtown-albany.json"),
    ];

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The preset for exactly this location (to ~10 m, like <see cref="MapCache"/>) and radius, if there is one.</summary>
    public static DemoMap? Exact(double latitude, double longitude, double radiusMeters) =>
        All.FirstOrDefault(d => Key(d.Latitude, d.Longitude) == Key(latitude, longitude) &&
                                Math.Abs(d.RadiusMeters - radiusMeters) < 1);

    /// <summary>The bundled map whose centre is closest to the location, if it's within <see cref="NearbyMeters"/>.</summary>
    public static DemoMap? Nearest(double latitude, double longitude) =>
        All.Select(d => (Demo: d, Meters: GeoMath.DistanceMeters(new LatLon(latitude, longitude), new LatLon(d.Latitude, d.Longitude))))
           .Where(x => x.Meters <= NearbyMeters)
           .OrderBy(x => x.Meters)
           .Select(x => x.Demo)
           .FirstOrDefault();

    /// <summary>The bundled map, or null if its resource is missing or unreadable.</summary>
    public static CityMap? Load(DemoMap demo)
    {
        try
        {
            using var stream = typeof(DemoMaps).Assembly.GetManifestResourceStream($"PathFinder.Maps.{demo.File}");
            if (stream is null) return null;
            var data = JsonSerializer.Deserialize<MapData>(stream, Json);
            if (data is null) return null;
            var map = MapDataConverter.ToCityMap(data);
            map.Attribution ??= MapBuilder.OsmAttribution; // OpenStreetMap data: the credit must stay on screen
            return map;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[demo maps] Couldn't read {demo.File}: {ex.Message}");
            return null;
        }
    }

    static string Key(double latitude, double longitude) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude:F4}_{longitude:F4}");
}
