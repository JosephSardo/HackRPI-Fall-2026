using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PathFinder.Loading;

/// <summary>
/// Fallback street source for when every Overpass server fails: the main OpenStreetMap API's
/// <c>map.json</c> call, which returns all OSM data in a bounding box as OSM JSON (the same
/// element format Overpass uses). It returns everything (buildings, footpaths, rivers, relations),
/// so this keeps only the street ways <see cref="StreetQuery"/> would have selected, plus the nodes
/// they use, and hands back JSON that <see cref="StreetNetwork.Parse"/> reads.
/// <para>
/// The OSM API is meant for editing, and its usage policy discourages heavy read use. This is only
/// tried when Overpass is unreachable, for one small area per match, with an identifying
/// User-Agent: acceptable for a student project, but don't make it the main source.
/// </para>
/// </summary>
internal sealed class OsmApiClient : IDisposable
{
    private const string Endpoint = "https://api.openstreetmap.org/api/0.6/map.json";

    // The API refuses boxes over 0.25 square degrees or 50,000 nodes. Well before that, a big
    // circle would mean a slow, heavy download from a server that isn't meant for it.
    public const double MaxRadiusMeters = 2_000;
    private const double MaxBoxSquareDegrees = 0.25;

    private static readonly Regex StreetHighway = new(StreetQuery.StreetFilter, RegexOptions.CultureInvariant);

    private readonly HttpClient _client;

    public OsmApiClient()
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("PathFinder/1.0 (HackRPI Fall 2026 student project)");
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// The streets touching <paramref name="area"/>'s bounding box, as OSM JSON holding only street
    /// ways and their nodes. <see cref="CityMapBuilder"/> clips the result to the circle.
    /// </summary>
    public async Task<string> QueryStreetsAsync(SearchArea area, CancellationToken cancellationToken)
    {
        if (area.RadiusMeters > MaxRadiusMeters)
            throw new InvalidDataException($"radius over {MaxRadiusMeters:0} m is too big for the OSM API");

        var (minLat, minLon, maxLat, maxLon) = BoundingBox(area);
        if ((maxLat - minLat) * (maxLon - minLon) >= MaxBoxSquareDegrees)
            throw new InvalidDataException("area is too big for the OSM API");

        var url = string.Create(CultureInfo.InvariantCulture,
            $"{Endpoint}?bbox={minLon:0.0000000},{minLat:0.0000000},{maxLon:0.0000000},{maxLat:0.0000000}");
        using var response = await _client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return KeepStreets(doc);
    }

    /// <summary>The box around the circle, in degrees: the radius converted around the center.</summary>
    internal static (double MinLat, double MinLon, double MaxLat, double MaxLon) BoundingBox(SearchArea area)
    {
        var dLat = area.RadiusMeters / GeoMath.EarthRadiusMeters * 180 / Math.PI;
        // A degree of longitude shrinks towards the poles; the floor keeps this finite near them.
        var dLon = dLat / Math.Max(Math.Cos(GeoMath.ToRadians(area.Center.Lat)), 0.01);
        return (Math.Max(area.Center.Lat - dLat, -90), Math.Max(area.Center.Lon - dLon, -180),
                Math.Min(area.Center.Lat + dLat, 90), Math.Min(area.Center.Lon + dLon, 180));
    }

    /// <summary>
    /// Rewrites an OSM JSON document to just the street ways (same <c>highway</c> filter as
    /// <see cref="StreetQuery"/>) and the nodes they reference, dropping tags, relations and
    /// everything else.
    /// </summary>
    internal static string KeepStreets(JsonDocument doc)
    {
        var ways = new List<JsonElement>();
        var usedNodes = new HashSet<long>();
        foreach (var element in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            if (element.GetProperty("type").GetString() != "way" || !IsStreet(element)) continue;
            ways.Add(element);
            foreach (var nodeRef in element.GetProperty("nodes").EnumerateArray())
                usedNodes.Add(nodeRef.GetInt64());
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("elements");
            foreach (var element in doc.RootElement.GetProperty("elements").EnumerateArray())
            {
                if (element.GetProperty("type").GetString() != "node") continue;
                var id = element.GetProperty("id").GetInt64();
                if (!usedNodes.Contains(id)) continue;
                writer.WriteStartObject();
                writer.WriteString("type", "node");
                writer.WriteNumber("id", id);
                writer.WriteNumber("lat", element.GetProperty("lat").GetDouble());
                writer.WriteNumber("lon", element.GetProperty("lon").GetDouble());
                writer.WriteEndObject();
            }
            foreach (var way in ways)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "way");
                writer.WriteNumber("id", way.GetProperty("id").GetInt64());
                writer.WritePropertyName("nodes");
                way.GetProperty("nodes").WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static bool IsStreet(JsonElement way) =>
        way.TryGetProperty("tags", out var tags)
        && tags.TryGetProperty("highway", out var highway)
        && highway.ValueKind == JsonValueKind.String
        && StreetHighway.IsMatch(highway.GetString()!);
}
