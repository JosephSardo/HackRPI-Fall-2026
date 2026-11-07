using System;
using System.Collections.Generic;
using System.Linq;
using PathFinder.Models;
using PathFinder.Services;
using QuikGraph.Algorithms;

namespace PathFinder.Network;

/// <summary>
/// Real round rules on the host's canonical <see cref="CityMap"/>. The host owns this map and is
/// the only one that changes it: each call sets the round's StartId/EndId on it and then uses the
/// core code (PathfindingService, the core scoring formula) as-is. Only ever called from the host's
/// single event loop, so mutating the map needs no locking.
/// </summary>
public sealed class CityMapRoundRules(CityMap map, int minRouteStreets = 4, int? seed = null) : IRoundRules
{
    readonly Random _random = seed is int s ? new Random(s) : new Random();
    readonly HashSet<(int, int)> _usedPairs = [];
    readonly Dictionary<(int, int), double?> _optimalLengths = [];

    /// <summary>The canonical map these rules run on (what the host snapshots and sends to players).</summary>
    public CityMap Map { get; } = map;

    /// <summary>
    /// A random start/end pair whose best route uses at least <paramref name="minRouteStreets"/> streets,
    /// not reused within this match. Falls back to the longest route found if the map is too small.
    /// </summary>
    public (int StartId, int EndId) PickStartAndEnd(int roundNumber)
    {
        var ids = Map.Graph.Vertices.Select(v => v.Id).ToList();
        if (ids.Count < 2) throw new InvalidOperationException("The map needs at least two intersections.");

        (int, int)? best = null;
        var bestStreets = -1;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var start = ids[_random.Next(ids.Count)];
            var end = ids[_random.Next(ids.Count)];
            if (start == end || _usedPairs.Contains((start, end)) || _usedPairs.Contains((end, start))) continue;

            var streets = ShortestRoute(Map, start, end)?.Count ?? 0; // 0 = unreachable
            if (streets > bestStreets) (best, bestStreets) = ((start, end), streets);
            if (streets >= minRouteStreets) break;
        }

        if (best is not { } pair || bestStreets <= 0)
            throw new InvalidOperationException("Couldn't find a start and end with a route between them.");
        _usedPairs.Add(pair);
        return pair;
    }

    /// <summary>
    /// The core scoring formula on the player's path (node IDs, start to end): 100 for the best
    /// route, less the longer it is. A path that doesn't follow streets scores 0 (can't happen with
    /// the real UI, which only builds connected paths; this just keeps a buggy client from crashing the host).
    /// </summary>
    public double Score(IReadOnlyList<int> nodeIds, int startId, int endId)
    {
        if (nodeIds.Count < 2 || nodeIds[0] != startId || nodeIds[^1] != endId) return 0;

        double length = 0;
        for (var i = 1; i < nodeIds.Count; i++)
        {
            if (StreetBetween(nodeIds[i - 1], nodeIds[i]) is not { } street) return 0;
            length += street.Distance;
        }

        if (OptimalLength(startId, endId) is not { } optimal || optimal <= 0) return 0;
        // Same formula as GameService.CalculatePlayerScore; keep the two in sync.
        return int.Max(0, (int)(100 * (1 - Math.Abs(length - optimal) / optimal)));
    }

    /// <summary>The best route as node IDs from start to end, and its length.</summary>
    public (IReadOnlyList<int> NodeIds, double Length) OptimalRoute(int startId, int endId) =>
        BestRoute(Map, startId, endId);

    /// <summary>
    /// Shortest route on any map (also usable on a client's copy, e.g. for the placeholder submit buttons).
    /// Empty if end can't be reached.
    /// </summary>
    public static (IReadOnlyList<int> NodeIds, double Length) BestRoute(CityMap map, int startId, int endId)
    {
        var route = ShortestRoute(map, startId, endId);
        if (route is null) return ([], 0);

        var nodes = new List<int> { startId };
        var current = map.GetIntersection(startId);
        foreach (var edge in route)
        {
            current = edge.Source.Id == current.Id ? edge.Target : edge.Source; // undirected: walk to the other end
            nodes.Add(current.Id);
        }
        return (nodes, route.Sum(e => e.Tag.Distance));
    }

    /// <summary>Best route length via the core PathfindingService (which reads StartId/EndId off the map).</summary>
    double? OptimalLength(int startId, int endId)
    {
        if (_optimalLengths.TryGetValue((startId, endId), out var cached)) return cached;
        Map.StartId = startId;
        Map.EndId = endId;
        return _optimalLengths[(startId, endId)] = PathfindingService.FindOptimalDistance(Map);
    }

    /// <summary>Dijkstra over street lengths; null if end can't be reached from start.</summary>
    static List<QuikGraph.TaggedUndirectedEdge<Intersection, Street>>? ShortestRoute(CityMap map, int startId, int endId)
    {
        var tryGetPath = map.Graph.ShortestPathsDijkstra(e => e.Tag.Distance, map.GetIntersection(startId));
        return tryGetPath(map.GetIntersection(endId), out var path) ? path.ToList() : null;
    }

    Street? StreetBetween(int a, int b)
    {
        var from = Map.Graph.Vertices.FirstOrDefault(v => v.Id == a);
        var to = Map.Graph.Vertices.FirstOrDefault(v => v.Id == b);
        if (from is null || to is null) return null;
        return Map.Graph.TryGetEdge(from, to, out var edge) ? edge.Tag : null;
    }
}
