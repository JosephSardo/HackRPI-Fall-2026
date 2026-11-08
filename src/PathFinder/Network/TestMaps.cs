using System;
using System.Collections.Generic;
using System.Linq;
using PathFinder.Models;

namespace PathFinder.Network;

/// <summary>
/// A built-in map for playing before real OpenStreetMap maps exist, and as the host's fallback
/// when downloading a map fails. Same seed = same map.
/// </summary>
public static class TestMaps
{
    /// <summary>
    /// A <paramref name="columns"/> x <paramref name="rows"/> street grid, <paramref name="spacing"/> metres apart.
    /// Street lengths vary up to 60% above the spacing (so the shortest route isn't just "any staircase"),
    /// and about <paramref name="removeFraction"/> of streets are removed, never disconnecting the map.
    /// Streets have made-up names so names can be tried offline: rows are avenues ("1st Ave",
    /// "2nd Ave"...), columns are lettered streets ("A St", "B St"...).
    /// </summary>
    public static CityMap Grid(int columns = 6, int rows = 6, double spacing = 100, double removeFraction = 0.15, int seed = 2026)
    {
        var random = new Random(seed);
        int Id(int col, int row) => row * columns + col + 1;

        // Candidate streets between horizontal and vertical neighbours.
        var streets = new List<(int A, int B, double Length)>();
        for (var row = 0; row < rows; row++)
        for (var col = 0; col < columns; col++)
        {
            if (col + 1 < columns) streets.Add((Id(col, row), Id(col + 1, row), Length()));
            if (row + 1 < rows) streets.Add((Id(col, row), Id(col, row + 1), Length()));
        }
        double Length() => Math.Round(spacing * (1 + 0.6 * random.NextDouble()), 1);

        // Knock out some streets, keeping the map connected.
        var toRemove = (int)(streets.Count * removeFraction);
        foreach (var candidate in streets.OrderBy(_ => random.Next()).ToList())
        {
            if (toRemove == 0) break;
            var without = streets.Where(s => s != candidate).ToList();
            if (!IsConnected(columns * rows, without)) continue;
            streets = without;
            toRemove--;
        }

        var map = new CityMap($"Test grid {columns}x{rows}");
        var intersections = new Dictionary<int, Intersection>();
        for (var row = 0; row < rows; row++)
        for (var col = 0; col < columns; col++)
        {
            var intersection = new Intersection(Id(col, row), col * spacing, row * spacing);
            intersections[intersection.Id] = intersection;
            map.AddIntersection(intersection);
        }
        var streetId = 1;
        foreach (var (a, b, length) in streets)
            map.AddStreet(new Street(streetId++, length, intersections[a], intersections[b]) { Name = NameOf(a, b) });
        return map;

        // Ids are row-major, so a street whose ends are a whole row apart runs down a column.
        string NameOf(int a, int b) => Math.Abs(a - b) == columns
            ? $"{Letters((a - 1) % columns)} St"
            : $"{Ordinal((a - 1) / columns + 1)} Ave";
    }

    static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // A, B, ... Z, AA, AB, ... like spreadsheet columns.
    static string Letters(int index) => index < 26 ? ((char)('A' + index)).ToString() : Letters(index / 26 - 1) + (char)('A' + index % 26);

    static bool IsConnected(int nodeCount, List<(int A, int B, double Length)> streets)
    {
        var neighbours = streets.SelectMany(s => new[] { (s.A, s.B), (s.B, s.A) }).ToLookup(p => p.Item1, p => p.Item2);
        var seen = new HashSet<int> { 1 };
        var queue = new Queue<int>([1]);
        while (queue.TryDequeue(out var node))
            foreach (var next in neighbours[node])
                if (seen.Add(next)) queue.Enqueue(next);
        return seen.Count == nodeCount;
    }
}
