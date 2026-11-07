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
/// client, via localhost) connects to it the same way. Runs the lobby and the rounds of a match.
/// </summary>
public sealed class GameHost : IDisposable
{
    public static readonly TimeSpan DefaultRoundDuration = TimeSpan.FromSeconds(90);
    public const int TotalRounds = 3;

    sealed class Peer(string id, int order, string name, bool isHost, Connection connection)
    {
        public string Id { get; } = id;
        public int Order { get; } = order;
        public string Name { get; } = name;
        public bool IsHost { get; } = isHost;
        public Connection Connection { get; } = connection;
        public long LastSeenMs = Environment.TickCount64;
    }

    enum Phase { Lobby, Playing, Results }

    static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Extra time after the deadline for submissions still in flight.</summary>
    static readonly TimeSpan SubmitGrace = TimeSpan.FromSeconds(1);
    const long SilenceLimitMs = 10_000;

    readonly IRoundRules _rules;
    readonly ConcurrentDictionary<string, Peer> _peers = new();
    readonly CancellationTokenSource _cts = new();
    readonly object _gate = new(); // guards joining (_peers additions, _nextId, _matchStarted)
    TcpListener? _listener;
    int _nextId;
    bool _matchStarted;

    // Match state. Only touched inside RunLogicAsync, which runs one change at a time,
    // so the messages each change sends always go out in order.
    readonly SemaphoreSlim _logic = new(1, 1);
    Phase _phase = Phase.Lobby;
    int _round, _startId, _endId, _matchPlayerCount;
    readonly Dictionary<string, string> _roundPlayers = [];      // id -> name, everyone in this round
    readonly Dictionary<string, List<int>> _submissions = [];    // id -> path
    readonly HashSet<string> _ready = [];
    readonly Dictionary<string, (string Name, double Total)> _totals = [];
    CancellationTokenSource? _roundTimer;

    public GameHost(IRoundRules? rules = null, TimeSpan? roundDuration = null)
    {
        _rules = rules ?? new StubRoundRules();
        RoundDuration = roundDuration ?? DefaultRoundDuration;
    }

    public TimeSpan RoundDuration { get; }

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

    // ------------------------------------------------------------ connections and lobby

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
                        await RunLogicAsync(StartMatchAsync);
                        break;
                    case SubmitPathMessage submit:
                        await RunLogicAsync(() => OnSubmitAsync(peer, submit));
                        break;
                    case ReadyForNextMessage ready:
                        await RunLogicAsync(() => OnReadyAsync(peer, ready));
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
                if (!ct.IsCancellationRequested)
                {
                    _ = BroadcastLobbyAsync();
                    _ = RunLogicAsync(OnPeerLeftAsync);
                }
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

    IEnumerable<Peer> ConnectedPlayers() => _peers.Values.OrderBy(p => p.Order);

    Task BroadcastLobbyAsync() =>
        BroadcastAsync(new LobbyUpdateMessage(ConnectedPlayers().Select(p => new PlayerInfo(p.Id, p.Name, p.IsHost)).ToList()));

    Task BroadcastAsync(NetMessage message)
    {
        var line = Connection.Serialize(message);
        return Task.WhenAll(_peers.Values.Select(async p =>
        {
            try { await p.Connection.SendLineAsync(line); }
            catch { /* that peer's read loop notices and removes it */ }
        }));
    }

    // ------------------------------------------------------------ match and rounds

