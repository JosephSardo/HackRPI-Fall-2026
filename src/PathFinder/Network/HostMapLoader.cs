using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PathFinder.Models;
using PathFinder.Services;

namespace PathFinder.Network;

/// <summary>
/// Gets the host its canonical map before it starts hosting. Tries the real map builder; if that
/// fails, takes too long, or produces a map with no playable start/end pair, falls back to the
/// built-in test grid so the host can always run a game.
/// </summary>
public static class HostMapLoader
{
    // Used when the host leaves the lobby's Location empty: RPI campus.
    public const double DefaultLatitude = 42.7284, DefaultLongitude = -73.6918;
    public const double RadiusMeters = 300;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The map around a location, plus a warning to show the host if it's the fallback.</summary>
    public static async Task<(CityMap Map, string? Warning)> LoadAsync(
        double latitude, double longitude, Func<CancellationToken, Task<CityMap>>? build = null)
    {
        build ??= ct => MapBuilder.BuildAsync(latitude, longitude, RadiusMeters, ct);
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            var map = await build(timeout.Token).WaitAsync(timeout.Token);
            new CityMapRoundRules(map).PickStartAndEnd(1); // throws if no round could be played on it
            return (map, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[host] Map loading failed, using the test grid: {ex}");
            var reason = ex switch
            {
                OperationCanceledException or TimeoutException => "took too long to load",
                InvalidOperationException => "has no playable routes",   // from the PickStartAndEnd check
                _ => "couldn't be loaded",
            };
            return (TestMaps.Grid(), $"The real map {reason}, so this game uses the built-in test map.");
        }
    }
}
