using System.Collections.Generic;
using System.Linq;

namespace PathFinder.Loading;

/// <summary>
/// Finds the streets between intersections in a <see cref="StreetNetwork"/>, and how long they
/// really are.
/// </summary>
internal static class StreetTracer
{
    /// <summary>
    /// Every street joining two of <paramref name="intersections"/>, keyed by its two ends (lower
    /// OSM ID first), with its length in metres. Where two roads join the same pair of
    /// intersections, only the shorter is kept.
    /// </summary>
    public static Dictionary<(long A, long B), double> Trace(StreetNetwork network, IReadOnlySet<long> intersections)
    {
        // Walk from each intersection along each of its streets to the next intersection. Each
        // street is found from both ends; keying by the ordered pair keeps it once.
        var streets = new Dictionary<(long A, long B), double>();
        foreach (var start in intersections)
        foreach (var first in network.Neighbors[start])
        {
            if (WalkToIntersection(network, start, first) is not ({ } end, var length)) continue;
            if (!intersections.Contains(end) || end == start) continue; // left the area, or looped back

            var key = start < end ? (start, end) : (end, start);
            if (!streets.TryGetValue(key, out var best) || length < best) streets[key] = length;
        }
        return streets;
    }

    /// <summary>
    /// Follows a street from an intersection, through its first node, to the next intersection,
    /// adding up the length of every segment so curves count. Null at a dead end, or where the
    /// data runs out (a node without a position).
    /// </summary>
    private static (long End, double Length)? WalkToIntersection(StreetNetwork network, long start, long first)
    {
        var positions = network.Positions;
        long prev = start, current = first;
        if (!positions.ContainsKey(current)) return null;
        double length = GeoMath.DistanceMeters(positions[start], positions[current]);

        // Nodes in the middle of a street have exactly 2 neighbours (curve points, or where OSM
        // split one road into two ways): keep going the way we weren't coming from.
        while (network.Degree(current) == 2)
        {
            var next = network.Neighbors[current].First(n => n != prev);
            if (!positions.ContainsKey(next)) return null;
            length += GeoMath.DistanceMeters(positions[current], positions[next]);
            (prev, current) = (current, next);
        }

        // Reaching a node that isn't degree 2 ends the walk; degree 1 means a dead end. Dead-end
        // streets are dropped: no shortest route between two intersections ever uses one.
        return network.IsJunction(current) ? (current, length) : null;
    }
}
