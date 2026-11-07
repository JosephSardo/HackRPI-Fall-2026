using System;
using System.Collections.Generic;

namespace PathFinder.Network;

/// <summary>
/// The game rules the host needs to run rounds. Keeps the network code independent of the
/// map/scoring code. The real implementation is <see cref="CityMapRoundRules"/>.
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
