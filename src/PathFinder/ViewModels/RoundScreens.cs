using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathFinder.Network;

namespace PathFinder.ViewModels;

// Placeholder screens for the round flow: Playing -> Waiting -> Results (10s) -> next round, or Summary.
// The UI owner can replace the views freely; these view models are the contract.

/// <summary>Counts down a round from when RoundStart arrived (laptop clocks don't agree, so no wall-clock times).</summary>
public sealed class RoundClock(TimeSpan duration)
{
    readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public TimeSpan Remaining => duration > _elapsed.Elapsed ? duration - _elapsed.Elapsed : TimeSpan.Zero;
    public bool IsUp => Remaining == TimeSpan.Zero;
    public string Text => $"{Math.Ceiling(Remaining.TotalSeconds):0}s left";
}

/// <summary>Screens that show "N of M submitted".</summary>
public interface IShowsSubmissions
{
    string Submissions { get; set; }
}

/// <summary>Base for screens with a ticking countdown. Stops ticking when disposed (i.e. navigated away from).</summary>
public abstract partial class CountdownViewModel : ViewModelBase, IDisposable
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    [ObservableProperty] private string _timeLeft = "";

    protected CountdownViewModel(RoundClock clock)
    {
        Clock = clock;
        _timeLeft = clock.Text;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    protected RoundClock Clock { get; }

    protected virtual void Tick() => TimeLeft = Clock.Text;

    public void Dispose() => _timer.Stop();
}

/// <summary>Countdown screens shown while a round is being played.</summary>
public abstract partial class RoundInProgressViewModel(RoundClock clock, string submissions)
    : CountdownViewModel(clock), IShowsSubmissions
{
    [ObservableProperty] private string _submissions = submissions;
}

/// <summary>The round is on: the player builds and submits a path.</summary>
public partial class PlayingViewModel : RoundInProgressViewModel
{
    readonly GameClient _client;
    readonly RoundStartMessage _round;
    readonly Action<bool> _done; // true if time ran out before submitting
    bool _finished;

    public PlayingViewModel(GameClient client, RoundStartMessage round, RoundClock clock, string submissions, Action<bool> done)
        : base(clock, submissions)
    {
        _client = client;
        _round = round;
        _done = done;
    }

    public string Title => $"Round {_round.RoundNumber} of {_round.TotalRounds}";
    public string Route => $"Get from intersection {_round.StartId} to intersection {_round.EndId}";
    public string MapInfo => _client.Map is { } map
        ? $"{map.Name}: {map.Graph.VertexCount} intersections, {map.Graph.EdgeCount} streets"
        : "No map received";

    // Placeholder buttons until the map view exists: they submit real routes on this player's copy of the map.
    [RelayCommand] private Task SubmitBestAsync() => SubmitAsync(BestRoute());
    [RelayCommand] private Task SubmitLongerAsync() => SubmitAsync(LongerRoute());

    IReadOnlyList<int> BestRoute() => _client.Map is { } map
        ? Network.CityMapRoundRules.BestRoute(map, _round.StartId, _round.EndId).NodeIds
        : [];

    /// <summary>The best route with a detour first: out to a neighbour of the start and back.</summary>
    IReadOnlyList<int> LongerRoute()
    {
        var best = BestRoute();
        if (_client.Map is not { } map || best.Count < 2) return best;
        var start = map.GetIntersection(_round.StartId);
        var neighbour = map.Graph.AdjacentEdges(start)
            .Select(e => e.Source.Id == start.Id ? e.Target.Id : e.Source.Id)
            .FirstOrDefault(id => id != best[1], best[1]);
        return [_round.StartId, neighbour, .. best];
    }

    async Task SubmitAsync(IReadOnlyList<int> nodeIds)
    {
        if (_finished || Clock.IsUp) return;
        if (await _client.SubmitPathAsync(nodeIds)) Finish(timedOut: false);
    }

    protected override void Tick()
    {
        base.Tick();
        if (Clock.IsUp && !_client.HasSubmitted) Finish(timedOut: true);
    }

    void Finish(bool timedOut)
    {
        if (_finished) return;
        _finished = true;
        _done(timedOut);
    }
}

/// <summary>Submitted (or out of time): waiting for everyone else.</summary>
public partial class WaitingViewModel(RoundClock clock, string submissions, bool timedOut) : RoundInProgressViewModel(clock, submissions)
{
    public string Message { get; } = timedOut ? "Time's up! You didn't submit a path this round." : "Path submitted!";

    protected override void Tick() => TimeLeft = Clock.IsUp ? "Waiting for results..." : $"Waiting for others... {Clock.Text}";
}

/// <summary>Round results, best first. Moves on by itself when the host's results timer runs out.</summary>
public partial class ResultsViewModel : CountdownViewModel
{
    readonly bool _isLastRound;

    public ResultsViewModel(RoundResultsMessage results, string? myId, bool isLastRound)
        : base(new RoundClock(TimeSpan.FromSeconds(results.SecondsUntilNext)))
    {
        _isLastRound = isLastRound;
        Title = $"Round {results.RoundNumber} results";
        Rows = results.Results.Select(r =>
            $"#{r.Rank}  {r.Name}{(r.PlayerId == myId ? " (you)" : "")}  " +
            (r.Submitted ? $"{r.Score:0.#} points" : "no path, 0 points")).ToList();
        BestRoute = $"Best route: {string.Join(" -> ", results.OptimalNodeIds)} (length {results.OptimalLength:0.#})";
        Tick();
    }

    public string Title { get; }
    public IReadOnlyList<string> Rows { get; }
    public string BestRoute { get; }

    protected override void Tick()
    {
        var seconds = Math.Ceiling(Clock.Remaining.TotalSeconds);
        TimeLeft = seconds <= 0 ? "Starting..." : $"{(_isLastRound ? "Final standings" : "Next round")} in {seconds:0}s";
    }
}

/// <summary>Final standings, then back to the lobby.</summary>
public partial class SummaryViewModel(MatchOverMessage over, string? myId, Action backToLobby) : ViewModelBase
{
    public IReadOnlyList<string> Rows { get; } = over.Standings
        .Select(s => $"#{s.Rank}  {s.Name}{(s.PlayerId == myId ? " (you)" : "")}  {s.TotalScore:0.#} points")
        .ToList();

    public string Winner { get; } = Headline(over.Standings, myId);

    static string Headline(IReadOnlyList<PlayerTotal> standings, string? myId)
    {
        var winners = standings.Where(s => s.Rank == 1).ToList();
        var iWon = winners.Any(w => w.PlayerId == myId);
        return winners.Count switch
        {
            0 => "Match over",
            1 => iWon ? "You win!" : $"{winners[0].Name} wins!",
            _ when iWon => $"You tied for first with {string.Join(" and ", winners.Where(w => w.PlayerId != myId).Select(w => w.Name))}!",
            _ => $"It's a tie: {string.Join(" and ", winners.Select(w => w.Name))}!",
        };
    }

    [RelayCommand]
    private void BackToLobby() => backToLobby();
}
