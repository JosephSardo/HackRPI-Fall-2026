using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PathFinder.Models;

namespace PathFinder.Loading;

public class StreetLoader : IDisposable
{
    private const string Endpoint = "https://overpass-api.de/api/interpreter";
    private const int MaxAttempts = 5;
    private static readonly TimeSpan InitialTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(30);
    private const double EarthRadiusMeters = 6_371_000;

    // Drivable public streets. "service" (driveways, parking aisles) is excluded so
    // every driveway doesn't register as an intersection.
    private const string StreetFilter =
        "^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street)(_link)?$";

    private readonly HttpClient _client;

    public double Latitude { get; }
    public double Longitude { get; }
    public double RadiusMeters { get; }

    /// <param name="latitude">Center latitude in degrees.</param>
    /// <param name="longitude">Center longitude in degrees.</param>
    /// <param name="radiusMeters">Search radius in meters.</param>
    public StreetLoader(double latitude, double longitude, double radiusMeters)
    {
        if (latitude is < -90 or > 90) throw new ArgumentOutOfRangeException(nameof(latitude));
        if (longitude is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(longitude));
        if (radiusMeters <= 0) throw new ArgumentOutOfRangeException(nameof(radiusMeters));

        Latitude = latitude;
        Longitude = longitude;
        RadiusMeters = radiusMeters;

        _client = new HttpClient();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("PathFinder/1.0");
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Returns every street intersection within <see cref="RadiusMeters"/> of the center, as (latitude, longitude).</summary>
    public async Task<IReadOnlyList<(double X, double Y)>> LoadIntersections(
        CancellationToken cancellationToken = default
    )
    {
        var network = ParseNetwork(await PostQuery(BuildQuery(), cancellationToken));

        var result = new List<(double X, double Y)>();
        foreach (var id in network.Neighbors.Keys)
        {
            if (network.IsJunction(id) && network.Coords.TryGetValue(id, out var c) && IsInside(c))
                result.Add((c.Lat, c.Lon));
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
        var network = ParseNetwork(await PostQuery(BuildQuery(), cancellationToken));
        return BuildMap(network);
    }

    /// <summary>The raw OSM street network: node positions, and which nodes each street segment joins.</summary>
    private sealed record Network(
        Dictionary<long, (double Lat, double Lon)> Coords,
        Dictionary<long, HashSet<long>> Neighbors)
    {
        // Degree >= 3 means a real junction. Degree 2 is either a mid-street node or a spot
        // where OSM splits one street into two ways, and degree 1 is a dead end.
        public bool IsJunction(long id) => Neighbors[id].Count >= 3;
    }

    private string BuildQuery() =>
        // Ways with their ordered node refs, then the coordinates of every referenced node.
        // InvariantCulture so coordinates always use '.' as the decimal separator.
        string.Create(CultureInfo.InvariantCulture, $"""
            [out:json][timeout:25];
            way(around:{RadiusMeters},{Latitude},{Longitude})["highway"~"{StreetFilter}"];
            out body;
            >;
            out skel qt;
            """);

    private static Network ParseNetwork(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var coords = new Dictionary<long, (double Lat, double Lon)>();
        // Distinct neighbors of each node along the street network.
        var neighbors = new Dictionary<long, HashSet<long>>();

        foreach (var element in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            var id = element.GetProperty("id").GetInt64();

            switch (element.GetProperty("type").GetString())
            {
                case "node":
                    coords[id] = (element.GetProperty("lat").GetDouble(), element.GetProperty("lon").GetDouble());
                    break;

                case "way":
                    long? prev = null;
                    foreach (var nodeRef in element.GetProperty("nodes").EnumerateArray())
                    {
                        var current = nodeRef.GetInt64();
                        if (prev is { } p && p != current)
                        {
                            Neighbors(p).Add(current);
                            Neighbors(current).Add(p);
                        }
                        prev = current;
                    }
                    break;
            }
        }
        return new Network(coords, neighbors);

        HashSet<long> Neighbors(long node) =>
            neighbors.TryGetValue(node, out var set) ? set : neighbors[node] = new HashSet<long>();
    }

    private CityMap BuildMap(Network network)
    {
        var (coords, neighbors) = (network.Coords, network.Neighbors);

        // Intersections inside the circle. Ways that only partly overlap it bring in nodes outside it.
        var junctions = neighbors.Keys
            .Where(id => network.IsJunction(id) && coords.TryGetValue(id, out var c) && IsInside(c))
            .ToHashSet();

        // Walk from each intersection along each of its streets to the next intersection. The nodes
        // in between have exactly 2 neighbours (curve points, or where OSM split one road into two
        // ways), so the walk just keeps going the way it wasn't coming from. Each street is found
        // from both ends; keying by the ordered pair keeps one, and the shortest if two roads join
        // the same pair of intersections.
        var streets = new Dictionary<(long A, long B), double>();
        foreach (var start in junctions)
        foreach (var first in neighbors[start])
        {
            if (WalkToIntersection(start, first) is not ({ } end, var length)) continue;
            if (!junctions.Contains(end) || end == start) continue; // left the circle, or looped back

            var key = start < end ? (start, end) : (end, start);
            if (!streets.TryGetValue(key, out var best) || length < best) streets[key] = length;
        }

        // Cutting at the radius can strand small groups of intersections. Keep the biggest
        // connected piece so every start/end pair has a route.
        var keep = LargestConnectedPiece(streets.Keys);

        var map = new CityMap(string.Create(CultureInfo.InvariantCulture,
            $"{Latitude:0.0000}, {Longitude:0.0000} ({RadiusMeters:0} m)"));

        // OSM node IDs are huge and sparse; the game uses small sequential IDs. Numbering in OSM-ID
        // order makes the same area always produce the same IDs.
        var intersections = new Dictionary<long, Intersection>();
        var nextIntersectionId = 1;
        foreach (var osmId in keep.Order())
        {
            var (x, y) = ToMetres(osmId);
            var intersection = new Intersection(nextIntersectionId++, x, y);
            intersections.Add(osmId, intersection);
            map.AddIntersection(intersection);
        }

        // Streets in a stable order too, so street IDs are repeatable.
        var nextStreetId = 1;
        foreach (var ((a, b), length) in streets.Where(s => keep.Contains(s.Key.A)).OrderBy(s => s.Key))
            map.AddStreet(new Street(nextStreetId++, length, intersections[a], intersections[b]));

        return map;

        // Follows a street from an intersection to the next one. Null at a dead end, or where the
        // data runs out (a node without coordinates).
        (long End, double Length)? WalkToIntersection(long start, long first)
        {
            long prev = start, current = first;
            if (!coords.ContainsKey(current)) return null;
            double length = Step(start, current);

            while (neighbors[current].Count == 2)
            {
                var next = neighbors[current].First(n => n != prev);
                if (!coords.ContainsKey(next)) return null;
                length += Step(current, next);
                (prev, current) = (current, next);
            }
            // Reaching a node that isn't degree 2 ends the walk; degree 1 means a dead end. Dead-end
            // streets are dropped: no shortest route between two intersections ever uses one.
            return neighbors[current].Count >= 3 ? (current, length) : null;
        }

        double Step(long a, long b) => DistanceMeters(coords[a].Lat, coords[a].Lon, coords[b].Lat, coords[b].Lon);

        // Flat projection around the center: accurate to well under 1% over a few kilometres.
        (double X, double Y) ToMetres(long id)
        {
            const double rad = Math.PI / 180;
            var (lat, lon) = coords[id];
            return ((lon - Longitude) * rad * EarthRadiusMeters * Math.Cos(Latitude * rad),
                    (lat - Latitude) * rad * EarthRadiusMeters);
        }
    }

    private static HashSet<long> LargestConnectedPiece(IEnumerable<(long A, long B)> streets)
    {
        var adjacent = streets.SelectMany(s => new[] { (s.A, s.B), (s.B, s.A) }).ToLookup(p => p.Item1, p => p.Item2);
        var seen = new HashSet<long>();
        var largest = new HashSet<long>();

        foreach (var root in adjacent.Select(g => g.Key))
        {
            if (seen.Contains(root)) continue;

            var piece = new HashSet<long> { root };
            var queue = new Queue<long>([root]);
            while (queue.TryDequeue(out var node))
                foreach (var next in adjacent[node])
                    if (piece.Add(next)) queue.Enqueue(next);

            seen.UnionWith(piece);
            if (piece.Count > largest.Count) largest = piece;
        }
        return largest;
    }

    private bool IsInside((double Lat, double Lon) c) => DistanceMeters(Latitude, Longitude, c.Lat, c.Lon) <= RadiusMeters;

    // Haversine great-circle distance.
    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double deg) => deg * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Sqrt(a));
    }

    private async Task<string> PostQuery(string query, CancellationToken cancellationToken)
    {
        TimeSpan timeout = InitialTimeout;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Fresh content per attempt; a sent HttpContent shouldn't be reused.
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { { "data", query } });
                using var response = await _client.PostAsync(Endpoint, content, cancellationToken);

                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync(cancellationToken);

                if (!IsTransient(response.StatusCode) || attempt >= MaxAttempts)
                    response.EnsureSuccessStatusCode(); // throws
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < MaxAttempts)
            {
                // Network-level failure (DNS, connection reset, etc.) — retry.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttempts)
            {
                // HttpClient timeout — retry.
            }

            await Task.Delay(timeout, cancellationToken);
            timeout = timeout > MaxTimeout ? MaxTimeout : TimeSpan.FromSeconds(2 * timeout.Seconds);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests      // 429: Overpass rate limit
        || status == HttpStatusCode.RequestTimeout    // 408
        || (int)status >= 500;                        // 502/503/504: Overpass overloaded
}
