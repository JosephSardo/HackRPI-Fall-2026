using System;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PathFinder.Models;

namespace PathFinder.Loading;

/// <summary>
/// Loads real streets from OpenStreetMap for a circle on the map. The work is split across this
/// folder:
/// <list type="bullet">
/// <item><see cref="StreetQuery"/> builds the Overpass query and <see cref="OverpassClient"/> runs it.</item>
/// <item><see cref="OsmApiClient"/> is the fallback when Overpass fails: the main OpenStreetMap API.</item>
/// <item><see cref="StreetNetwork"/> parses the response into nodes and the segments joining them.</item>
/// <item><see cref="StreetTracer"/> follows the segments to find streets between intersections.</item>
/// <item><see cref="CityMapBuilder"/> turns those into a <see cref="CityMap"/>.</item>
/// <item><see cref="SearchArea"/> and <see cref="GeoMath"/> handle the circle and the distances.</item>
/// </list>
/// </summary>
public class StreetLoader : IDisposable
{
    private readonly SearchArea _area;
    private readonly OverpassClient _overpass = new();
    private readonly OsmApiClient _osmApi = new();

    /// <param name="latitude">Center latitude in degrees.</param>
    /// <param name="longitude">Center longitude in degrees.</param>
    /// <param name="radiusMeters">Search radius in meters.</param>
    public StreetLoader(double latitude, double longitude, double radiusMeters)
    {
        _area = new SearchArea(latitude, longitude, radiusMeters);
    }

    public double Latitude => _area.Center.Lat;
    public double Longitude => _area.Center.Lon;
    public double RadiusMeters => _area.RadiusMeters;

    public void Dispose()
    {
        _overpass.Dispose();
        _osmApi.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Returns every street intersection within <see cref="RadiusMeters"/> of the center, as (latitude, longitude).</summary>
    public async Task<IReadOnlyList<(double X, double Y)>> LoadIntersections(
        CancellationToken cancellationToken = default
    )
    {
        var network = await LoadNetwork(cancellationToken);

        var result = new List<(double X, double Y)>();
        foreach (var id in network.Neighbors.Keys)
        {
            if (network.IsJunction(id) && network.Positions.TryGetValue(id, out var p) && _area.Contains(p))
                result.Add((p.Lat, p.Lon));
        }
        return result;
    }

    /// <summary>
    /// Returns the street network within <see cref="RadiusMeters"/> of the center as a
    /// <see cref="CityMap"/>: an <see cref="Intersection"/> for each junction and a
    /// <see cref="Street"/> for each road between two junctions, with its real (curve-following) length.
    /// <list type="bullet">
    /// <item>Intersection IDs run 1..N; the same area always gets the same IDs.</item>
    /// <item>Intersection X/Y are metres from the center: X is east, Y is north.</item>
    /// <item>At most one street joins any two intersections, and every intersection can reach every other one.</item>
    /// <item>Dead-end streets are left out: no shortest route between two intersections uses one.</item>
    /// </list>
    /// The map is empty if there are no streets in the area.
    /// </summary>
    public async Task<CityMap> LoadMap(CancellationToken cancellationToken = default)
    {
        var network = await LoadNetwork(cancellationToken);
        return CityMapBuilder.Build(network, _area);
    }

    /// <summary>
    /// Tries Overpass first and, if it fails for any reason other than the caller cancelling, the
    /// OpenStreetMap API. If both fail, the exception's message names both failures.
    /// </summary>
    private async Task<StreetNetwork> LoadNetwork(CancellationToken cancellationToken)
    {
        // The OSM API answers a small area in about a second, while public Overpass servers are often
        // overloaded (they accept the connection and then never reply). So use the OSM API first when
        // the area is small enough for it, and Overpass for bigger areas or when the OSM API fails.
        var sources = new List<(string Name, Func<Task<string>> Fetch)>();
        if (_area.RadiusMeters <= OsmApiClient.MaxRadiusMeters)
            sources.Add(("OSM API", () => _osmApi.QueryStreetsAsync(_area, cancellationToken)));
        sources.Add(("Overpass", () => _overpass.QueryAsync(StreetQuery.For(_area), cancellationToken)));

        var errors = new List<(string Name, Exception Error)>();
        foreach (var (name, fetch) in sources)
        {
            try
            {
                return StreetNetwork.Parse(await fetch());
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add((name, ex));
            }
        }

        throw new HttpRequestException(
            string.Join("; ", errors.Select(e => $"{e.Name}: {Describe(e.Error)}")),
            new AggregateException(errors.Select(e => e.Error)));
    }

    /// <summary>A few words on why a download failed, short enough for the host's warning.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => "timed out",
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        _ => ex.Message.Length <= 60 ? ex.Message : ex.Message[..60] + "...",
    };
}
