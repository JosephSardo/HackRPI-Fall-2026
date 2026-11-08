using System.Threading;
using System.Threading.Tasks;
using PathFinder.Loading;
using PathFinder.Models;

namespace PathFinder.Services;

/// <summary>
/// The one meeting point between map loading and the game: the host calls this when a match starts
/// (around the lobby's Location), owns the result as the canonical map, and sends a snapshot to every player.
/// The host falls back to the built-in test grid if this throws, takes too long, or returns a map
/// with no playable routes (e.g. an area with no streets). See Network/HostMapLoader.
/// </summary>
public static class MapBuilder
{
    /// <summary>The on-screen credit the OpenStreetMap licence (ODbL) requires wherever its data is shown.</summary>
    public const string OsmAttribution = "© OpenStreetMap contributors";

    /// <summary>Real streets from OpenStreetMap; see <see cref="StreetLoader.LoadMap"/> for what the map guarantees.</summary>
    public static async Task<CityMap> BuildAsync(double latitude, double longitude, double radiusMeters,
                                                 CancellationToken cancellationToken = default)
    {
        using var loader = new StreetLoader(latitude, longitude, radiusMeters);
        var map = await loader.LoadMap(cancellationToken);
        map.Attribution = OsmAttribution;
        return map;
    }
}
