using System.Collections.Generic;
using System.Linq;
using PathFinder.Models;

namespace PathFinder.Services;

/// <summary>Human-readable descriptions of places on a map, from street names where available.</summary>
public static class MapText
{
    /// <summary>
    /// An intersection described by the streets that meet there, e.g. "Sage Avenue & 15th Street".
    /// Falls back to "intersection 26" when the streets have no names (e.g. the test grid without names).
    /// </summary>
    public static string Intersection(CityMap map, int id)
    {
        List<string> names;
        try
        {
            var node = map.GetIntersection(id);
            names = map.Graph.AdjacentEdges(node)
                .Select(e => e.Tag.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct()
                .OrderBy(n => n)
                .Take(2)
                .ToList();
        }
        catch (KeyNotFoundException)
        {
            return $"intersection {id}";
        }

        return names.Count switch
        {
            0 => $"intersection {id}",
            1 => $"{names[0]} (intersection {id})",
            _ => $"{names[0]} & {names[1]}",
        };
    }
}
