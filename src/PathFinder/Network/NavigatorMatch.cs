using System.Collections.Generic;
using System.Linq;
using PathFinder.Models;
using PathFinder.Services;

namespace PathFinder.Network;

/// <summary>
/// The rules of a Cloud Navigator match: one team (a navigator and a driver) against the clock.
/// Like <see cref="Match"/>, it has no networking, threads or timers: the host tells it what happened
/// and broadcasts what it returns. The host owns the driver's position: clients only ask to move
/// (<see cref="DriveTo"/>), and every step is checked against the host's canonical map.
///
/// A round: RoundStart, then a DriverMoved at the start (distance 0), then one DriverMoved per
/// accepted step. It ends when the driver reaches the destination or time runs out (RoundResults),
/// and the match carries on exactly like the competitive mode (results timer, next round, MatchOver).
/// Not thread-safe: the host calls it from its single event loop only.
/// </summary>
public sealed class NavigatorMatch : IMatch
{
    enum Phase { Playing, Results, Over }

    readonly IRoundRules _rules;
    readonly CityMap _map;
    readonly Dictionary<int, Intersection> _nodes;
    readonly int _totalRounds, _roundSeconds, _resultsSeconds;
    readonly Dictionary<string, string> _names;
    readonly HashSet<string> _connected;
    readonly Dictionary<string, double> _totals = [];

    Phase _phase;
    int _round, _startId, _endId;
    readonly List<int> _path = [];     // the driver's route this round, start first
    double _distance, _heading;

    /// <param name="map">The host's canonical map (the same one <paramref name="rules"/> runs on), for checking steps.</param>
    public NavigatorMatch(IRoundRules rules, CityMap map, IReadOnlyList<PlayerInfo> players, string navigatorId, string driverId,
                          int totalRounds, int roundSeconds, int resultsSeconds)
    {
        _rules = rules;
        _map = map;
        _nodes = map.Graph.Vertices.ToDictionary(v => v.Id);
        NavigatorId = navigatorId;
        DriverId = driverId;
        _totalRounds = totalRounds;
        _roundSeconds = roundSeconds;
        _resultsSeconds = resultsSeconds;
        // Everyone gets a line in the results (spectators score 0); TODO (navigator mode): leave spectators out?
        _names = players.ToDictionary(p => p.Id, p => p.Name);
        _connected = [.. _names.Keys];
        foreach (var id in _names.Keys) _totals[id] = 0;
    }

    public string NavigatorId { get; }
    public string DriverId { get; }
    public bool IsOver => _phase == Phase.Over;

    public IReadOnlyList<NetMessage> Start() => StartRound(1);

    /// <summary>Not used in this mode: the route is built step by step with <see cref="DriveTo"/>.</summary>
    public IReadOnlyList<NetMessage> Submit(string playerId, int round, List<int> nodeIds) => [];

    /// <summary>
    /// The driver asks to drive to <paramref name="nodeId"/>. Accepted only from the driver, during the
    /// current round, and only if a street joins it to where the driver is now; otherwise ignored.
    /// U-turns (driving back the way you came) are allowed and count towards the distance.
    /// </summary>
    public IReadOnlyList<NetMessage> DriveTo(string playerId, int round, int nodeId)
    {
        if (_phase != Phase.Playing || round != _round || playerId != DriverId) return [];
        if (!_nodes.TryGetValue(_path[^1], out var here) || !_nodes.TryGetValue(nodeId, out var next)) return [];
        if (!_map.Graph.TryGetEdge(here, next, out var street)) return [];
        // TODO (navigator mode): tell the driver why a step was refused? Only matters for a buggy or stale client:
        // the ground view only offers real exits.

        _path.Add(nodeId);
        _distance += street.Tag.Distance;
        _heading = Compass.Bearing(here, next);

        List<NetMessage> messages = [Moved()];
        if (nodeId == _endId) messages.AddRange(EndRound(arrived: true));
        return messages;
    }

    public IReadOnlyList<NetMessage> TimeUp(int round) =>
        _phase == Phase.Playing && round == _round ? EndRound(arrived: false) : [];

    public IReadOnlyList<NetMessage> ResultsTimeUp(int round)
    {
        if (_phase != Phase.Results || round != _round) return [];
        return _round < _totalRounds ? StartRound(_round + 1) : EndMatch();
    }

    /// <summary>The team can't play without both of its players, so losing either one ends the match.</summary>
    public IReadOnlyList<NetMessage> PlayerLeft(string playerId)
    {
        if (IsOver || !_connected.Remove(playerId)) return [];
        return playerId == NavigatorId || playerId == DriverId ? EndMatch() : [];
        // TODO (navigator mode): let a spectator take over the empty seat instead of ending the match.
    }

    List<NetMessage> StartRound(int round)
    {
        _round = round;
        (_startId, _endId) = _rules.PickStartAndEnd(round);
        _path.Clear();
        _path.Add(_startId);
        _distance = 0;
        _heading = Compass.InitialHeading(_map, _startId);
        _phase = Phase.Playing;
        return [new RoundStartMessage(round, _totalRounds, _startId, _endId, _roundSeconds), Moved()];
    }

    DriverMovedMessage Moved() => new(_round, DriverId, _path[^1], _heading, _distance);

    /// <summary>
    /// Scores the team's route with the existing rules (100 for the best route, less the longer it is),
    /// or 0 if the driver didn't arrive. Both team members get the team's score; anyone else gets 0.
    /// </summary>
    List<NetMessage> EndRound(bool arrived)
    {
        var score = arrived ? _rules.Score(_path, _startId, _endId) : 0;
        // TODO (navigator mode): time component. Match has no clock on purpose; have the host pass the
        // elapsed time into DriveTo (it already knows when the round started) and add a bonus for time left.
        // TODO (navigator mode): partial credit for getting close when time runs out?

        var team = new HashSet<string> { NavigatorId, DriverId };
        var outcomes = _names.Keys
            .Where(id => _connected.Contains(id) || team.Contains(id))
            .Select(id => (Id: id, Scored: team.Contains(id) && arrived, Score: team.Contains(id) ? score : 0));
        var results = Ranking.RankBy(outcomes, o => o.Score)
            .Select(r => new RoundResult(r.Item.Id, _names[r.Item.Id], r.Item.Scored,
                                         team.Contains(r.Item.Id) ? [.. _path] : [], r.Item.Score, r.Rank))
            .ToList();
        foreach (var r in results) _totals[r.PlayerId] += r.Score;

        var (optimalPath, optimalLength) = _rules.OptimalRoute(_startId, _endId);
        _phase = Phase.Results;
        return [new RoundResultsMessage(_round, results, [.. optimalPath], optimalLength, _resultsSeconds)];
    }

    List<NetMessage> EndMatch()
    {
        var standings = Ranking.RankBy(_totals, t => t.Value)
            .Select(r => new PlayerTotal(r.Item.Key, _names[r.Item.Key], r.Item.Value, r.Rank))
            .ToList();
        _phase = Phase.Over;
        return [new MatchOverMessage(standings)];
    }
}
