using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using PathFinder.Models;

namespace PathFinder.Network;

/// <summary>
/// Saves downloaded maps to disk so the same location loads instantly next time and doesn't depend
/// on OpenStreetMap being up (handy for demos: load a location once beforehand). Stored as
/// <see cref="MapData"/> JSON in %LOCALAPPDATA%\PathFinder\maps, one file per location + radius.
/// Delete the folder to force fresh downloads.
/// </summary>
public static class MapCache
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PathFinder", "maps");

    /// <summary>The saved map for this spot, or null if there isn't a usable one.</summary>
    public static CityMap? TryLoad(double latitude, double longitude, double radiusMeters)
    {
        var file = FileFor(latitude, longitude, radiusMeters);
        if (!File.Exists(file)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<MapData>(File.ReadAllText(file), Json);
            return data is null ? null : MapDataConverter.ToCityMap(data);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[map cache] Ignoring unreadable {file}: {ex.Message}");
            return null;
        }
    }

    public static void Save(double latitude, double longitude, double radiusMeters, CityMap map)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FileFor(latitude, longitude, radiusMeters),
                              JsonSerializer.Serialize(MapDataConverter.ToData(map), Json));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[map cache] Couldn't save: {ex.Message}"); // not fatal: just no cache next time
        }
    }

    /// <summary>Locations within ~10 m of each other (4 decimal places) share a file.</summary>
    static string FileFor(double latitude, double longitude, double radiusMeters) =>
        Path.Combine(Folder, string.Create(CultureInfo.InvariantCulture,
            $"{latitude:F4}_{longitude:F4}_{radiusMeters:F0}m.json"));
}
