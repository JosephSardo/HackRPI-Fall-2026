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

// Placeholder screens for the round flow: Playing -> Waiting -> Results -> (next round | Summary).
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
public abstract partial class CountdownViewModel : ViewModelBase, IShowsSubmissions, IDisposable
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    [ObservableProperty] private string _timeLeft = "";
    [ObservableProperty] private string _submissions = "";

    protected CountdownViewModel(RoundClock clock, string submissions)
    {
        Clock = clock;
        _submissions = submissions;
        _timeLeft = clock.Text;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    protected RoundClock Clock { get; }

    protected virtual void Tick() => TimeLeft = Clock.Text;

    public void Dispose() => _timer.Stop();
}

/// <summary>The round is on: the player builds and submits a path.</summary>
public partial class PlayingViewModel : CountdownViewModel
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

    // Placeholder buttons until the map view exists: they submit made-up paths.
    [RelayCommand] private Task SubmitDirectAsync() => SubmitAsync([_round.StartId, _round.EndId]);
    [RelayCommand] private Task SubmitDetourAsync() => SubmitAsync([_round.StartId, 999, _round.EndId]);

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
public partial class WaitingViewModel(RoundClock clock, string submissions, bool timedOut) : CountdownViewModel(clock, submissions)
{
    public string Message { get; } = timedOut ? "Time's up! You didn't submit a path this round." : "Path submitted!";

    protected override void Tick() => TimeLeft = Clock.IsUp ? "Waiting for results..." : $"Waiting for others... {Clock.Text}";
}

/// <summary>Round results, best first.</summary>
public partial class ResultsViewModel : ViewModelBase
{
    readonly GameClient _client;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private bool _isWaiting;

    public ResultsViewModel(GameClient client, RoundResultsMessage results, bool isLastRound)
    {
        _client = client;
        Title = $"Round {results.RoundNumber} results";
        Rows = results.Results.Select(r =>
            $"#{r.Rank}  {r.Name}{(r.PlayerId == client.PlayerId ? " (you)" : "")}  " +
            (r.Submitted ? $"{r.Score:0.#} points" : "no path, 0 points")).ToList();
        BestRoute = $"Best route: {string.Join(" -> ", results.OptimalNodeIds)} (length {results.OptimalLength:0.#})";
        NextLabel = isLastRound ? "See final standings" : "Next round";
    }

    public string Title { get; }
    public IReadOnlyList<string> Rows { get; }
    public string BestRoute { get; }
    public string NextLabel { get; }
    public bool CanContinue => !IsWaiting;

    [RelayCommand]
    private async Task NextAsync()
    {
        IsWaiting = true;
        await _client.ReadyForNextAsync();
    }
}

/// <summary>Final standings, then back to the lobby.</summary>
public partial class SummaryViewModel(MatchOverMessage over, string? myId, Action backToLobby) : ViewModelBase
{
    public IReadOnlyList<string> Rows { get; } = over.Standings
        .Select(s => $"#{s.Rank}  {s.Name}{(s.PlayerId == myId ? " (you)" : "")}  {s.TotalScore:0.#} points")
        .ToList();

    public string Winner { get; } = over.Standings.Count == 0 ? "Match over" :
        over.Standings[0].PlayerId == myId ? "You win!" : $"{over.Standings[0].Name} wins!";

    [RelayCommand]
    private void BackToLobby() => backToLobby();
}
