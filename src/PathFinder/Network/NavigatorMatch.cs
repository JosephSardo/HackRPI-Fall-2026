using System;
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
/// A round: NavigatorRound (who drives this time, and the starting heading), RoundStart, then a
/// DriverMoved at the start (distance 0), then one DriverMoved per accepted step (a refused step gets a
/// DriveRejected, which the host sends to the driver only). It ends when the driver reaches the
/// destination or time runs out (NavigatorScore, then RoundResults), and the match carries on exactly
/// like the competitive mode (results timer, next round, MatchOver).
/// The two team members swap seats every round: the lobby's driver drives the odd rounds, the lobby's
/// navigator the even ones. The match has no clock: the host passes how long the round has been running.
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
    readonly Random _random;
    readonly string _firstNavigatorId, _firstDriverId;
    readonly HashSet<string> _team;    // both seats, whichever way round they're sitting this round

    Phase _phase;
    int _round, _startId, _endId;
    readonly List<int> _path = [];     // the driver's route this round, start first
    double _distance, _heading;

    /// <param name="map">The host's canonical map (the same one <paramref name="rules"/> runs on), for checking steps.</param>
    /// <param name="navigatorId">Navigates round 1 (and every odd round); drives the even rounds.</param>
    /// <param name="driverId">Drives round 1 (and every odd round); navigates the even rounds.</param>
    /// <param name="random">Picks the starting heading; pass a seeded one to make rounds repeatable.</param>
    public NavigatorMatch(IRoundRules rules, CityMap map, IReadOnlyList<PlayerInfo> players, string navigatorId, string driverId,
                          int totalRounds, int roundSeconds, int resultsSeconds, Random? random = null)
    {
        _rules = rules;
        _map = map;
        _nodes = map.Graph.Vertices.ToDictionary(v => v.Id);
        _firstNavigatorId = NavigatorId = navigatorId;
        _firstDriverId = DriverId = driverId;
        _team = [navigatorId, driverId];
        _totalRounds = totalRounds;
        _roundSeconds = roundSeconds;
        _resultsSeconds = resultsSeconds;
        _random = random ?? new Random();
        // Everyone gets a line in the results (spectators score 0); TODO (navigator mode): leave spectators out?
        _names = players.ToDictionary(p => p.Id, p => p.Name);
        _connected = [.. _names.Keys];
        foreach (var id in _names.Keys) _totals[id] = 0;
    }

    /// <summary>Who navigates the current round (the team swaps seats every round).</summary>
    public string NavigatorId { get; private set; }
    /// <summary>Who drives the current round.</summary>
    public string DriverId { get; private set; }
    public bool IsOver => _phase == Phase.Over;

    /// <summary>
    /// The speed bonus: 1 + the fraction of the round's time left on arrival, so 2x for arriving
    /// instantly, 1.5x with half the time left, 1x at the buzzer.
    /// </summary>
    public static double SpeedMultiplier(double secondsUsed, int roundSeconds) =>
        1 + (roundSeconds <= 0 ? 0 : Math.Clamp(1 - secondsUsed / roundSeconds, 0, 1));

    public IReadOnlyList<NetMessage> Start() => StartRound(1);

    /// <summary>Not used in this mode: the route is built step by step with <see cref="DriveTo"/>.</summary>
    public IReadOnlyList<NetMessage> Submit(string playerId, int round, List<int> nodeIds) => [];

    /// <summary>
    /// The driver asks to drive to <paramref name="nodeId"/>, <paramref name="elapsed"/> into the round
    /// (by the host's clock). Accepted only from this round's driver, during the current round, and only
    /// if a street joins it to where the driver is now. A refused step returns just a
    /// <see cref="DriveRejectedMessage"/>, for the host to send to that player only (everything else is broadcast).
    /// U-turns (driving back the way you came) are allowed and count towards the distance.
    /// </summary>
    public IReadOnlyList<NetMessage> DriveTo(string playerId, int round, int nodeId, TimeSpan elapsed)
    {
        var reason = Refusal(playerId, round, nodeId, out var here, out var next, out var length);
        if (reason is not null) return [new DriveRejectedMessage(round, reason)];

        _path.Add(nodeId);
        _distance += length;
        _heading = Compass.Bearing(here, next);

        List<NetMessage> messages = [Moved()];
        if (nodeId == _endId) messages.AddRange(EndRound(elapsed));
        return messages;
    }

    /// <summary>Why a DriveTo can't be accepted (short, shown to the driver as is), or null if it can.</summary>
    string? Refusal(string playerId, int round, int nodeId, out Intersection here, out Intersection next, out double length)
    {
        here = next = null!;
        length = 0;
        if (_phase == Phase.Over) return "The match is over.";
        if (round != _round || _phase != Phase.Playing) return "That round is over.";
        if (playerId != DriverId)
            return playerId == NavigatorId ? "You're navigating this round, not driving." : "You're watching this round.";
        if (!_nodes.TryGetValue(_path[^1], out here!) || !_nodes.TryGetValue(nodeId, out next!))
            return "That intersection isn't on the map.";
        if (!_map.Graph.TryGetEdge(here, next, out var street)) return "No street goes there from where you are.";
        length = street.Tag.Distance;
        return null;
    }

    public IReadOnlyList<NetMessage> TimeUp(int round) =>
        _phase == Phase.Playing && round == _round ? EndRound(elapsed: null) : [];

    public IReadOnlyList<NetMessage> ResultsTimeUp(int round)
    {
        if (_phase != Phase.Results || round != _round) return [];
        return _round < _totalRounds ? StartRound(_round + 1) : EndMatch();
    }

    /// <summary>The team can't play without both of its players, so losing either one ends the match.</summary>
    public IReadOnlyList<NetMessage> PlayerLeft(string playerId)
    {
        if (IsOver || !_connected.Remove(playerId)) return [];
        return _team.Contains(playerId) ? EndMatch() : [];
        // TODO (navigator mode): let a spectator take over the empty seat instead of ending the match.
    }

    List<NetMessage> StartRound(int round)
    {
        _round = round;
        // Swap seats every round, so both players get to drive.
        (NavigatorId, DriverId) = round % 2 == 1 ? (_firstNavigatorId, _firstDriverId) : (_firstDriverId, _firstNavigatorId);
        (_startId, _endId) = _rules.PickStartAndEnd(round);
        _path.Clear();
        _path.Add(_startId);
        _distance = 0;
        _heading = Compass.InitialHeading(_map, _startId, _random);
        _phase = Phase.Playing;
        return
        [
            new NavigatorRoundMessage(round, NavigatorId, DriverId, _heading),
            new RoundStartMessage(round, _totalRounds, _startId, _endId, _roundSeconds),
            Moved(),
        ];
    }

    DriverMovedMessage Moved() => new(_round, DriverId, _path[^1], _heading, _distance);

    /// <summary>
    /// Scores the round: route points from the existing rules (100 for the best route, less the longer
    /// it is) x the speed multiplier (1 + the fraction of the round's time left on arrival, see
    /// <see cref="SpeedMultiplier"/>), so 0 to 200. 0 if the driver didn't arrive (<paramref name="elapsed"/>
    /// null). Both team members get the team's score; anyone else gets 0.
    /// </summary>
    List<NetMessage> EndRound(TimeSpan? elapsed)
    {
        var arrived = elapsed is not null;
        var secondsUsed = Math.Min(elapsed?.TotalSeconds ?? _roundSeconds, _roundSeconds);
        // Rounded before multiplying, so the breakdown on the Results screen adds up exactly.
        var routePoints = arrived ? Math.Round(_rules.Score(_path, _startId, _endId), 1) : 0;
        var multiplier = arrived ? Math.Round(SpeedMultiplier(secondsUsed, _roundSeconds), 2) : 1;
        var score = Math.Round(routePoints * multiplier, 1);
        // TODO (navigator mode): partial credit for getting close when time runs out?
        var breakdown = new NavigatorScoreMessage(_round, arrived, routePoints, Math.Round(secondsUsed, 1),
                                                  _roundSeconds, multiplier, score, Math.Round(_distance));

        var outcomes = _names.Keys
            .Where(id => _connected.Contains(id) || _team.Contains(id))
            .Select(id => (Id: id, Scored: _team.Contains(id) && arrived, Score: _team.Contains(id) ? score : 0));
        var results = Ranking.RankBy(outcomes, o => o.Score)
            .Select(r => new RoundResult(r.Item.Id, _names[r.Item.Id], r.Item.Scored,
                                         _team.Contains(r.Item.Id) ? [.. _path] : [], r.Item.Score, r.Rank))
            .ToList();
        foreach (var r in results) _totals[r.PlayerId] += r.Score;

        var (optimalPath, optimalLength) = _rules.OptimalRoute(_startId, _endId);
        _phase = Phase.Results;
        return [breakdown, new RoundResultsMessage(_round, results, [.. optimalPath], optimalLength, _resultsSeconds)];
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
