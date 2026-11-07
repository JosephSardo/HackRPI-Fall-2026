using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PathFinder.Network;

/// <summary>
/// One player's connection to a <see cref="GameHost"/>. Events are raised on the thread that
/// created the client (the UI thread), so view models can handle them directly.
/// </summary>
public sealed class GameClient : IDisposable
{
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);
    const long SilenceLimitMs = 10_000;

    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    readonly CancellationTokenSource _cts = new();
    Connection? _connection;
    long _lastHeardMs = Environment.TickCount64;
    int _closed;

    public string? PlayerId { get; private set; }
    public IReadOnlyList<PlayerInfo> Players { get; private set; } = [];

    public event Action<IReadOnlyList<PlayerInfo>>? PlayersChanged;
    public event Action? MatchStarting;
    /// <summary>Connection ended unexpectedly. The argument is a reason that can be shown to the player.</summary>
    public event Action<string>? Disconnected;

    /// <summary>Connects and joins the lobby. Throws <see cref="NetworkException"/> with a player-friendly message.</summary>
    public async Task ConnectAsync(string address, int port, string name, string? hostKey = null)
    {
        var ip = await ResolveAsync(address);
        var tcp = new TcpClient(ip.AddressFamily);
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            await tcp.ConnectAsync(ip, port, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            throw new NetworkException(
                $"Couldn't reach {address}. Check the IP, make sure you're on the same Wi-Fi, " +
                "and that the host allowed the game through Windows Firewall.");
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new NetworkException(ex.SocketErrorCode == SocketError.ConnectionRefused
                ? $"Reached {address}, but no game is being hosted there."
                : $"Couldn't connect to {address} ({ex.SocketErrorCode}).", ex);
        }

        _connection = new Connection(tcp);
        try
        {
            await _connection.SendAsync(new JoinMessage(name, Protocol.Version, hostKey));
            await WaitForWelcomeAsync();
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            Dispose();
            throw new NetworkException("Connected, but the host didn't respond. Try again.", ex);
        }
        catch (NetworkException)
        {
            Dispose();
            throw;
        }

        _ = ReceiveLoopAsync(_cts.Token);
        _ = PingLoopAsync(_cts.Token);
    }

    public Task StartMatchAsync() => _connection?.SendAsync(new StartMatchMessage()) ?? Task.CompletedTask;

    static async Task<IPAddress> ResolveAsync(string address)
    {
        address = address.Trim();
        if (IPAddress.TryParse(address.Trim('[', ']'), out var ip)) return ip;
        try
        {
            var found = (await Dns.GetHostAddressesAsync(address)).FirstOrDefault();
            if (found is not null) return found;
        }
        catch (SocketException) { }
        throw new NetworkException($"\"{address}\" isn't a valid IP address.");
    }

    async Task WaitForWelcomeAsync()
    {
        using var timeout = new CancellationTokenSource(ConnectTimeout);
        while (true)
        {
            switch (await _connection!.ReceiveAsync(timeout.Token))
            {
                case WelcomeMessage welcome:
                    PlayerId = welcome.PlayerId;
                    return;
                case RejectedMessage rejected:
                    throw new NetworkException(rejected.Reason);
                case LobbyUpdateMessage update: // can arrive just before our Welcome
                    Players = update.Players;
                    break;
                case null:
                    throw new NetworkException("The host closed the connection.");
            }
        }
    }

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var reason = "The host closed the game.";
        try
        {
            if (Players.Count > 0) Raise(() => PlayersChanged?.Invoke(Players));
            while (await _connection!.ReceiveAsync(ct) is { } message)
            {
                Interlocked.Exchange(ref _lastHeardMs, Environment.TickCount64);
                switch (message)
                {
                    case LobbyUpdateMessage update:
                        Players = update.Players;
                        Raise(() => PlayersChanged?.Invoke(update.Players));
                        break;
                    case MatchStartingMessage:
                        Raise(() => MatchStarting?.Invoke());
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            reason = "Lost connection to the host.";
        }
        Close(reason);
    }

    async Task PingLoopAsync(CancellationToken ct)
    {
        long seq = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, ct);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastHeardMs) > SilenceLimitMs)
                {
                    Close("Lost connection to the host (no response for 10 seconds).");
                    return;
                }
                await _connection!.SendAsync(new PingMessage(++seq), ct);
            }
        }
        catch { /* the receive loop reports the disconnect */ }
    }

    /// <summary>Ends the connection and reports why, once. Not reported if the player chose to leave.</summary>
    void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _cts.Cancel();
        _connection?.Dispose();
        Raise(() => Disconnected?.Invoke(reason));
    }

    void Raise(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _cts.Cancel();
        _connection?.Dispose();
    }
}
