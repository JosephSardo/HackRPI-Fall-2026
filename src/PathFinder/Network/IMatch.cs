using System.Collections.Generic;

namespace PathFinder.Network;

/// <summary>
/// What <see cref="GameHost"/> needs from a match's rules, whichever mode is being played:
/// <see cref="Match"/> (competitive) or <see cref="NavigatorMatch"/> (Cloud Navigator). Same contract
/// as Match: no networking, threads or timers; the host broadcasts whatever each call returns, calls
/// <see cref="TimeUp"/> when a round's time runs out and <see cref="ResultsTimeUp"/> after results.
/// </summary>
public interface IMatch
{
    bool IsOver { get; }
    IReadOnlyList<NetMessage> Start();
    IReadOnlyList<NetMessage> Submit(string playerId, int round, List<int> nodeIds);
    IReadOnlyList<NetMessage> TimeUp(int round);
    IReadOnlyList<NetMessage> ResultsTimeUp(int round);
    IReadOnlyList<NetMessage> PlayerLeft(string playerId);
}
