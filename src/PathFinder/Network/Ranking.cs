using System;
using System.Collections.Generic;
using System.Linq;

namespace PathFinder.Network;

public static class Ranking
{
    /// <summary>
    /// Sorts best (highest score) first and assigns places. Equal scores share a place:
    /// 100, 80, 80, 50 -> 1, 2, 2, 4. Ties keep their input order.
    /// </summary>
    public static List<(T Item, int Rank)> RankBy<T>(IEnumerable<T> items, Func<T, double> score)
    {
        var sorted = items.OrderByDescending(score).ToList();
        var ranked = new List<(T Item, int Rank)>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            var tied = i > 0 && score(sorted[i]) == score(sorted[i - 1]);
            ranked.Add((sorted[i], tied ? ranked[i - 1].Rank : i + 1));
        }
        return ranked;
    }
}
