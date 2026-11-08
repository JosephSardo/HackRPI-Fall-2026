using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathFinder.Models;
using PathFinder.Network;
using PathFinder.Services;
using PathFinder.Views;

namespace PathFinder.ViewModels;

// Cloud Navigator screens. A round shows DriverView on the driver's laptop and NavigatorView on
// everyone else's (the navigator, plus anyone watching). Results and Summary are the shared screens.
// Placeholder layouts: the view models are the contract.

/// <summary>Screens that follow the driver's DriverMoved broadcasts (the lobby forwards them).</summary>
public interface IFollowsDriver
{
    void OnDriverMoved(DriverMovedMessage moved);
}

public static class NavigatorScreens
{
    /// <summary>The round screen for this player: the ground view if they drive, the map otherwise.</summary>
    public static ViewModelBase ForRound(GameClient client, CityMap map, RoundStartMessage round, RoundClock clock)
    {
        var me = client.Players.FirstOrDefault(p => p.Id == client.PlayerId);
        return me?.Role == PlayerRole.Driver
            ? new DriverViewModel(client, map, round, clock)
            : new NavigatorViewModel(map, round, clock, isSpectator: me?.Role != PlayerRole.Navigator);
    }

    /// <summary>
    /// Lobby text: who would navigate and drive if the host starts a Cloud Navigator match now
    /// (explicit choices, plus the defaults for everyone else).
    /// </summary>
    public static string TeamPreview(IReadOnlyList<PlayerInfo> players, string? myId)
    {
        string Name(PlayerRole role) => NavigatorRoles.WithRoles(players).FirstOrDefault(p => p.Role == role) is { } p
            ? p.Name + (p.Id == myId ? " (you)" : "")
            : "nobody yet";
        return $"Cloud Navigator team: {Name(PlayerRole.Navigator)} navigates, {Name(PlayerRole.Driver)} drives.";
    }

    public static string DistanceText(double metres) => $"Distance driven: {metres:0} m";
}

/// <summary>One button on the driver's screen: an exit from the current intersection.</summary>
public sealed record ExitOption(string Label, string Detail, IRelayCommand Drive);

/// <summary>
/// The driver's "ground view": no map, just the exits from where they are, as arrows relative to the
/// way they're facing. Picking one asks the host to drive that street; the screen only moves when the
/// host's DriverMoved comes back (the host owns the position), so a refused step simply does nothing.
/// </summary>
public partial class DriverViewModel : CountdownViewModel, IFollowsDriver
{
    readonly GameClient _client;
    readonly CityMap _map;
    readonly RoundStartMessage _round;
    bool _waitingForHost;

    [ObservableProperty] private IReadOnlyList<ExitOption> _exits = [];
    [ObservableProperty] private string _distance = NavigatorScreens.DistanceText(0);
    [ObservableProperty] private string _hint = "Listen to your navigator, then pick a way to go.";

    public DriverViewModel(GameClient client, CityMap map, RoundStartMessage round, RoundClock clock) : base(clock)
    {
        _client = client;
        _map = map;
        _round = round;
        // Show the start straight away; the host's first DriverMoved (start, same heading) confirms it.
        Show(round.StartId, Compass.InitialHeading(map, round.StartId));
    }

    public string Title => $"Round {_round.RoundNumber} of {_round.TotalRounds}: you're driving";

    public void OnDriverMoved(DriverMovedMessage moved)
    {
        if (moved.RoundNumber != _round.RoundNumber || moved.PlayerId != _client.PlayerId) return;
        _waitingForHost = false;
        Distance = NavigatorScreens.DistanceText(moved.DistanceSoFar);
        Show(moved.NodeId, moved.HeadingDegrees);
    }

