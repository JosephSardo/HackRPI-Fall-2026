using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PathFinder.Models;
using PathFinder.Services;

namespace PathFinder.Network;

/// <summary>The map for a match, where it came from, and a warning to show the host if it's the fallback.</summary>
public sealed record HostMap(CityMap Map, string? Warning, bool FromCache);

/// <summary>
/// Gets the host its canonical map when a match starts. Uses a saved copy of the location if there is
/// one (<see cref="MapCache"/>); otherwise downloads it and saves it. If downloading fails, takes too
/// long, or produces a map with no playable start/end pair, falls back to the built-in test grid so
/// the host can always run a game.
/// </summary>
public static class HostMapLoader
{
    // Used when the host leaves the lobby's Location empty: RPI campus.
    public const double DefaultLatitude = 42.7284, DefaultLongitude = -73.6918;
    public const double DefaultRadiusMeters = 300;
    // Long enough for the Overpass client's own retries (waits of 1, 2, 4, 8 s) to finish.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The map within <paramref name="radiusMeters"/> of a location. <paramref name="build"/> replaces the real downloader (for testing);
    /// only real downloads are cached.
    /// </summary>
    public static async Task<HostMap> LoadAsync(
        double latitude, double longitude, double radiusMeters = DefaultRadiusMeters,
        Func<CancellationToken, Task<CityMap>>? build = null)
    {
        var useCache = build is null;
        if (useCache && MapCache.TryLoad(latitude, longitude, radiusMeters) is { } saved && IsPlayable(saved))
            return new HostMap(saved, null, FromCache: true);

        build ??= ct => MapBuilder.BuildAsync(latitude, longitude, radiusMeters, ct);
        var elapsed = Stopwatch.StartNew();
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            var map = await build(timeout.Token).WaitAsync(timeout.Token);
            new CityMapRoundRules(map).PickStartAndEnd(1); // throws if no round could be played on it
            if (useCache) MapCache.Save(latitude, longitude, radiusMeters, map);
            return new HostMap(map, null, FromCache: false);
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
            // Include the actual error so problems (e.g. OpenStreetMap rate limits) can be diagnosed from the lobby.
            var detail = ex is OperationCanceledException ? "" : $" ({ex.GetType().Name}: {Shorten(ex.Message)})";
            return new HostMap(TestMaps.Grid(),
                $"The real map {reason} after {elapsed.Elapsed.TotalSeconds:0.0}s{detail}, so this game uses the built-in test map.",
                FromCache: false);
        }
    }

    static bool IsPlayable(CityMap map)
    {
        try { new CityMapRoundRules(map).PickStartAndEnd(1); return true; }
        catch (InvalidOperationException) { return false; }
    }

    static string Shorten(string message)
    {
        message = message.ReplaceLineEndings(" ").Trim();
        return message.Length <= 160 ? message : message[..160] + "...";
    }
}
