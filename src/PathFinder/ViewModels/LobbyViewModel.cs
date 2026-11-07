using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathFinder.Network;

namespace PathFinder.ViewModels;

/// <summary>Host or join a game, then wait in the lobby until the host starts the match.</summary>
public partial class LobbyViewModel : ViewModelBase, IDisposable
{
    GameHost? _host;
    GameClient? _client;
    int _port = Protocol.DefaultPort;

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
            _host.Start(_port);
            await ConnectAsync("127.0.0.1", _host.Port, _host.HostKey);
            IsHost = true;
            var addresses = NetInfo.LanAddresses();
            HostAddresses = addresses.Count == 0
                ? "No network connection found. Only players on this laptop can join."
                : "Others join with: " + string.Join("   or   ", addresses.Take(2));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Fail($"Port {_port} is already in use. Is another game (or NetCheck) already hosting on this laptop?");
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
            await ConnectAsync(JoinAddress, _port, hostKey: null);
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
        client.MatchStarting += OnMatchStarting;
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

    void OnMatchStarting()
    {
        MatchStarted = true;
        Status = "Match starting! (Rounds aren't implemented yet.)";
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
            _client.MatchStarting -= OnMatchStarting;
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
    }

    /// <summary>
    /// Shortcuts for testing several windows on one laptop:
    /// <c>--name Bob</c>, <c>--host</c>, or <c>--join 127.0.0.1</c>, plus <c>--port 5060</c>.
    /// </summary>
    public void ApplyCommandLine(string[] args)
    {
        string? Value(string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Value("--name") is { } name) PlayerName = name;
        if (int.TryParse(Value("--port"), out var port)) _port = port;
        if (args.Contains("--host"))
            Dispatcher.UIThread.Post(() => HostCommand.Execute(null));
        else if (Value("--join") is { } address)
        {
            JoinAddress = address;
            Dispatcher.UIThread.Post(() => JoinCommand.Execute(null));
        }
    }

    public void Dispose() => Cleanup();
}
