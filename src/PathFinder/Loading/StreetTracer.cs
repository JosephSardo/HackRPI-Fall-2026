using System;
using System.Collections.Generic;
using System.Linq;

namespace PathFinder.Loading;

/// <summary>A street found by <see cref="StreetTracer"/>: its real length in metres, and its name (null if unnamed).</summary>
internal readonly record struct TracedStreet(double Length, string? Name);

/// <summary>
/// Finds the streets between intersections in a <see cref="StreetNetwork"/>, how long they
/// really are, and what they're called.
/// </summary>
internal static class StreetTracer
{
    /// <summary>
    /// Every street joining two of <paramref name="intersections"/>, keyed by its two ends (lower
    /// OSM ID first). Where two roads join the same pair of intersections, only the shorter is kept.
    /// </summary>
    public static Dictionary<(long A, long B), TracedStreet> Trace(StreetNetwork network, IReadOnlySet<long> intersections)
    {
        // Walk from each intersection along each of its streets to the next intersection. Each
        // street is found from both ends; keying by the ordered pair keeps it once.
        var streets = new Dictionary<(long A, long B), TracedStreet>();
        foreach (var start in intersections)
        foreach (var first in network.Neighbors[start])
        {
            if (WalkToIntersection(network, start, first) is not ({ } end, var street)) continue;
            if (!intersections.Contains(end) || end == start) continue; // left the area, or looped back

            var key = start < end ? (start, end) : (end, start);
            if (!streets.TryGetValue(key, out var best) || street.Length < best.Length) streets[key] = street;
        }
        return streets;
    }

    /// <summary>
    /// Follows a street from an intersection, through its first node, to the next intersection,
    /// adding up the length of every segment so curves count. Null at a dead end, or where the
    /// data runs out (a node without a position).
    /// </summary>
    private static (long End, TracedStreet Street)? WalkToIntersection(StreetNetwork network, long start, long first)
    {
        var positions = network.Positions;
        long prev = start, current = first;
        if (!positions.ContainsKey(current)) return null;

        // A street can run through several OSM ways (OSM splits roads wherever tags change), and
        // now and then they're named differently, e.g. where a road changes name mid-block. The
        // street takes the name covering the most metres; unnamed segments don't count.
        var metresPerName = new Dictionary<string, double>();
        double length = 0;
        void Add(long a, long b)
        {
            var segment = GeoMath.DistanceMeters(positions[a], positions[b]);
            length += segment;
            if (network.SegmentName(a, b) is { } name)
                metresPerName[name] = metresPerName.GetValueOrDefault(name) + segment;
        }
        Add(start, current);

        // Nodes in the middle of a street have exactly 2 neighbours (curve points, or where OSM
        // split one road into two ways): keep going the way we weren't coming from.
        while (network.Degree(current) == 2)
        {
            var next = network.Neighbors[current].First(n => n != prev);
            if (!positions.ContainsKey(next)) return null;
            Add(current, next);
            (prev, current) = (current, next);
        }

        // Reaching a node that isn't degree 2 ends the walk; degree 1 means a dead end. Dead-end
        // streets are dropped: no shortest route between two intersections ever uses one.
        if (!network.IsJunction(current)) return null;

        // Rounded so both walking directions (which add the segments in opposite orders) agree,
        // and ties go to the alphabetically first name.
        var streetName = metresPerName
            .OrderByDescending(n => Math.Round(n.Value, 3)).ThenBy(n => n.Key, StringComparer.Ordinal)
            .Select(n => n.Key).FirstOrDefault();
        return (current, new TracedStreet(length, streetName));
    }
}
