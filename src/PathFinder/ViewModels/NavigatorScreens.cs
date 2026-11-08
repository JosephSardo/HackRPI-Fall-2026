using System;
using System.Collections.Generic;
using System.Diagnostics;
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
// The team swaps seats every round, so who drives comes from the host's NavigatorRound for that round.

/// <summary>Screens that follow the driver's DriverMoved broadcasts (the lobby forwards them).</summary>
public interface IFollowsDriver
{
    void OnDriverMoved(DriverMovedMessage moved);

    /// <summary>The host refused this player's DriveTo (only the driver ever gets one).</summary>
    void OnDriveRejected(DriveRejectedMessage rejected) { }
}

public static class NavigatorScreens
{
    /// <summary>The round screen for this player: the ground view if they drive this round, the map otherwise.</summary>
    public static ViewModelBase ForRound(GameClient client, CityMap map, RoundStartMessage round, RoundClock clock)
    {
        // The host sends NavigatorRound just before RoundStart; fall back to the lobby's roles if it's missing.
        var seats = client.NavigatorRound is { } r && r.RoundNumber == round.RoundNumber ? r : null;
        var driverId = seats?.DriverId ?? client.Players.FirstOrDefault(p => p.Role == PlayerRole.Driver)?.Id;
        var navigatorId = seats?.NavigatorId ?? client.Players.FirstOrDefault(p => p.Role == PlayerRole.Navigator)?.Id;
        var heading = seats?.StartHeadingDegrees ?? Compass.InitialHeading(map, round.StartId);
        string NameOf(string? id) => client.Players.FirstOrDefault(p => p.Id == id)?.Name ?? "your teammate";

        return client.PlayerId == driverId
            ? new DriverViewModel(client, map, round, clock, heading, NameOf(navigatorId))
            : new NavigatorViewModel(map, round, clock, heading, NameOf(driverId), isSpectator: client.PlayerId != navigatorId);
    }

    /// <summary>
    /// Lobby text: who would navigate and drive first if the host starts a Cloud Navigator match now
    /// (explicit choices, plus the defaults for everyone else).
    /// </summary>
    public static string TeamPreview(IReadOnlyList<PlayerInfo> players, string? myId)
    {
        string Name(PlayerRole role) => NavigatorRoles.WithRoles(players).FirstOrDefault(p => p.Role == role) is { } p
            ? p.Name + (p.Id == myId ? " (you)" : "")
            : "nobody yet";
        return $"Cloud Navigator team: {Name(PlayerRole.Navigator)} navigates, {Name(PlayerRole.Driver)} drives " +
               "first; you swap every round.";
    }

    public static string DistanceText(double metres) => $"Distance driven: {metres:0} m";

    /// <summary>The exits from where the driver is, numbered left to right: the same list on both screens.</summary>
    public static IReadOnlyList<Compass.Exit> ExitsAt(CityMap map, int nodeId, double heading, int endId) =>
        nodeId == endId ? [] : Compass.Exits(map, nodeId, heading);

    public static string ExitText(Compass.Exit exit, int index) => $"{index + 1}.  {exit.Label}  ({exit.Length:0} m)";

    /// <summary>Results screen line for this mode: how the team's score was worked out.</summary>
    public static string ScoreBreakdown(NavigatorScoreMessage s)
    {
        if (!s.Arrived)
            return $"Didn't reach the destination in {s.RoundSeconds} s ({s.DistanceDriven:0} m driven): 0 points.";
        var left = Math.Max(0, s.RoundSeconds - s.SecondsUsed);
        return $"Arrived in {s.SecondsUsed:0.#} s of {s.RoundSeconds} s ({left:0.#} s left), {s.DistanceDriven:0} m driven.\n" +
               $"Route {s.RoutePoints:0.#} pts × speed bonus {s.SpeedMultiplier:0.00} = {s.Score:0.#} points\n" +
               "(route: 100 for the best route, less if longer; speed bonus: 1 + fraction of time left)";
    }
}

/// <summary>One button on the driver's screen: an exit from the current intersection.</summary>
public sealed record ExitOption(string Number, string Label, string Detail, IRelayCommand Drive);

