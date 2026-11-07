using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PathFinder.Network;

/// <summary>
/// The referee. Runs on the hosting player's machine; every player (including the host's own
/// client, via localhost) connects to it the same way. Currently handles the lobby only.
/// </summary>
public sealed class GameHost : IDisposable
{
    sealed class Peer(string id, int order, string name, bool isHost, Connection connection)
    {
        public string Id { get; } = id;
        public int Order { get; } = order;
        public string Name { get; } = name;
        public bool IsHost { get; } = isHost;
        public Connection Connection { get; } = connection;
        public long LastSeenMs = Environment.TickCount64;
    }

    static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);
    const long SilenceLimitMs = 10_000;

    readonly ConcurrentDictionary<string, Peer> _peers = new();
    readonly CancellationTokenSource _cts = new();
    readonly object _gate = new();
    TcpListener? _listener;
    int _nextId;
    bool _matchStarted;

    /// <summary>Secret the host's own client sends in Join, so only it gets host rights.</summary>
    public string HostKey { get; } = Guid.NewGuid().ToString("N");

    public int Port { get; private set; }

    /// <summary>Starts listening on all interfaces. Throws SocketException (AddressAlreadyInUse) if the port is taken.</summary>
    public void Start(int port = Protocol.DefaultPort)
    {
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, port);
            _listener.Server.DualMode = true; // accept IPv4 and IPv6
            _listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode != SocketError.AddressAlreadyInUse)
        {
            _listener = new TcpListener(IPAddress.Any, port); // machine without IPv6
            _listener.Start();
        }
        Port = port;
        _ = AcceptLoopAsync(_cts.Token);
        _ = DropSilentPeersLoopAsync(_cts.Token);
    }

    async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandlePeerAsync(tcp, ct), ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    async Task HandlePeerAsync(TcpClient tcp, CancellationToken ct)
    {
        using var connection = new Connection(tcp);
        Peer? peer = null;
        try
        {
            using (var joinTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                joinTimeout.CancelAfter(JoinTimeout);
                if (await connection.ReceiveAsync(joinTimeout.Token) is not JoinMessage join) return;
                if (join.ProtocolVersion != Protocol.Version)
                {
                    await connection.SendAsync(new RejectedMessage(
                        "The host is running a different version of the game. Everyone needs to pull and rebuild."), ct);
                    return;
                }
                if (TryAdmit(join, connection, out peer) is { } rejection)
                {
                    await connection.SendAsync(new RejectedMessage(rejection), ct);
                    return;
                }
            }

            Debug.WriteLine($"[host] {peer!.Name} ({peer.Id}) joined from {connection.RemoteEndPoint}");
            await connection.SendAsync(new WelcomeMessage(peer.Id), ct);
            await BroadcastLobbyAsync();

            while (await connection.ReceiveAsync(ct) is { } message)
            {
                Interlocked.Exchange(ref peer.LastSeenMs, Environment.TickCount64);
                switch (message)
                {
                    case PingMessage ping:
                        await connection.SendAsync(new PongMessage(ping.Seq), ct);
                        break;
                    case StartMatchMessage when peer.IsHost:
                        bool firstStart;
                        lock (_gate)
                        {
                            firstStart = !_matchStarted;
                            _matchStarted = true;
                        }
                        if (firstStart) await BroadcastAsync(new MatchStartingMessage());
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        finally
        {
            if (peer is not null && _peers.TryRemove(peer.Id, out _))
            {
                Debug.WriteLine($"[host] {peer.Name} ({peer.Id}) left");
                if (!ct.IsCancellationRequested) _ = BroadcastLobbyAsync();
            }
        }
    }

    /// <summary>Adds the player to the lobby, or returns why they can't join.</summary>
    string? TryAdmit(JoinMessage join, Connection connection, out Peer? peer)
    {
        peer = null;
        lock (_gate)
        {
            if (_matchStarted) return "That game has already started.";
            if (_peers.Count >= Protocol.MaxPlayers) return $"That game is full ({Protocol.MaxPlayers} players).";

            var isHost = join.HostKey == HostKey && !_peers.Values.Any(p => p.IsHost);
            var order = ++_nextId;
            peer = new Peer($"p{order}", order, UniqueName(CleanName(join.Name, $"Player {order}")), isHost, connection);
            _peers[peer.Id] = peer;
            return null;
        }
    }

    string UniqueName(string name)
    {
        var taken = _peers.Values.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }

    static string CleanName(string? name, string fallback)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > 20) cleaned = cleaned[..20].Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    Task BroadcastLobbyAsync()
    {
        var players = _peers.Values.OrderBy(p => p.Order).Select(p => new PlayerInfo(p.Id, p.Name, p.IsHost)).ToList();
        return BroadcastAsync(new LobbyUpdateMessage(players));
    }

    Task BroadcastAsync(NetMessage message)
    {
        var line = Connection.Serialize(message);
        return Task.WhenAll(_peers.Values.Select(async p =>
        {
            try { await p.Connection.SendLineAsync(line); }
            catch { /* that peer's read loop notices and removes it */ }
        }));
    }

    /// <summary>Drops players we haven't heard from (e.g. a laptop that silently left the Wi-Fi).</summary>
    async Task DropSilentPeersLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                var now = Environment.TickCount64;
                foreach (var p in _peers.Values)
                    if (now - Interlocked.Read(ref p.LastSeenMs) > SilenceLimitMs)
                        p.Connection.Dispose(); // unblocks its read loop, which removes it
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        foreach (var p in _peers.Values) p.Connection.Dispose();
        _peers.Clear();
    }
}
