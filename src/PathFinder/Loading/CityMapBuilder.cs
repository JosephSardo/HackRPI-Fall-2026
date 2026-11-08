using System.Collections.Generic;
using System.Linq;
using PathFinder.Models;

namespace PathFinder.Loading;

/// <summary>Turns a raw <see cref="StreetNetwork"/> into a playable <see cref="CityMap"/>.</summary>
internal static class CityMapBuilder
{
    /// <summary>See <see cref="StreetLoader.LoadMap"/> for what the map guarantees.</summary>
    public static CityMap Build(StreetNetwork network, SearchArea area)
    {
        // Intersections inside the circle. Ways that only partly overlap it bring in nodes outside it.
        var intersections = network.Neighbors.Keys
            .Where(id => network.IsJunction(id) && network.Positions.TryGetValue(id, out var p) && area.Contains(p))
            .ToHashSet();

        var streets = StreetTracer.Trace(network, intersections);

        // Cutting at the radius can strand small groups of intersections. Keep the biggest
        // connected piece so every start/end pair has a route.
        var keep = LargestConnectedPiece(streets.Keys);

        return Assemble(network, area, keep, streets.Where(s => keep.Contains(s.Key.A)));
    }

    /// <summary>
    /// Creates the <see cref="Intersection"/>s and <see cref="Street"/>s. OSM node IDs are huge and
    /// sparse; the game uses small sequential IDs. Numbering in OSM-ID order makes the same area
    /// always produce the same IDs.
    /// </summary>
    private static CityMap Assemble(StreetNetwork network, SearchArea area,
                                    IEnumerable<long> intersectionIds,
                                    IEnumerable<KeyValuePair<(long A, long B), TracedStreet>> streets)
    {
        var map = new CityMap(area.Name);

        var intersections = new Dictionary<long, Intersection>();
        var nextIntersectionId = 1;
        foreach (var osmId in intersectionIds.Order())
        {
            var (x, y) = area.ToMetres(network.Positions[osmId]);
            var intersection = new Intersection(nextIntersectionId++, x, y);
            intersections.Add(osmId, intersection);
            map.AddIntersection(intersection);
        }

        var nextStreetId = 1;
        foreach (var ((a, b), street) in streets.OrderBy(s => s.Key))
            map.AddStreet(new Street(nextStreetId++, street.Length, intersections[a], intersections[b]) { Name = street.Name });

        return map;
    }

    /// <summary>The intersections in the biggest group that streets connect (breadth-first search).</summary>
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
}