/// <summary>
/// The driver's "ground view": no map, just the exits from where they are, as arrows relative to the
/// way they're facing, plus a compass rose and a rough pointer to the destination. Picking an exit asks
/// the host to drive that street; the screen only moves when the host's DriverMoved comes back (the host
/// owns the position). A refused step comes back as DriveRejected, and the exits work again.
/// </summary>
public partial class DriverViewModel : CountdownViewModel, IFollowsDriver
{
    const string DefaultHint = "Listen to your navigator, then pick a way to go.";
    /// <summary>If the host hasn't answered a step in this long, let the driver try again anyway.</summary>
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    readonly GameClient _client;
    readonly CityMap _map;
    readonly RoundStartMessage _round;
    readonly Stopwatch _waiting = new();
    int _nodeId;
    bool _arrived;

    [ObservableProperty] private IReadOnlyList<ExitOption> _exits = [];
    [ObservableProperty] private IReadOnlyList<double> _exitAngles = [];
    [ObservableProperty] private string _distance = NavigatorScreens.DistanceText(0);
    [ObservableProperty] private string _hint = DefaultHint;
    [ObservableProperty] private bool _isRejected;
    [ObservableProperty] private double _heading;
    [ObservableProperty] private double? _destinationBearing;
    [ObservableProperty] private string _facing = "";
    [ObservableProperty] private string _destinationHint = "";

    public DriverViewModel(GameClient client, CityMap map, RoundStartMessage round, RoundClock clock,
                           double startHeading, string navigatorName) : base(clock)
    {
        _client = client;
        _map = map;
        _round = round;
        NavigatorName = navigatorName;
        // Show the start straight away; the host's first DriverMoved (start, same heading) confirms it.
        Show(round.StartId, startHeading);
    }

    public string RoleBanner => "You're driving this round!";
    public string NavigatorName { get; }
    public string Title => $"Round {_round.RoundNumber} of {_round.TotalRounds}: {NavigatorName} is your navigator";

    bool IsWaiting => _waiting.IsRunning;

    public void OnDriverMoved(DriverMovedMessage moved)
    {
        if (moved.RoundNumber != _round.RoundNumber || moved.PlayerId != _client.PlayerId) return;
        StopWaiting();
        IsRejected = false;
        Distance = NavigatorScreens.DistanceText(moved.DistanceSoFar);
        Show(moved.NodeId, moved.HeadingDegrees);
    }

    public void OnDriveRejected(DriveRejectedMessage rejected)
    {
        if (rejected.RoundNumber != _round.RoundNumber) return;
        StopWaiting();
        IsRejected = true;
        Hint = $"Couldn't drive there: {rejected.Reason} Pick again.";
    }

    void Show(int nodeId, double heading)
    {
        _nodeId = nodeId;
        Heading = heading;
        Facing = $"Facing {Compass.CompassPoint(heading)} ({heading:0}°)";
        var here = _map.GetIntersection(nodeId);
        var end = _map.GetIntersection(_round.EndId);

        if (nodeId == _round.EndId)
        {
            _arrived = true;
            Exits = [];
            ExitAngles = [];
            DestinationBearing = null;
            DestinationHint = "You're at the destination!";
            Hint = "You've arrived!";
            return;
        }

        var bearing = Compass.Bearing(here, end);
        DestinationBearing = bearing;
        DestinationHint = $"Destination: {Compass.RoughDistance(Compass.Distance(here, end))}, " +
                          Compass.RoughDirection(Compass.Relative(heading, bearing));

        var exits = NavigatorScreens.ExitsAt(_map, nodeId, heading, _round.EndId);
        Exits = exits.Select((e, i) => new ExitOption($"{i + 1}", e.Label, $"{e.Length:0} m",
                                                      new RelayCommand(() => Drive(e.NodeId), CanDrive)))
                     .ToList();
        ExitAngles = exits.Select(e => e.Relative).ToList();
        if (!IsRejected)
            Hint = Exits.Count switch
            {
                0 => "Dead end... and no way out. (This shouldn't happen on a connected map.)",
                1 => "Dead end: the only way is back.",
                _ => DefaultHint,
            };
        // TODO (navigator mode): show street names once the map carries them (another branch adds Street.Name).
    }

    bool CanDrive() => !IsWaiting && !_arrived && !Clock.IsUp;