    /// <summary>Runs one match-state change at a time. Quietly does nothing once the host is shut down.</summary>
    async Task RunLogicAsync(Func<Task> change)
    {
        try { await _logic.WaitAsync(_cts.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
        try { await change(); }
        catch (Exception ex) { Debug.WriteLine($"[host] Match logic error: {ex}"); }
        finally { _logic.Release(); }
    }

    async Task StartMatchAsync()
    {
        if (_phase != Phase.Lobby || _peers.IsEmpty) return;
        lock (_gate) _matchStarted = true; // lobby closes: no joins mid-match
        _totals.Clear();
        foreach (var p in ConnectedPlayers()) _totals[p.Id] = (p.Name, 0);
        _matchPlayerCount = _totals.Count;
        await StartRoundAsync(1);
    }

    async Task StartRoundAsync(int round)
    {
        _round = round;
        (_startId, _endId) = _rules.PickStartAndEnd(round);
        _submissions.Clear();
        _ready.Clear();
        _roundPlayers.Clear();
        foreach (var p in ConnectedPlayers()) _roundPlayers[p.Id] = p.Name;
        _phase = Phase.Playing;

        _roundTimer?.Cancel();
        _roundTimer = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = RoundTimerAsync(round, _roundTimer.Token);

        Debug.WriteLine($"[host] Round {round}/{TotalRounds}: {_startId} -> {_endId}");
        await BroadcastAsync(new RoundStartMessage(round, TotalRounds, _startId, _endId, (int)RoundDuration.TotalSeconds));
    }

    async Task RoundTimerAsync(int round, CancellationToken ct)
    {
        try { await Task.Delay(RoundDuration + SubmitGrace, ct); }
        catch (OperationCanceledException) { return; } // round ended early, or host shut down
        await RunLogicAsync(() => EndRoundAsync(round));
    }

    async Task OnSubmitAsync(Peer peer, SubmitPathMessage submit)
    {
        if (_phase != Phase.Playing || submit.RoundNumber != _round) return;           // too late, or an old round
        if (!_roundPlayers.ContainsKey(peer.Id) || _submissions.ContainsKey(peer.Id)) return; // once per round
        _submissions[peer.Id] = submit.NodeIds ?? [];

        var playerCount = _roundPlayers.Keys.Count(id => _peers.ContainsKey(id) || _submissions.ContainsKey(id));
        await BroadcastAsync(new PlayerSubmittedMessage(_round, peer.Id, _submissions.Count, playerCount));
        if (EveryoneConnectedSubmitted()) await EndRoundAsync(_round);
    }

    bool EveryoneConnectedSubmitted() =>
        _roundPlayers.Keys.Where(_peers.ContainsKey).All(_submissions.ContainsKey);

    async Task EndRoundAsync(int round)
    {
        if (_phase != Phase.Playing || round != _round) return; // already ended
        _roundTimer?.Cancel();

        // Everyone in the round who is still here, plus anyone who submitted and then left.
        var outcomes = _roundPlayers
            .Where(p => _peers.ContainsKey(p.Key) || _submissions.ContainsKey(p.Key))
            .Select(p =>
            {
                var submitted = _submissions.TryGetValue(p.Key, out var path);
                var score = submitted ? _rules.Score(path!, _startId, _endId) : 0;
                return (Id: p.Key, Name: p.Value, Submitted: submitted, Path: path ?? [], Score: score);
            })
            .OrderByDescending(o => o.Score)
            .ToList();

        var ranks = Ranks(outcomes.Select(o => o.Score).ToList());
        var results = outcomes.Select((o, i) => new RoundResult(o.Id, o.Name, o.Submitted, o.Path, o.Score, ranks[i])).ToList();
        foreach (var o in outcomes)
            if (_totals.TryGetValue(o.Id, out var t)) _totals[o.Id] = (t.Name, t.Total + o.Score);

        var (optimalPath, optimalLength) = _rules.OptimalRoute(_startId, _endId);
        _phase = Phase.Results;
        await BroadcastAsync(new RoundResultsMessage(round, results, optimalPath.ToList(), optimalLength));
    }

    async Task OnReadyAsync(Peer peer, ReadyForNextMessage ready)
    {
        if (_phase != Phase.Results || ready.RoundNumber != _round) return;
        _ready.Add(peer.Id);
        await AdvanceIfEveryoneReadyAsync();
    }

    async Task AdvanceIfEveryoneReadyAsync()
    {
        if (!_peers.Keys.All(_ready.Contains)) return;
        if (_round < TotalRounds) await StartRoundAsync(_round + 1);
        else await EndMatchAsync();
    }

    async Task EndMatchAsync()
    {
        _roundTimer?.Cancel();
        var sorted = _totals.OrderByDescending(t => t.Value.Total).ToList();
        var ranks = Ranks(sorted.Select(t => t.Value.Total).ToList());
        var standings = sorted.Select((t, i) => new PlayerTotal(t.Key, t.Value.Name, t.Value.Total, ranks[i])).ToList();

        // Back to the lobby: the host can start another match and new players can join.
        _phase = Phase.Lobby;
        lock (_gate) _matchStarted = false;
        Debug.WriteLine("[host] Match over");
        await BroadcastAsync(new MatchOverMessage(standings));
    }

    /// <summary>Someone disconnected mid-match: keep going without them, or stop if they were the last opponent.</summary>
    async Task OnPeerLeftAsync()
    {
        if (_phase == Phase.Lobby) return;
        if (_matchPlayerCount >= 2 && _peers.Count < 2)
        {
            await EndMatchAsync();
            return;
        }
        if (_phase == Phase.Playing && EveryoneConnectedSubmitted()) await EndRoundAsync(_round);
        else if (_phase == Phase.Results) await AdvanceIfEveryoneReadyAsync();
    }

    /// <summary>Ranks for scores sorted best first. Equal scores share a place: 100, 80, 80, 50 -> 1, 2, 2, 4.</summary>
    static List<int> Ranks(IReadOnlyList<double> sortedScores)
    {
        var ranks = new List<int>(sortedScores.Count);
        for (var i = 0; i < sortedScores.Count; i++)
            ranks.Add(i > 0 && sortedScores[i] == sortedScores[i - 1] ? ranks[i - 1] : i + 1);
        return ranks;
    }

    // ------------------------------------------------------------ housekeeping

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
        _roundTimer?.Cancel();
        _listener?.Stop();
        foreach (var p in _peers.Values) p.Connection.Dispose();
        _peers.Clear();
    }
}
