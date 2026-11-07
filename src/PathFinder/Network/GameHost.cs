using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PathFinder.Network;

/// <summary>
/// The referee. Runs on the hosting player's machine; every player (including the host's own
/// client, via localhost) connects to it the same way.
///
/// Threading: each connection has a background read loop, but all it does is post events to one
/// queue. A single loop (<see cref="RunAsync"/>) handles every event in order, so all state below
/// is only ever touched by one piece of code at a time and needs no locks. Game rules live in
/// <see cref="Match"/>; this class just moves messages in and out.
/// </summary>
public sealed class GameHost : IDisposable
{
    public static readonly TimeSpan DefaultRoundDuration = TimeSpan.FromSeconds(90);
    public const int TotalRounds = 3;

    static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Extra time after the deadline for submissions still in flight.</summary>
    static readonly TimeSpan SubmitGrace = TimeSpan.FromSeconds(1);
    static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(10);

    /// <summary>One connection. Info is null until the player is admitted to the lobby.</summary>
    sealed class Peer(Connection connection)
    {
        public Connection Connection { get; } = connection;
        public PlayerInfo? Info { get; set; }
        public long LastHeardMs { get; set; } = Environment.TickCount64;
    }

    // Everything that can happen, posted to the queue and handled one at a time.
    abstract record HostEvent;
    sealed record Connected(Peer Peer) : HostEvent;
    sealed record Received(Peer Peer, NetMessage Message) : HostEvent;
    sealed record Disconnected(Peer Peer) : HostEvent;
    sealed record RoundTimeUp(Match Match, int Round) : HostEvent;
    sealed record Tick : HostEvent;

    readonly IRoundRules _rules;
    readonly Channel<HostEvent> _events = Channel.CreateUnbounded<HostEvent>(new() { SingleReader = true });
    readonly CancellationTokenSource _cts = new();
    readonly TcpListener _listener;

    // Only touched by the event loop.
    readonly HashSet<Peer> _connections = [];
    readonly List<Peer> _players = [];   // admitted, in join order
    Match? _match;                       // null while in the lobby
    int _nextId;

    /// <summary>
    /// Starts hosting right away: listens on all interfaces (IPv4 and IPv6) and starts the loops.
    /// Throws SocketException (AddressAlreadyInUse) if the port is taken.
    /// </summary>
    public GameHost(int port = Protocol.DefaultPort, IRoundRules? rules = null, TimeSpan? roundDuration = null)
    {
        _rules = rules ?? new StubRoundRules();
        RoundDuration = roundDuration ?? DefaultRoundDuration;
        _listener = Listen(port);
        Port = port;

        // Last, so the loops only ever see a fully constructed host.
        _ = RunAsync(_cts.Token);
        _ = AcceptLoopAsync(_cts.Token);
        _ = TickLoopAsync(_cts.Token);
    }

    public TimeSpan RoundDuration { get; }

    /// <summary>Secret the host's own client sends in Join, so only it gets host rights.</summary>
    public string HostKey { get; } = Guid.NewGuid().ToString("N");

    public int Port { get; }

    /// <summary>Listens on IPv6 + IPv4 if possible, otherwise IPv4 only. Never leaves a socket open on failure.</summary>
    static TcpListener Listen(int port)
    {
        var dual = new TcpListener(IPAddress.IPv6Any, port);
        try
        {
            dual.Server.DualMode = true;
            dual.Start();
            return dual;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            dual.Stop();
            throw;
        }
        catch
        {
            dual.Stop(); // no IPv6 on this machine: fall back to IPv4
        }

        var ipv4 = new TcpListener(IPAddress.Any, port);
        try
        {
            ipv4.Start();
            return ipv4;
        }
        catch
        {
            ipv4.Stop();
            throw;
        }
    }

    void Post(HostEvent e) => _events.Writer.TryWrite(e);

    // ------------------------------------------------------------ background producers (post events only)