    async void Drive(int nodeId)
    {
        if (!CanDrive()) return;
        _waiting.Restart();
        IsRejected = false;
        Hint = "Driving...";
        RefreshButtons();
        try { await _client.DriveToAsync(nodeId); }
        catch (Exception) { StopWaiting(); } // connection problems are reported by the client's Disconnected
    }

    void StopWaiting()
    {
        _waiting.Reset();
        RefreshButtons();
    }

    void RefreshButtons()
    {
        foreach (var exit in Exits) exit.Drive.NotifyCanExecuteChanged();
    }

    protected override void Tick()
    {
        TimeLeft = _arrived ? "Arrived! Waiting for results..." : Clock.IsUp ? "Time's up! Waiting for results..." : Clock.Text;
        if (Clock.IsUp && Exits.Count > 0) Exits = [];
        if (IsWaiting && _waiting.Elapsed > ReplyTimeout && !Clock.IsUp)
        {
            StopWaiting();
            Show(_nodeId, Heading);
            Hint = "The host didn't answer. Try again.";
        }
    }
}

/// <summary>
/// The navigator's view "from the clouds": the whole map, the destination, and the driver's trail and
/// position (with an arrow for the way they face), updated from DriverMoved, plus the driver's exits
/// exactly as they see them. Nothing to click: the navigator talks. Also used for spectators.
/// </summary>
public partial class NavigatorViewModel : CountdownViewModel, IFollowsDriver
{
    const string TrailColor = "#FFC83D", TrailEdgeColor = "#1d1945",
                 DestinationColor = "#FF5252", StartColor = "#3DDC84", DriverColor = "#4FC3F7";

    readonly CityMap _map;
    readonly RoundStartMessage _round;
    readonly List<int> _trail;

    [ObservableProperty] private IReadOnlyList<RouteLine> _routes = [];
    [ObservableProperty] private IReadOnlyList<MapMarker> _markers = [];
    [ObservableProperty] private string _distance = NavigatorScreens.DistanceText(0);
    [ObservableProperty] private string _driverStatus = "";
    [ObservableProperty] private IReadOnlyList<string> _driverExits = [];

    public NavigatorViewModel(CityMap map, RoundStartMessage round, RoundClock clock, double startHeading,
                              string driverName, bool isSpectator) : base(clock)
    {
        _map = map;
        Map = map;
        _round = round;
        DriverName = driverName;
        IsSpectator = isSpectator;
        _trail = [round.StartId];
        Update(round.StartId, startHeading);
    }

    public CityMap Map { get; }
    public bool IsSpectator { get; }
    public string DriverName { get; }
    public string RoleBanner => IsSpectator ? $"You're watching this round: {DriverName} is driving" : "You're navigating this round!";
    public string Title => $"Round {_round.RoundNumber} of {_round.TotalRounds}: {DriverName} is driving";
    public string Instructions => IsSpectator
        ? "Watch the driver (blue arrow) try to reach the red ring."
        : $"Talk {DriverName} to the red ring. They can't see the map, only the exits below, numbered left to right " +
          "the way they're facing: \"take exit 2\", \"turn right\", \"straight on\".";

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
        // The trail is drawn twice (a dark edge under a bright line) so it stands out on any street colour.
        Routes = [new RouteLine([.. _trail], TrailEdgeColor, 10), new RouteLine([.. _trail], TrailColor, 6)];
        var arrived = driverAt == _round.EndId;
        Markers =
        [
            new MapMarker(_round.StartId, StartColor, 8),
            new MapMarker(_round.EndId, DestinationColor, 12),
            new MapMarker(driverAt, DriverColor, 7, Filled: true, HeadingDegrees: arrived ? null : heading),
        ];
        DriverStatus = arrived
            ? $"{DriverName} has arrived!"
            : $"{DriverName} is facing {Compass.CompassPoint(heading)} ({heading:0}°), {_trail.Count - 1} streets so far";
        DriverExits = NavigatorScreens.ExitsAt(_map, driverAt, heading, _round.EndId)
            .Select(NavigatorScreens.ExitText).ToList();
    }

    protected override void Tick() => TimeLeft = Clock.IsUp ? "Time's up! Waiting for results..." : Clock.Text;
}
