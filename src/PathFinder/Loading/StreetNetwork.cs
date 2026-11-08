using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PathFinder.Loading;

/// <summary>
/// The raw OpenStreetMap street network: where each node is, and which nodes each one is joined
/// to by a street segment. Nodes include every curve point, not just intersections.
/// </summary>
internal sealed class StreetNetwork
{
    private StreetNetwork(Dictionary<long, LatLon> positions, Dictionary<long, HashSet<long>> neighbors,
                          Dictionary<(long, long), string> segmentNames)
    {
        Positions = positions;
        Neighbors = neighbors;
        _segmentNames = segmentNames;
    }

    // Keyed by the segment's two nodes, lower OSM ID first. Unnamed segments aren't in it.
    private readonly Dictionary<(long, long), string> _segmentNames;

    /// <summary>Node positions by OSM node ID. A node can be missing at the edge of the data.</summary>
    public IReadOnlyDictionary<long, LatLon> Positions { get; }

    /// <summary>The distinct nodes each node is directly joined to, by OSM node ID.</summary>
    public IReadOnlyDictionary<long, HashSet<long>> Neighbors { get; }

    /// <summary>How many street segments meet at a node.</summary>
    public int Degree(long node) => Neighbors[node].Count;

    // Degree >= 3 means a real junction. Degree 2 is either a mid-street node or a spot
    // where OSM splits one street into two ways, and degree 1 is a dead end.
    public bool IsJunction(long node) => Degree(node) >= 3;

    /// <summary>
    /// The name of the way the segment between two neighbouring nodes belongs to: its OSM
    /// <c>name</c>, else its <c>ref</c> (route numbers like "NY 2"), else null.
    /// </summary>
    public string? SegmentName(long a, long b) => _segmentNames.GetValueOrDefault(a < b ? (a, b) : (b, a));

    /// <summary>Reads the JSON that <see cref="StreetQuery"/> asks Overpass for.</summary>
    public static StreetNetwork Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var positions = new Dictionary<long, LatLon>();
        var neighbors = new Dictionary<long, HashSet<long>>();
        var segmentNames = new Dictionary<(long, long), string>();

        foreach (var element in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            var id = element.GetProperty("id").GetInt64();

            switch (element.GetProperty("type").GetString())
            {
                case "node":
                    positions[id] = new LatLon(element.GetProperty("lat").GetDouble(), element.GetProperty("lon").GetDouble());
                    break;

                case "way":
                    // A way lists its nodes in order; each consecutive pair is one street segment.
                    var name = WayName(element);
                    long? prev = null;
                    foreach (var nodeRef in element.GetProperty("nodes").EnumerateArray())
                    {
                        var current = nodeRef.GetInt64();
                        if (prev is { } p && p != current)
                        {
                            NeighborsOf(p).Add(current);
                            NeighborsOf(current).Add(p);
                            // Where two ways share a segment, the first named one wins.
                            if (name is not null) segmentNames.TryAdd(p < current ? (p, current) : (current, p), name);
                        }
                        prev = current;
                    }
                    break;
            }
        }
        return new StreetNetwork(positions, neighbors, segmentNames);

        HashSet<long> NeighborsOf(long node) =>
            neighbors.TryGetValue(node, out var set) ? set : neighbors[node] = new HashSet<long>();
    }

    private static string? WayName(JsonElement way)
    {
        if (!way.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in (ReadOnlySpan<string>)["name", "ref"])
            if (tags.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString()!.Trim() is { Length: > 0 } text)
                return text;
        return null;
    }
}