    void Show(int nodeId, double heading)
    {
        if (nodeId == _round.EndId)
        {
            Exits = [];
            Hint = "You've arrived!";
            return;
        }
        Exits = Compass.Exits(_map, nodeId, heading)
            .Select(e => new ExitOption(e.Label, $"{e.Length:0} m", new RelayCommand(() => Drive(e.NodeId))))
            .ToList();
        Hint = Exits.Count switch
        {
            0 => "Dead end... and no way out. (This shouldn't happen on a connected map.)",
            1 => "Dead end: the only way is back.",
            _ => "Listen to your navigator, then pick a way to go.",
        };
        // TODO (navigator mode): show street names / landmarks if the map ever carries them, so the
        // navigator has something to talk about besides angles.
    }

    async void Drive(int nodeId)
    {
        if (_waitingForHost || Clock.IsUp) return;
        _waitingForHost = true;
        Hint = "Driving...";
        // TODO (navigator mode): if the host refuses (stale click), we wait forever; add a timeout or a reply.
        try { await _client.DriveToAsync(nodeId); }
        catch (Exception) { _waitingForHost = false; } // connection problems are reported by the client's Disconnected
    }

    protected override void Tick()
    {
        TimeLeft = Clock.IsUp ? "Time's up! Waiting for results..." : Clock.Text;
        if (Clock.IsUp && Exits.Count > 0) Exits = [];
    }
}

/// <summary>
/// The navigator's view "from the clouds": the whole map, the destination, and the driver's trail and
/// position, updated from DriverMoved. Nothing to click: the navigator talks. Also used for spectators.
/// </summary>
public partial class NavigatorViewModel : CountdownViewModel, IFollowsDriver
{
    const string TrailColor = "#FFC83D", DestinationColor = "#FF5252", StartColor = "#3DDC84", DriverColor = "#4FC3F7";

    readonly RoundStartMessage _round;
    readonly List<int> _trail;

    [ObservableProperty] private IReadOnlyList<RouteLine> _routes = [];
    [ObservableProperty] private IReadOnlyList<MapMarker> _markers = [];
    [ObservableProperty] private string _distance = NavigatorScreens.DistanceText(0);
    [ObservableProperty] private string _driverStatus = "";

    public NavigatorViewModel(CityMap map, RoundStartMessage round, RoundClock clock, bool isSpectator) : base(clock)
    {
        Map = map;
        _round = round;
        IsSpectator = isSpectator;
        _trail = [round.StartId];
        Update(round.StartId, Compass.InitialHeading(map, round.StartId));
    }

    public CityMap Map { get; }
    public bool IsSpectator { get; }
    public string Title => $"Round {_round.RoundNumber} of {_round.TotalRounds}: " +
                           (IsSpectator ? "watching" : "you're the navigator");
    public string Instructions => IsSpectator
        ? "Watch the driver (blue) try to reach the red ring."
        : "Talk your driver to the red ring. They can only see which way the roads turn, so tell them left, right or straight.";

    public void OnDriverMoved(DriverMovedMessage moved)
    {
        if (moved.RoundNumber != _round.RoundNumber) return;
        if (_trail[^1] != moved.NodeId) _trail.Add(moved.NodeId); // the round's first DriverMoved repeats the start
        Distance = NavigatorScreens.DistanceText(moved.DistanceSoFar);
        Update(moved.NodeId, moved.HeadingDegrees);
    }

    void Update(int driverAt, double heading)
    {
        // New lists each time, so the map control sees a changed property and redraws.
        Routes = [new RouteLine([.. _trail], TrailColor, 5)];
        Markers =
        [
            new MapMarker(_round.StartId, StartColor, 8),
            new MapMarker(_round.EndId, DestinationColor, 12),
            new MapMarker(driverAt, DriverColor, 7, Filled: true),
        ];
        DriverStatus = driverAt == _round.EndId
            ? "The driver has arrived!"
            : $"Driver facing {Compass.CompassPoint(heading)} ({heading:0}°), {_trail.Count - 1} streets so far";
        // TODO (navigator mode): draw the heading as an arrow on the map instead of only in text.
    }

    protected override void Tick() => TimeLeft = Clock.IsUp ? "Time's up! Waiting for results..." : Clock.Text;
}
