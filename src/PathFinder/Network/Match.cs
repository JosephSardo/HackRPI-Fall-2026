using System.Collections.Generic;
using System.Linq;

namespace PathFinder.Network;

/// <summary>
/// The rules of one match, with no networking, threads or timers. The host tells it what
/// happened and broadcasts whatever messages it returns. When it returns a RoundStart, the
/// host is expected to call <see cref="TimeUp"/> for that round once the time runs out; when
/// it returns RoundResults, <see cref="ResultsTimeUp"/> once the results have been shown.
/// Not thread-safe: the host calls it from one place only.
/// </summary>
public sealed class Match
{
    enum Phase { Playing, Results, Over }

    readonly IRoundRules _rules;
    readonly int _totalRounds;
    readonly int _roundSeconds;
    readonly int _resultsSeconds;
    readonly Dictionary<string, string> _names;        // everyone who started; kept after they leave
    readonly HashSet<string> _connected;
    readonly Dictionary<string, double> _totals = [];
    readonly bool _startedWithOpponents;

    Phase _phase;
    int _round, _startId, _endId;
    readonly List<string> _roundPlayers = [];          // who was here when the round started
    readonly Dictionary<string, List<int>> _submissions = [];

    public Match(IRoundRules rules, IEnumerable<PlayerInfo> players, int totalRounds, int roundSeconds, int resultsSeconds)
    {
        _rules = rules;
        _totalRounds = totalRounds;
        _roundSeconds = roundSeconds;
        _resultsSeconds = resultsSeconds;
        _names = players.ToDictionary(p => p.Id, p => p.Name);
        _connected = [.. _names.Keys];
        foreach (var id in _names.Keys) _totals[id] = 0;
        _startedWithOpponents = _names.Count >= 2;
    }

    public bool IsOver => _phase == Phase.Over;

    public IReadOnlyList<NetMessage> Start() => StartRound(1);

    /// <summary>A player's one submission for the round. Late, repeated or stale submissions are ignored.</summary>
    public IReadOnlyList<NetMessage> Submit(string playerId, int round, List<int> nodeIds)
    {
        if (_phase != Phase.Playing || round != _round) return [];
        if (!_roundPlayers.Contains(playerId) || _submissions.ContainsKey(playerId)) return [];
        _submissions[playerId] = nodeIds;

        var playerCount = _roundPlayers.Count(id => _connected.Contains(id) || _submissions.ContainsKey(id));
        List<NetMessage> messages = [new PlayerSubmittedMessage(_round, playerId, _submissions.Count, playerCount)];
        if (EveryoneConnectedSubmitted()) messages.AddRange(EndRound());
        return messages;
    }

    /// <summary>The round's timer ran out. Ignored if that round already ended.</summary>
    public IReadOnlyList<NetMessage> TimeUp(int round) =>
        _phase == Phase.Playing && round == _round ? EndRound() : [];

    /// <summary>The results for a round have been shown long enough: next round, or the end of the match.</summary>
    public IReadOnlyList<NetMessage> ResultsTimeUp(int round)
    {
        if (_phase != Phase.Results || round != _round) return [];
        return _round < _totalRounds ? StartRound(_round + 1) : EndMatch();
    }

    /// <summary>A player disconnected: carry on without them, or stop if nobody is left to play against.</summary>
    public IReadOnlyList<NetMessage> PlayerLeft(string playerId)
    {
        if (IsOver || !_connected.Remove(playerId)) return [];
        if (_connected.Count == 0 || (_startedWithOpponents && _connected.Count < 2)) return EndMatch();
        return _phase == Phase.Playing && EveryoneConnectedSubmitted() ? EndRound() : [];
    }

    List<NetMessage> StartRound(int round)
    {
        _round = round;
        (_startId, _endId) = _rules.PickStartAndEnd(round);
        _roundPlayers.Clear();
        _roundPlayers.AddRange(_names.Keys.Where(_connected.Contains)); // join order, so ties list predictably
        _submissions.Clear();
        _phase = Phase.Playing;
        return [new RoundStartMessage(round, _totalRounds, _startId, _endId, _roundSeconds)];
    }

    bool EveryoneConnectedSubmitted() =>
        _roundPlayers.Where(_connected.Contains).All(_submissions.ContainsKey);

    List<NetMessage> EndRound()
    {
        // Everyone in the round who is still here, plus anyone who submitted and then left.
        var outcomes = _roundPlayers
            .Where(id => _connected.Contains(id) || _submissions.ContainsKey(id))
            .Select(id =>
            {
                var submitted = _submissions.TryGetValue(id, out var path);
                var score = submitted ? _rules.Score(path!, _startId, _endId) : 0;
                return (Id: id, Submitted: submitted, Path: path ?? [], Score: score);
            });

        var results = Ranking.RankBy(outcomes, o => o.Score)
            .Select(r => new RoundResult(r.Item.Id, _names[r.Item.Id], r.Item.Submitted, r.Item.Path, r.Item.Score, r.Rank))
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
