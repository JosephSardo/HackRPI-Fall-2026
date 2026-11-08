using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathFinder.Mapping;
using PathFinder.Network;
using PathFinder.Services;

namespace PathFinder.ViewModels;

/// <summary>
/// Host or join a game and wait in the lobby. Owns the connection for the whole session and
/// switches to the round screens as the match progresses (via <c>showScreen</c>; null = this lobby).
/// </summary>
public partial class LobbyViewModel(Action<ViewModelBase?> showScreen) : ViewModelBase, IDisposable
{
    GameHost? _host;
    GameClient? _client;
    ViewModelBase? _screen;      // the round screen showing, if any
    RoundClock? _clock;
    string _submissions = "";
    int _totalRounds;

    [ObservableProperty] private string _playerName = Environment.UserName;
    [ObservableProperty] private string _joinAddress = "";

    // Match settings (host only). NumericUpDown binds to decimal?.
    [ObservableProperty] private decimal? _rounds = Protocol.DefaultRounds;
    [ObservableProperty] private decimal? _roundSeconds = Protocol.DefaultRoundSeconds;
    public int MinRounds => Protocol.MinRounds;
    public int MaxRounds => Protocol.MaxRounds;
    public int MinRoundSeconds => Protocol.MinRoundSeconds;
    public int MaxRoundSeconds => Protocol.MaxRoundSeconds;

    /// <summary>Where the game's map is centered, typed as "latitude, longitude" (host only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameLocation), nameof(LocationError), nameof(CanStartMatch))]
    private string _location = "";

    /// <summary>How far around <see cref="Location"/> the map is loaded, in meters. Set from the map picker's slider.</summary>
    [ObservableProperty] private double _radiusMeters = HostMapLoader.DefaultRadiusMeters;

    /// <summary>The parsed <see cref="Location"/>, or null if it's empty or invalid.</summary>
    public GeoPoint? GameLocation => GeoPoint.TryParse(Location, out var point) ? point : null;

