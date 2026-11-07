using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathFinder.Network;

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

    [ObservableProperty] private string _playerName = Environment.UserName;
    [ObservableProperty] private string _joinAddress = "";
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isHost;
    [ObservableProperty] private bool _matchStarted;
    [ObservableProperty] private string? _hostAddresses;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInMenu))]
    private bool _isInLobby;

    public bool IsInMenu => !IsInLobby;

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
            var addresses = NetInfo.LanAddresses();
            HostAddresses = addresses.Count == 0
                ? "No network connection found. Only players on this laptop can join."
                : "Others join with: " + string.Join("   or   ", addresses.Take(2));
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
            Status = "Enter the host's IP address.";
            return;
        }
        IsBusy = true;
        Status = $"Connecting to {JoinAddress.Trim()}...";
        try
        {
            await ConnectAsync(JoinAddress, Protocol.DefaultPort, hostKey: null);
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
        if (_client is null) return;
        try { await _client.StartMatchAsync(); }
        catch (Exception ex) { Status = $"Couldn't start the match: {ex.Message}"; }
    }

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
    }

    // ---- match flow: Playing -> Waiting -> Results -> (next round | Summary) -> lobby

    void OnRoundStarted(RoundStartMessage start)
    {
        if (_client is null) return;
        MatchStarted = true;
        _clock = new RoundClock(TimeSpan.FromSeconds(start.DurationSeconds));
        _submissions = "";
        var clock = _clock;
        Show(new PlayingViewModel(_client, start, clock, _submissions,
            done: timedOut => Show(new WaitingViewModel(clock, _submissions, timedOut))));
    }

    void OnPlayerSubmitted(PlayerSubmittedMessage submitted)
    {
        _submissions = $"{submitted.SubmittedCount} of {submitted.PlayerCount} players submitted";
        if (_screen is IShowsSubmissions screen) screen.Submissions = _submissions;
    }

    void OnRoundResults(RoundResultsMessage results)
    {
        if (_client is null) return;
        Show(new ResultsViewModel(_client, results, isLastRound: results.RoundNumber >= GameHost.TotalRounds));
    }

    void OnMatchOver(MatchOverMessage over)
    {
        MatchStarted = false; // host can start another match from the lobby
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
            _client.RoundStarted -= OnRoundStarted;
            _client.PlayerSubmitted -= OnPlayerSubmitted;
            _client.RoundResultsReady -= OnRoundResults;
            _client.MatchOver -= OnMatchOver;
            _client.Disconnected -= OnDisconnected;
            _client.Dispose();
            _client = null;
        }
        _host?.Dispose();
        _host = null;
        Players.Clear();
        IsHost = false;
        IsInLobby = false;
        MatchStarted = false;
        HostAddresses = null;
        if (_screen is not null) Show(null); // back to the lobby screen
    }

    public void Dispose() => Cleanup();
}
