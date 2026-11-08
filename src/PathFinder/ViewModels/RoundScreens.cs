using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathFinder.Models;
using PathFinder.Network;
using PathFinder.Services;

namespace PathFinder.ViewModels;

// Placeholder screens for the round flow: Playing -> Waiting -> Results (10s) -> next round, or Summary.
// The UI owner can replace the views freely; these view models are the contract.

/// <summary>Counts down a round from when RoundStart arrived (laptop clocks don't agree, so no wall-clock times).</summary>
public sealed class RoundClock(TimeSpan duration)
{
    readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public TimeSpan Duration => duration;
    public TimeSpan Remaining => duration > _elapsed.Elapsed ? duration - _elapsed.Elapsed : TimeSpan.Zero;
    public bool IsUp => Remaining == TimeSpan.Zero;

    /// <summary>1 at the start of the round, 0 when time is up.</summary>
    public double FractionLeft => duration <= TimeSpan.Zero ? 0 : Remaining / duration;
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

    public PlayingViewModel(GameClient client, GameService game, RoundStartMessage round, RoundClock clock,
                            string submissions, Action<bool> done, string? hostWarning = null)
        : base(clock, submissions)
    {
        _client = client;
        Game = game;
        _round = round;
        _done = done;
        HostWarning = hostWarning;
        UpdatePressure();
    }

    /// <summary>Host only: why this match is on the test map (e.g. the real map took too long). Null otherwise.</summary>
    public string? HostWarning { get; }

    /// <summary>
    /// This player's game for the round: the map to draw (<c>Game.CityMap</c>), start/end already set,
    /// and the route being built (<c>Game.AddStreetToRoute</c> / <c>RemoveStreetFromRoute</c>).
    /// The view reads it from its DataContext.
    /// </summary>
    public GameService Game { get; }

    /// <summary>The map's required data credit ("© OpenStreetMap contributors"), or null for the test grid.</summary>
    public string? MapAttribution => Game.CityMap.Attribution;

    /// <summary>Call after changing the route so the Submit button, summary and map update.</summary>
    public void RouteChanged()
    {
        OnPropertyChanged(nameof(CanSubmitRoute));
        OnPropertyChanged(nameof(RouteSummary));
        RouteUpdated?.Invoke();
    }

    public bool CanSubmitRoute => Game.PathIsComplete() && !_finished;

    // ---- building the route by clicking streets on the map

    /// <summary>Raised whenever the route changes, so the map can redraw its highlight.</summary>
    public event Action? RouteUpdated;

    public int StartId => _round.StartId;
    public int EndId => _round.EndId;

    /// <summary>The route so far, as intersection IDs from the start.</summary>
    public IReadOnlyList<int> RouteNodeIds => Game.GetPlayerPath();

    public string RouteSummary => RouteNodeIds.Count < 2
        ? "Click a street touching the start to begin your route."
        : $"Your route: {RouteNodeIds.Count - 1} streets, {Game.PathLength():0} m" +
          (Game.PathIsComplete() ? " (destination reached)" : "");

    /// <summary>Why the last click did nothing (or what to do next); null when there's nothing to say.</summary>
    [ObservableProperty] private string? _routeHint;

    /// <summary>The street under the mouse, e.g. "Congress Street · 120 m"; null when not over a street.</summary>
    [ObservableProperty] private string? _hoverInfo;

    /// <summary>
    /// A street was clicked: clicking your last street again takes it back; clicking a street that
    /// touches the end of your route extends it; anything else explains why not.
    /// </summary>
    public void ClickStreet(Street street)
    {
        if (_finished) return;
        var path = Game.GetPlayerPath();

        if (path.Count >= 2 && Joins(street, path[^2], path[^1]))
        {
            Game.RemoveStreetFromRoute(street);
            RouteHint = null;
        }
        else if (Game.PathIsComplete())
        {
            RouteHint = "You've reached the destination. Submit, or click your last street to undo it.";
            return;
        }
        else if (Game.AddStreetToRoute(street))
        {
            RouteHint = Game.PathIsComplete() ? "Destination reached! Submit your route." : null;
        }
        else
        {
            RouteHint = path.Count < 2
                ? "Start from the green intersection: pick a street that touches it."
                : "That street doesn't connect to the end of your route (the yellow ring).";
            return;
        }
        RouteChanged();
    }