    /// <summary>Shown under the location box while the text can't be parsed.</summary>
    public string? LocationError =>
        string.IsNullOrWhiteSpace(Location) || GameLocation is not null
            ? null
            : "Use latitude, longitude in degrees, e.g. 42.7284, -73.6918";
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsMorePlayers))]
    [NotifyPropertyChangedFor(nameof(ShowAddressesDirectly))]
    private bool _isHost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMatch))]
    private bool _matchStarted;

    [ObservableProperty] private string? _hostAddresses;
    /// <summary>Host only: which map this game is on, and a warning if it fell back to the test map.</summary>
    [ObservableProperty] private string? _mapInfo;
    [ObservableProperty] private string? _mapWarning;
    /// <summary>Short code for the host's address (e.g. "K3QZ-7TB"); null if it has no IPv4 address.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAddressesDirectly))]
    private string? _gameCode;

    /// <summary>Host without an IPv4 game code: show the IPs plainly instead of in the "Other ways to join" expander.</summary>
    public bool ShowAddressesDirectly => IsHost && GameCode is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInMenu))]
    private bool _isInLobby;

    public bool IsInMenu => !IsInLobby;

    /// <summary>Host's Start button: the host refuses to start with fewer than <see cref="Protocol.MinPlayers"/> players.</summary>
    public bool CanStartMatch =>
        !MatchStarted && !IsLoadingMap && LocationError is null && Players.Count >= Protocol.MinPlayers;

    /// <summary>Host: true while the map for the next match is being loaded (after clicking Start).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMatch), nameof(StartButtonText))]
    private bool _isLoadingMap;

    public string StartButtonText => IsLoadingMap ? $"Loading map... {LoadingSeconds}s" : "Start match";

    /// <summary>Seconds spent loading so far, so a slow download reads as progress rather than a hang.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    private int _loadingSeconds;
    public bool NeedsMorePlayers => IsHost && Players.Count < Protocol.MinPlayers;

    public ObservableCollection<string> Players { get; } = [];

    [RelayCommand]
    private async Task HostAsync()
    {
        if (!ValidateName()) return;
        IsBusy = true;
        Status = "Starting game...";
        try
        {
            _host = new GameHost();
            await ConnectAsync("127.0.0.1", _host.Port, _host.HostKey);
            IsHost = true;
            MapInfo = "The map is loaded around Location when you start the match (RPI campus if it's empty).";
            var addresses = NetInfo.LanAddresses();
            GameCode = addresses.Select(Network.GameCode.FromAddress).FirstOrDefault(code => code is not null);
            HostAddresses = addresses.Count == 0
                ? "No network connection found. Only players on this laptop can join."
                : (GameCode is null ? "Others join with IP: " : "Or join with IP: ") + string.Join("   or   ", addresses.Take(2));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Fail($"Port {Protocol.DefaultPort} is already in use. Is another game already hosting on this laptop?");
        }
        catch (NetworkException ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task JoinAsync()
    {
        if (!ValidateName()) return;
        if (string.IsNullOrWhiteSpace(JoinAddress))
        {
            Status = "Enter the host's game code (or IP address).";
            return;
        }

        // A game code is just the host's IP in disguise; anything else is treated as an IP/host name.
        var address = JoinAddress.Trim();
        if (Network.GameCode.TryParse(address, out var decoded, out var looksLikeCode)) address = decoded.ToString();
        else if (looksLikeCode)
        {
            Status = "That game code isn't valid. Check it for typos.";
            return;
        }

        IsBusy = true;
        Status = $"Connecting to {JoinAddress.Trim()}...";
        try
        {
            await ConnectAsync(address, Protocol.DefaultPort, hostKey: null);
        }
        catch (NetworkException ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartMatchAsync()
    {
        if (_client is null || _host is null) return;
        IsLoadingMap = true;
        LoadingSeconds = 0;
        var started = DateTime.Now;
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        ticker.Tick += (_, _) => LoadingSeconds = (int)(DateTime.Now - started).TotalSeconds;
        ticker.Start();
        try
        {
            // The map is built around the lobby's Location (or loaded from a saved copy), fresh for every match.
            var at = GameLocation ?? new GeoPoint(HostMapLoader.DefaultLatitude, HostMapLoader.DefaultLongitude);
            var result = await HostMapLoader.LoadAsync(at.Latitude, at.Longitude, RadiusMeters);
            if (_client is null || _host is null) return; // left the lobby while loading

            var map = result.Map;
            _host.UseMap(map);
            MapInfo = $"Map: {map.Name} ({map.Graph.VertexCount} intersections, {map.Graph.EdgeCount} streets) " +
                      $"around {at.ToCoordinateString()}{(result.FromCache ? " (saved copy)" : "")}";
            MapWarning = result.Warning;
            await _client.StartMatchAsync((int)(Rounds ?? Protocol.DefaultRounds), (int)(RoundSeconds ?? Protocol.DefaultRoundSeconds));
        }
        catch (Exception ex) { Status = $"Couldn't start the match: {ex.Message}"; }
        finally
        {
            ticker.Stop();
            IsLoadingMap = false;
        }
    }

    /// <summary>
    /// Opens the map so the host can pick the game's location: double-click a spot, then OK fills
    /// in Location and returns here. Back returns without changing it. Centers on the typed
    /// coordinates if they're valid, otherwise shows the whole world.
    /// </summary>
    [RelayCommand]
    private void PickLocationOnMap() =>
        showScreen(new MapViewModel(
            back: () => showScreen(null),
            start: GameLocation,
            startZoom: 15,
            radiusMeters: RadiusMeters,
            confirm: (point, radius) =>
            {
                Location = point.ToCoordinateString();
                RadiusMeters = radius;
                showScreen(null);
            }));

    [RelayCommand]
    private void Leave()
    {
        Cleanup();
        Status = null;
    }

    async Task ConnectAsync(string address, int port, string? hostKey)
    {
        var client = new GameClient();
        client.PlayersChanged += OnPlayersChanged;
        client.MatchStarted += OnMatchStarted;
        client.RoundStarted += OnRoundStarted;
        client.PlayerSubmitted += OnPlayerSubmitted;
        client.RoundResultsReady += OnRoundResults;
        client.MatchOver += OnMatchOver;
        client.Disconnected += OnDisconnected;
        _client = client;
        await client.ConnectAsync(address, port, PlayerName.Trim(), hostKey);
        IsInLobby = true;
        Status = null;
        OnPlayersChanged(client.Players);
    }

    void OnPlayersChanged(IReadOnlyList<PlayerInfo> players)
    {
        if (_client is null) return;
        Players.Clear();
        foreach (var p in players)
        {
            var tags = (p.IsHost ? " (host)" : "") + (p.Id == _client.PlayerId ? " (you)" : "");
            Players.Add(p.Name + tags);
        }
        OnPropertyChanged(nameof(CanStartMatch));
        OnPropertyChanged(nameof(NeedsMorePlayers));
    }

    // ---- match flow: Playing -> Waiting -> Results (timed) -> next round, or Summary -> lobby

    /// <summary>One per match, on this player's copy of the host's map. Reset each round with the round's start/end.</summary>
    GameService? _game;

    void OnMatchStarted(MatchStartMessage _)
    {
        if (_client?.Map is { } map) _game = new GameService(map);
    }

    void OnRoundStarted(RoundStartMessage start)
    {
        if (_client?.Map is not { } map) return;
        _game ??= new GameService(map);
        _game.SetStart(start.StartId); // also resets the player's route
        _game.SetEnd(start.EndId);

        MatchStarted = true;
        _totalRounds = start.TotalRounds;
        _clock = new RoundClock(TimeSpan.FromSeconds(start.DurationSeconds));
        _submissions = "";
        var clock = _clock;
        Show(new PlayingViewModel(_client, _game, start, clock, _submissions,
            done: timedOut => Show(new WaitingViewModel(clock, _submissions, timedOut)),
            hostWarning: IsHost ? MapWarning : null));
    }

    void OnPlayerSubmitted(PlayerSubmittedMessage submitted)
    {
        _submissions = $"{submitted.SubmittedCount} of {submitted.PlayerCount} players submitted";
        if (_screen is IShowsSubmissions screen) screen.Submissions = _submissions;
    }

    void OnRoundResults(RoundResultsMessage results)
    {
        Show(new ResultsViewModel(results, _client?.PlayerId, isLastRound: results.RoundNumber >= _totalRounds, map: _client?.Map));
    }

    void OnMatchOver(MatchOverMessage over)
    {
        MatchStarted = false; // host can start another match from the lobby
        _game = null;
        Show(new SummaryViewModel(over, _client?.PlayerId, backToLobby: () => Show(null)));
    }

    void Show(ViewModelBase? screen)
    {
        _screen = screen;
        showScreen(screen);
    }

    void OnDisconnected(string reason) => Fail(reason);

    bool ValidateName()
    {
        if (!string.IsNullOrWhiteSpace(PlayerName)) return true;
        Status = "Enter a name first.";
        return false;
    }

    void Fail(string message)
    {
        Cleanup();
        Status = message;
    }

    void Cleanup()
    {
        if (_client is not null)
        {
            _client.PlayersChanged -= OnPlayersChanged;
            _client.MatchStarted -= OnMatchStarted;
            _client.RoundStarted -= OnRoundStarted;
            _client.PlayerSubmitted -= OnPlayerSubmitted;
            _client.RoundResultsReady -= OnRoundResults;
            _client.MatchOver -= OnMatchOver;
            _client.Disconnected -= OnDisconnected;
            _client.Dispose();
            _client = null;
        }
        _game = null;
        _host?.Dispose();
        _host = null;
        Players.Clear();
        IsHost = false;
        IsInLobby = false;
        MatchStarted = false;
        HostAddresses = null;
        GameCode = null;
        MapInfo = null;
        MapWarning = null;
        IsLoadingMap = false;
        if (_screen is not null) Show(null); // back to the lobby screen
    }

    public void Dispose() => Cleanup();
}
