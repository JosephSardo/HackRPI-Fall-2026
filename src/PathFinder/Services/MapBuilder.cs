using System.Threading;
using System.Threading.Tasks;
using PathFinder.Models;
using PathFinder.Network;

namespace PathFinder.Services;

/// <summary>
/// The one meeting point between map loading and the game: the host calls this once when it
/// starts hosting, owns the result as the canonical map, and sends a snapshot to every player.
///
/// TODO (core/map owners): build the real map here from StreetLoader + MapService: intersections
/// and streets in metres, largest connected piece only, at most one street between any two
/// intersections. Until then this returns the built-in test grid.
/// </summary>
public static class MapBuilder
{
    public static Task<CityMap> BuildAsync(double latitude, double longitude, double radiusMeters,
                                           CancellationToken cancellationToken = default)
        => Task.FromResult(TestMaps.Grid());
}
