using System.Collections.Generic;
using System.Text.Json;

namespace PathFinder.Loading;

/// <summary>
/// The raw OpenStreetMap street network: where each node is, and which nodes each one is joined
/// to by a street segment. Nodes include every curve point, not just intersections.
/// </summary>
internal sealed class StreetNetwork
{
    private StreetNetwork(Dictionary<long, LatLon> positions, Dictionary<long, HashSet<long>> neighbors)
    {
        Positions = positions;
        Neighbors = neighbors;
    }

    /// <summary>Node positions by OSM node ID. A node can be missing at the edge of the data.</summary>
    public IReadOnlyDictionary<long, LatLon> Positions { get; }

    /// <summary>The distinct nodes each node is directly joined to, by OSM node ID.</summary>
    public IReadOnlyDictionary<long, HashSet<long>> Neighbors { get; }

    /// <summary>How many street segments meet at a node.</summary>
    public int Degree(long node) => Neighbors[node].Count;

    // Degree >= 3 means a real junction. Degree 2 is either a mid-street node or a spot
    // where OSM splits one street into two ways, and degree 1 is a dead end.
    public bool IsJunction(long node) => Degree(node) >= 3;

    /// <summary>Reads the JSON that <see cref="StreetQuery"/> asks Overpass for.</summary>
    public static StreetNetwork Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var positions = new Dictionary<long, LatLon>();
        var neighbors = new Dictionary<long, HashSet<long>>();

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
                    long? prev = null;
                    foreach (var nodeRef in element.GetProperty("nodes").EnumerateArray())
                    {
                        var current = nodeRef.GetInt64();
                        if (prev is { } p && p != current)
                        {
                            NeighborsOf(p).Add(current);
                            NeighborsOf(current).Add(p);
                        }
                        prev = current;
                    }
                    break;
            }
        }
        return new StreetNetwork(positions, neighbors);

        HashSet<long> NeighborsOf(long node) =>
            neighbors.TryGetValue(node, out var set) ? set : neighbors[node] = new HashSet<long>();
    }
}
