using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Loading;

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

    /// <summary>Returns every street intersection within <see cref="RadiusMeters"/> of the center.</summary>
    public async Task<IReadOnlyList<(double X, double Y)>> LoadIntersections(
        CancellationToken cancellationToken = default
    )
    {
        // Ways with their ordered node refs, then the coordinates of every referenced node.
        // InvariantCulture so coordinates always use '.' as the decimal separator.
        var query = string.Create(CultureInfo.InvariantCulture, $"""
            [out:json][timeout:25];
            way(around:{RadiusMeters},{Latitude},{Longitude})["highway"~"{StreetFilter}"];
            out body;
            >;
            out skel qt;
            """);

        var json = await PostQuery(query, cancellationToken);
        return ParseIntersections(json);
    }

    private List<(double X, double Y)> ParseIntersections(string json)
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

        // Degree >= 3 means a real junction. Degree 2 is either a mid-street node or a spot
        // where OSM splits one street into two ways, and degree 1 is a dead end.
        var result = new List<(double X, double Y)>();
        foreach (var (id, adjacent) in neighbors)
        {
            if (adjacent.Count < 3 || !coords.TryGetValue(id, out var c)) continue;

            // Ways that only partly overlap the circle bring in nodes outside it.
            if (DistanceMeters(Latitude, Longitude, c.Lat, c.Lon) <= RadiusMeters)
                result.Add(new (c.Lat, c.Lon));
        }
        return result;

        HashSet<long> Neighbors(long node) =>
            neighbors.TryGetValue(node, out var set) ? set : neighbors[node] = new HashSet<long>();
    }

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
