using System;
using System.Collections.Generic;

namespace PathFinder.Network;

/// <summary>
/// The game rules the host needs to run rounds. Keeps the network code independent of the
/// map/scoring code: swap <see cref="StubRoundRules"/> for an adapter over CityMap/PlayerRoute
/// once that lands, and nothing else in Network/ changes.
/// </summary>
public interface IRoundRules
{
    /// <summary>Start and end intersection IDs for a round.</summary>
    (int StartId, int EndId) PickStartAndEnd(int roundNumber);

    /// <summary>Score for a complete path (node IDs, start to end). Higher is better.</summary>
    double Score(IReadOnlyList<int> nodeIds, int startId, int endId);

    /// <summary>Shortest route between start and end, for the results screen.</summary>
    (IReadOnlyList<int> NodeIds, double Length) OptimalRoute(int startId, int endId);
}

/// <summary>
/// Placeholder rules with no real map: fixed fake start/end IDs, and a score that just
/// rewards shorter paths. Lets the round flow be built and tested before the map code exists.
/// </summary>
public sealed class StubRoundRules : IRoundRules
{
    public (int StartId, int EndId) PickStartAndEnd(int roundNumber) => (roundNumber * 10, roundNumber * 10 + 1);

    /// <summary>100 for a direct 2-node path, less for longer ones.</summary>
    public double Score(IReadOnlyList<int> nodeIds, int startId, int endId) =>
        Math.Round(100.0 * 2 / Math.Max(2, nodeIds.Count), 1);

    public (IReadOnlyList<int> NodeIds, double Length) OptimalRoute(int startId, int endId) =>
        ([startId, endId], 1.0);
}
