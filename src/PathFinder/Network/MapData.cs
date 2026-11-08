using System.Collections.Generic;
using System.IO;
using System.Linq;
using PathFinder.Models;

namespace PathFinder.Network;

/// <summary>
/// A plain, serializable snapshot of a <see cref="CityMap"/>: what the host sends to clients so
/// every laptop plays on exactly the same map with the same IDs. No graph types, no object references.
/// Start/end aren't included: they change every round and travel in RoundStart.
/// Attribution is the credit the map's data source requires on screen (null for the test grid).
/// </summary>
public sealed record MapData(string Name, List<NodeData> Nodes, List<StreetData> Streets, string? Attribution = null);

/// <summary>An intersection. X/Y are in metres (or whatever units the map was built in).</summary>
public sealed record NodeData(int Id, double X, double Y);

/// <summary>
/// A street between two intersections. Length is what scoring uses. Name is the real street name
/// (null if unnamed); it's last and optional so snapshots and cache files from before it still load.
/// </summary>
public sealed record StreetData(int Id, int FromId, int ToId, double Length, string? ImagePath = null, string? Name = null);

public static class MapDataConverter
{
    /// <summary>Snapshot the host's canonical map for sending.</summary>
    public static MapData ToData(CityMap map) => new(
        map.Name,
        map.Graph.Vertices
            .OrderBy(v => v.Id)
            .Select(v => new NodeData(v.Id, v.X, v.Y))
            .ToList(),
        map.Graph.Edges
            .Select(e => e.Tag)
            .OrderBy(s => s.Id)
            .Select(s => new StreetData(s.Id, s.Start.Id, s.End.Id, s.Distance, s.ImagePath, s.Name))
            .ToList(),
        map.Attribution);

    /// <summary>
    /// Rebuild a <see cref="CityMap"/> from a snapshot (on clients, from the host's MatchStart).
    /// Throws <see cref="InvalidDataException"/> if a street refers to a missing intersection,
    /// since that means the snapshot is broken and the game would silently play on a different map.
    /// </summary>
    public static CityMap ToCityMap(MapData data)
    {
        var map = new CityMap(data.Name) { Attribution = data.Attribution };
        var nodes = new Dictionary<int, Intersection>();
        foreach (var n in data.Nodes)
        {
            var intersection = new Intersection(n.Id, n.X, n.Y);
            nodes.Add(n.Id, intersection);
            map.AddIntersection(intersection);
        }

        foreach (var s in data.Streets)
        {
            if (!nodes.TryGetValue(s.FromId, out var from) || !nodes.TryGetValue(s.ToId, out var to))
                throw new InvalidDataException($"Street {s.Id} refers to an intersection that isn't in the map.");
            map.AddStreet(new Street(s.Id, s.Length, from, to) { ImagePath = s.ImagePath!, Name = s.Name });
        }
        return map;
    }
}
