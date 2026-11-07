using System;
using System.Collections.Generic;
using System.Linq;

namespace PathFinder.Network;

public static class Ranking
{
    /// <summary>
    /// Sorts best (highest score) first and assigns places. Equal scores share a place:
    /// 100, 80, 80, 50 -> 1, 2, 2, 4. Ties keep their input order.
    /// Scores are compared after rounding (see <see cref="Key"/>), so totals that differ only by
    /// floating-point error, e.g. 233.39999999999998 vs 233.4, count as a tie.
    /// </summary>
    public static List<(T Item, int Rank)> RankBy<T>(IEnumerable<T> items, Func<T, double> score)
    {
        var sorted = items.Select(item => (Item: item, Key: Key(score(item))))
                          .OrderByDescending(x => x.Key)   // stable sort: ties stay in input order
                          .ToList();
        var ranked = new List<(T Item, int Rank)>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            var tied = i > 0 && sorted[i].Key == sorted[i - 1].Key;
            ranked.Add((sorted[i].Item, tied ? ranked[i - 1].Rank : i + 1));
        }
        return ranked;
    }

    /// <summary>Round to 6 decimal places: far finer than any score shown, far coarser than float error.</summary>
    static double Key(double score) => Math.Round(score, 6);
}