    /// <summary>True if clicking this street would change the route (used for the hover highlight).</summary>
    public bool CanClick(Street street)
    {
        if (_finished) return false;
        var path = Game.GetPlayerPath();
        if (path.Count >= 2 && Joins(street, path[^2], path[^1])) return true; // undo
        if (Game.PathIsComplete()) return false;
        return street.Start.Id == path[^1] || street.End.Id == path[^1];
    }

    [RelayCommand]
    private void UndoStreet()
    {
        var path = Game.GetPlayerPath();
        if (_finished || path.Count < 2) return;
        if (StreetBetween(path[^2], path[^1]) is { } last) ClickStreet(last);
    }

    [RelayCommand]
    private void ClearRoute()
    {
        if (_finished) return;
        Game.SetStart(_round.StartId); // resets the route
        Game.SetEnd(_round.EndId);
        RouteHint = null;
        RouteChanged();
    }

    static bool Joins(Street street, int a, int b) =>
        (street.Start.Id == a && street.End.Id == b) || (street.Start.Id == b && street.End.Id == a);

    Street? StreetBetween(int a, int b)
    {
        var map = Game.CityMap;
        return map.Graph.TryGetEdge(map.GetIntersection(a), map.GetIntersection(b), out var edge) ? edge.Tag : null;
    }

    /// <summary>Submit the route built on the map (the real Submit button for the map UI).</summary>
    [RelayCommand] private Task SubmitRouteAsync() => CanSubmitRoute ? SubmitAsync(Game.GetPlayerPath()) : Task.CompletedTask;

    public string Title => $"Round {_round.RoundNumber} of {_round.TotalRounds}";
    public string Route =>
        $"Get from {MapText.Intersection(Game.CityMap, _round.StartId)} to {MapText.Intersection(Game.CityMap, _round.EndId)}";
    public string MapInfo => _client.Map is { } map
        ? $"{map.Name}: {map.Graph.VertexCount} intersections, {map.Graph.EdgeCount} streets"
        : "No map received";

    async Task SubmitAsync(IReadOnlyList<int> nodeIds)
    {
        if (_finished || Clock.IsUp) return;
        if (await _client.SubmitPathAsync(nodeIds)) Finish(timedOut: false);
    }

    protected override void Tick()
    {
        base.Tick();
        UpdatePressure();
        if (Clock.IsUp && !_client.HasSubmitted) Finish(timedOut: true);
    }

    // ---- the big, increasingly menacing countdown

    static readonly IBrush CalmBrush = Brush.Parse("#D3EAFF");
    static readonly IBrush WarningBrush = Brush.Parse("#FFB300");
    static readonly IBrush CriticalBrush = Brush.Parse("#FF3B30");

    /// <summary>Whole seconds left (rounded up), shown huge.</summary>
    [ObservableProperty] private int _secondsLeft;

    /// <summary>0-100, for the draining bar.</summary>
    [ObservableProperty] private double _timePercent = 100;

    /// <summary>Calm, then amber from half time, then red for the last 10 seconds.</summary>
    [ObservableProperty] private IBrush _timerBrush = CalmBrush;

    /// <summary>Last 10 seconds: the number pulses.</summary>
    [ObservableProperty] private bool _isCritical;