    async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var peer = new Peer(new Connection(await _listener.AcceptTcpClientAsync(ct)));
                Post(new Connected(peer));
                _ = ReadLoopAsync(peer, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    async Task ReadLoopAsync(Peer peer, CancellationToken ct)
    {
        try
        {
            using (var joinTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                joinTimeout.CancelAfter(JoinTimeout);
                if (await peer.Connection.ReceiveAsync(joinTimeout.Token) is not JoinMessage join) return;
                Post(new Received(peer, join));
            }
            while (await peer.Connection.ReceiveAsync(ct) is { } message)
                Post(new Received(peer, message));
        }
        catch { /* closed, timed out or broken: all mean the same thing */ }
        finally
        {
            Post(new Disconnected(peer));
        }
    }

    async Task RoundTimerAsync(Match match, int round, CancellationToken ct)
    {
        try { await Task.Delay(RoundDuration + SubmitGrace, ct); }
        catch (OperationCanceledException) { return; }
        Post(new RoundTimeUp(match, round));
    }

    async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try { while (await timer.WaitForNextTickAsync(ct)) Post(new Tick()); }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------ the event loop

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(ct))
            {
                try { await HandleAsync(e); }
                catch (Exception ex) { Debug.WriteLine($"[host] Error handling {e}: {ex}"); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var p in _connections) p.Connection.Dispose();
        }
    }

    Task HandleAsync(HostEvent e)
    {
        switch (e)
        {
            case Connected c:
                _connections.Add(c.Peer);
                return Task.CompletedTask;
            case Received r:
                r.Peer.LastHeardMs = Environment.TickCount64;
                return r.Peer.Info is null ? OnJoinAsync(r.Peer, r.Message) : OnMessageAsync(r.Peer, r.Peer.Info, r.Message);
            case Disconnected d:
                return OnDisconnectedAsync(d.Peer);
            case RoundTimeUp t when t.Match == _match: // ignore timers from an earlier match
                return ApplyAsync(_match.TimeUp(t.Round));
            case Tick:
                DropSilentPeers();
                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }

    async Task OnJoinAsync(Peer peer, NetMessage message)
    {
        if (message is not JoinMessage join) return;

        var rejection =
            join.ProtocolVersion != Protocol.Version ? "The host is running a different version of the game. Everyone needs to pull and rebuild." :
            _match is not null ? "That game has already started." :
            _players.Count >= Protocol.MaxPlayers ? $"That game is full ({Protocol.MaxPlayers} players)." :
            null;
        if (rejection is not null)
        {
            await SendAsync(peer, new RejectedMessage(rejection));
            peer.Connection.Dispose();
            return;
        }

        var id = $"p{++_nextId}";
        var name = PlayerNames.MakeUnique(PlayerNames.Clean(join.Name, $"Player {_nextId}"), _players.Select(p => p.Info!.Name));
        var isHost = join.HostKey == HostKey && !_players.Any(p => p.Info!.IsHost);
        peer.Info = new PlayerInfo(id, name, isHost);
        _players.Add(peer);

        Debug.WriteLine($"[host] {name} ({id}) joined from {peer.Connection.RemoteEndPoint}");
        await SendAsync(peer, new WelcomeMessage(id));
        await BroadcastLobbyAsync();
    }

    Task OnMessageAsync(Peer peer, PlayerInfo player, NetMessage message)
    {
        switch (message)
        {
            case PingMessage ping:
                return SendAsync(peer, new PongMessage(ping.Seq));
            case StartMatchMessage when player.IsHost && _match is null:
                _match = new Match(_rules, _players.Select(p => p.Info!), TotalRounds, (int)RoundDuration.TotalSeconds);
                return ApplyAsync(_match.Start());
            case SubmitPathMessage submit when _match is not null:
                return ApplyAsync(_match.Submit(player.Id, submit.RoundNumber, submit.NodeIds ?? []));
            case ReadyForNextMessage ready when _match is not null:
                return ApplyAsync(_match.Ready(player.Id, ready.RoundNumber));
            default:
                return Task.CompletedTask;
        }
    }

    async Task OnDisconnectedAsync(Peer peer)
    {
        _connections.Remove(peer);
        peer.Connection.Dispose();
        if (peer.Info is null || !_players.Remove(peer)) return;

        Debug.WriteLine($"[host] {peer.Info.Name} ({peer.Info.Id}) left");
        await BroadcastLobbyAsync();
        if (_match is not null) await ApplyAsync(_match.PlayerLeft(peer.Info.Id));
    }

    /// <summary>Sends what the match produced, starts round timers, and reopens the lobby when it's over.</summary>
    async Task ApplyAsync(IReadOnlyList<NetMessage> messages)
    {
        var match = _match;
        foreach (var message in messages)
        {
            if (message is RoundStartMessage start && match is not null)
                _ = RoundTimerAsync(match, start.RoundNumber, _cts.Token);
            await BroadcastAsync(message);
        }
        if (match?.IsOver == true)
        {
            Debug.WriteLine("[host] Match over, back to the lobby");
            _match = null;
        }
    }

    /// <summary>Drops players we haven't heard from (e.g. a laptop that silently left the Wi-Fi).</summary>
    void DropSilentPeers()
    {
        var now = Environment.TickCount64;
        foreach (var p in _connections)
            if (now - p.LastHeardMs > SilenceLimit.TotalMilliseconds)
                p.Connection.Dispose(); // its read loop ends and posts Disconnected
    }

    // ------------------------------------------------------------ sending

    Task BroadcastLobbyAsync() =>
        BroadcastAsync(new LobbyUpdateMessage(_players.Select(p => p.Info!).ToList()));

    Task BroadcastAsync(NetMessage message)
    {
        var line = Connection.Serialize(message);
        return Task.WhenAll(_players.Select(p => SendLineAsync(p, line)));
    }

    Task SendAsync(Peer peer, NetMessage message) => SendLineAsync(peer, Connection.Serialize(message));

    static async Task SendLineAsync(Peer peer, string line)
    {
        try { await peer.Connection.SendLineAsync(line); }
        catch { /* broken connection: its read loop will post Disconnected */ }
    }

    public void Dispose()
    {
        _cts.Cancel();       // stops all loops; the event loop closes every connection on its way out
        _listener.Stop();
    }
}