    void UpdatePressure()
    {
        SecondsLeft = (int)Math.Ceiling(Clock.Remaining.TotalSeconds);
        TimePercent = Clock.FractionLeft * 100;
        IsCritical = SecondsLeft <= 10 && !Clock.IsUp;
        TimerBrush = IsCritical ? CriticalBrush : Clock.FractionLeft <= 0.5 ? WarningBrush : CalmBrush;
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

    // Distinct from the best route (green) and the start/end rings (green/red).
    static readonly string[] PlayerColors =
        ["#FFC83D", "#4FC3F7", "#BA68C8", "#FF8A65", "#F06292", "#AED581", "#90A4AE", "#FFD180"];
    const string BestRouteColor = "#3DDC84";

    /// <param name="scoreBreakdown">Optional extra lines on how the score was worked out (Cloud Navigator); null hides them.</param>
    public ResultsViewModel(RoundResultsMessage results, string? myId, bool isLastRound, CityMap? map = null,
                            string? scoreBreakdown = null)
        : base(new RoundClock(TimeSpan.FromSeconds(results.SecondsUntilNext)))
    {
        _isLastRound = isLastRound;
        ScoreBreakdown = scoreBreakdown;
        Title = $"Round {results.RoundNumber} results";
        Map = map;

        // One colour per player, used for both their row and their line on the map.
        var colorOf = results.Results.Select((r, i) => (r.PlayerId, Color: PlayerColors[i % PlayerColors.Length]))
                                     .ToDictionary(x => x.PlayerId, x => x.Color);
        Rows = results.Results.Select(r => new ResultRow(
            $"#{r.Rank}  {r.Name}{(r.PlayerId == myId ? " (you)" : "")}  " +
            (r.Submitted ? $"{r.Score:0.#} points" : "no path, 0 points"),
            Brush.Parse(colorOf[r.PlayerId]))).ToList();
        BestRoute = results.OptimalNodeIds.Count < 2
            ? "No best route to show."
            : $"Best route (green band): {results.OptimalNodeIds.Count - 1} streets, {results.OptimalLength:0} m" +
              Via(map, results.OptimalNodeIds);

        // Best route first (a wide translucent band underneath), then everyone's route, yours last and thickest.
        var routes = new List<Views.RouteLine> { new(results.OptimalNodeIds, "#663DDC84", 14) };
        routes.AddRange(results.Results
            .Where(r => r.Submitted && r.NodeIds.Count >= 2)
            .OrderBy(r => r.PlayerId == myId)
            .Select(r => new Views.RouteLine(r.NodeIds, colorOf[r.PlayerId], r.PlayerId == myId ? 6 : 4,
                                             Dashed: r.PlayerId != myId)));
        MapRoutes = routes;
        Tick();
    }

    /// <summary>" via Congress Street, 8th Street": the route's street names in order, or "" if none are known.</summary>
    static string Via(CityMap? map, IReadOnlyList<int> nodeIds, int maxNames = 4)
    {
        if (map is null) return "";
        var names = new List<string>();
        for (var i = 0; i + 1 < nodeIds.Count; i++)
        {
            try
            {
                var (a, b) = (map.GetIntersection(nodeIds[i]), map.GetIntersection(nodeIds[i + 1]));
                if (map.Graph.TryGetEdge(a, b, out var edge) && edge.Tag.Name is { } name && names.LastOrDefault() != name)
                    names.Add(name);
            }
            catch (KeyNotFoundException) { return ""; } // results from a different map: say nothing
        }
        if (names.Count == 0) return "";
        return " via " + string.Join(", ", names.Take(maxNames)) + (names.Count > maxNames ? ", ..." : "");
    }

    public string Title { get; }
    public string? ScoreBreakdown { get; }
    public IReadOnlyList<ResultRow> Rows { get; }
    public string BestRoute { get; }
    public IBrush BestRouteSwatch { get; } = Brush.Parse(BestRouteColor);

    /// <summary>This player's copy of the round's map (null if unavailable), and the routes to draw on it.</summary>
    public CityMap? Map { get; }
    public IReadOnlyList<Views.RouteLine> MapRoutes { get; }

    protected override void Tick()
    {
        var seconds = Math.Ceiling(Clock.Remaining.TotalSeconds);
        TimeLeft = seconds <= 0 ? "Starting..." : $"{(_isLastRound ? "Final standings" : "Next round")} in {seconds:0}s";
    }
}

/// <summary>A results line plus the colour of that player's route on the map.</summary>
public sealed record ResultRow(string Text, IBrush Color);

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
