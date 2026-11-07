#:property PublishAot=false
#:property Nullable=enable

// NetCheck: tests whether laptops can reach each other on the current network.
// Uses the same wire format as the game will: one JSON object per line over TCP,
// each with a "type" field. Hosts also announce themselves over UDP broadcast.
//
// Run with the Windows `dotnet` (not from WSL), from the repo root:
//   dotnet run tools/NetCheck.cs -- host        [--port 5050] [--name Host]
//   dotnet run tools/NetCheck.cs -- join <ip>   [--port 5050] [--name Ryan]
//   dotnet run tools/NetCheck.cs -- discover    [--seconds 5]

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length == 0) { App.PrintUsage(); return 1; }

Console.CancelKeyPress += (_, e) => { e.Cancel = true; App.Cts.Cancel(); };
var ct = App.Cts.Token;

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "host":
            return await Host.RunAsync(Opt.Int(args, "--port", App.DefaultPort),
                                       Opt.Str(args, "--name", Environment.MachineName), ct);
        case "join" when args.Length >= 2 && !args[1].StartsWith("--"):
            return await Client.RunAsync(args[1], Opt.Int(args, "--port", App.DefaultPort),
                                         Opt.Str(args, "--name", Environment.UserName), ct);
        case "discover":
            return await Discovery.RunAsync(TimeSpan.FromSeconds(Opt.Int(args, "--seconds", 5)), ct);
        default:
            App.PrintUsage();
            return 1;
    }
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    return 0;
}

static class App
{
    public const int DefaultPort = 5050;
    public const int DiscoveryPort = 5051;
    public const string AppId = "NetCheck";
    public static readonly CancellationTokenSource Cts = new();
    public static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void PrintUsage() => Console.WriteLine("""
        NetCheck: can our laptops reach each other on this network?

          dotnet run tools/NetCheck.cs -- host        [--port 5050] [--name Host]
          dotnet run tools/NetCheck.cs -- join <ip>   [--port 5050] [--name Ryan]
          dotnet run tools/NetCheck.cs -- discover    [--seconds 5]

        1. One laptop runs `host` and reads out an IP it prints.
        2. Everyone else runs `join <that ip>`.
        3. Optionally run `discover` to see if automatic host discovery works.
        Type a line and press Enter to chat. /quit (or Ctrl+C) exits.
        """);
}

// ---------------------------------------------------------------- host

static class Host
{
    sealed class Player(string id, string name, Wire wire)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public Wire Wire { get; } = wire;
        public long LastSeenMs = App.Clock.ElapsedMilliseconds;
        public volatile bool TimedOut;
    }

    static readonly ConcurrentDictionary<string, Player> Players = new();
    static int _nextId;
    static string _hostName = "";

    public static async Task<int> RunAsync(int port, string name, CancellationToken ct)
    {
        _hostName = Text.Clean(name, "Host");

        TcpListener listener;
        try
        {
            try { listener = Listen(IPAddress.IPv6Any, port, dualMode: true); }
            catch (SocketException ex) when (ex.SocketErrorCode != SocketError.AddressAlreadyInUse)
            {
                listener = Listen(IPAddress.Any, port, dualMode: false); // no IPv6 on this machine
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Log.Error($"Port {port} is already in use. Is another host running? Try --port {port + 10}.");
            return 1;
        }

        var dual = listener.LocalEndpoint.AddressFamily == AddressFamily.InterNetworkV6;
        Log.Good($"Hosting \"{_hostName}\" on TCP port {port} ({(dual ? "IPv4 + IPv6" : "IPv4 only")}).");
        Console.WriteLine();
        Console.WriteLine("Other laptops should run ONE of these (Wi-Fi addresses are usually the right ones):");
        var portArg = port == App.DefaultPort ? "" : $" --port {port}";
        foreach (var a in NetInfo.LocalAddresses())
        {
            if (!dual && a.Address.AddressFamily == AddressFamily.InterNetworkV6) continue;
            Console.WriteLine($"  dotnet run tools/NetCheck.cs -- join {a.Address}{portArg}    ({a.Describe()})");
        }
        Console.WriteLine($"  Same laptop: dotnet run tools/NetCheck.cs -- join 127.0.0.1{portArg}");
        Console.WriteLine();
        Console.WriteLine("If Windows Firewall asks, click Allow (tick both Private and Public).");
        Console.WriteLine("Type a message + Enter to send it to everyone. /list shows players, /quit exits.");
        Console.WriteLine();

        _ = BeaconLoopAsync(port, ct);
        _ = ReaperLoopAsync(ct);
        ConsoleInput.Start(HandleCommandAsync);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(tcp, ct));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            foreach (var p in Players.Values) p.Wire.Dispose();
        }
        Log.Info("Host stopped.");
        return 0;
    }

    static TcpListener Listen(IPAddress address, int port, bool dualMode)
    {
        var l = new TcpListener(address, port);
        if (dualMode) l.Server.DualMode = true;
        l.Start();
        return l;
    }

    static async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        using var wire = new Wire(tcp);
        var remote = wire.RemoteText;
        Player? player = null;
        var reason = "closed the connection";
        try
        {
            using var joinTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            joinTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var first = await wire.ReceiveAsync(joinTimeout.Token);
            if (first is null || Json.Type(first) != "Join")
            {
                reason = "didn't send a Join message";
                return;
            }

            var id = $"p{Interlocked.Increment(ref _nextId)}";
            player = new Player(id, Text.Clean(Json.Str(first, "name"), id), wire);
            Players[id] = player;
            await wire.SendAsync(new JsonObject { ["type"] = "Welcome", ["playerId"] = id, ["hostName"] = _hostName }, ct);
            Log.Good($"{player.Name} ({id}) joined from {remote}. {Players.Count} player(s) connected.");
            await BroadcastLobbyAsync();

            while (true)
            {
                var msg = await wire.ReceiveAsync(ct);
                if (msg is null) break;
                player.LastSeenMs = App.Clock.ElapsedMilliseconds;
                switch (Json.Type(msg))
                {
                    case "Ping":
                        await wire.SendAsync(new JsonObject
                        {
                            ["type"] = "Pong",
                            ["seq"] = msg["seq"]?.DeepClone(),
                            ["sentAt"] = msg["sentAt"]?.DeepClone(),
                        }, ct);
                        break;
                    case "Chat":
                        var text = Json.Str(msg, "text") ?? "";
                        Log.Chat(player.Name, text);
                        await BroadcastAsync(Chat(player.Name, text), exceptId: id);
                        break;
                    default:
                        Log.Warn($"Unknown message type '{Json.Type(msg)}' from {player.Name}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { reason = "didn't send Join within 10 s"; }
        catch (OperationCanceledException) { reason = "host shutting down"; }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            reason = player?.TimedOut == true ? "no pings for 10 s (timed out)" : "connection lost";
        }
        finally
        {
            if (player is not null && Players.TryRemove(player.Id, out _))
            {
                Log.Warn($"{player.Name} ({player.Id}) left: {reason}. {Players.Count} player(s) connected.");
                _ = BroadcastLobbyAsync();
            }
            else if (player is null && !ct.IsCancellationRequested)
            {
                Log.Warn($"Dropped connection from {remote}: {reason}.");
            }
        }
    }

    static async Task HandleCommandAsync(string line)
    {
        if (line == "/list")
        {
            if (Players.IsEmpty) { Log.Info("No players connected."); return; }
            foreach (var p in Players.Values.OrderBy(p => p.Id))
                Log.Info($"  {p.Id}  {p.Name,-20} {p.Wire.RemoteText}");
            return;
        }
        if (line.StartsWith('/')) { Log.Warn("Commands: /list, /quit"); return; }
        var sent = await BroadcastAsync(Chat(_hostName + " (host)", line));
        Log.Info($"Sent to {sent} player(s).");
    }

    static JsonObject Chat(string from, string text) =>
        new() { ["type"] = "Chat", ["from"] = from, ["text"] = text };

    static Task BroadcastLobbyAsync()
    {
        var players = new JsonArray();
        foreach (var p in Players.Values.OrderBy(p => p.Id))
            players.Add(new JsonObject { ["id"] = p.Id, ["name"] = p.Name });
        return BroadcastAsync(new JsonObject { ["type"] = "Lobby", ["host"] = _hostName, ["players"] = players });
    }

    /// <summary>Sends to every player; returns how many sends succeeded.</summary>
    static async Task<int> BroadcastAsync(JsonObject msg, string? exceptId = null)
    {
        var line = msg.ToJsonString();
        var results = await Task.WhenAll(Players.Values.Where(p => p.Id != exceptId).Select(async p =>
        {
            try { await p.Wire.SendLineAsync(line); return true; }
            catch { return false; } // that player's read loop will clean up
        }));
        return results.Count(ok => ok);
    }

    /// <summary>Drops players we haven't heard from in 10 s (a laptop that silently left the Wi-Fi).</summary>
    static async Task ReaperLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);
            var now = App.Clock.ElapsedMilliseconds;
            foreach (var p in Players.Values)
            {
                if (now - Interlocked.Read(ref p.LastSeenMs) <= 10_000) continue;
                p.TimedOut = true;
                p.Wire.Dispose(); // unblocks its read loop, which removes it
            }
        }
    }

    /// <summary>Announces this host over UDP broadcast every second, for `discover`.</summary>
    static async Task BeaconLoopAsync(int port, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var payload = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = "Beacon", ["app"] = App.AppId, ["name"] = _hostName, ["port"] = port,
        }.ToJsonString());
        var warned = false;
        while (!ct.IsCancellationRequested)
        {
            var targets = NetInfo.BroadcastAddresses().Append(IPAddress.Broadcast).Distinct();
            foreach (var target in targets)
            {
                try { await udp.SendAsync(payload, new IPEndPoint(target, App.DiscoveryPort), ct); }
                catch (SocketException ex)
                {
                    if (!warned) Log.Warn($"UDP broadcast failed ({ex.SocketErrorCode}); `discover` won't find this host, use the IP instead.");
                    warned = true;
                }
            }
            await Task.Delay(1000, ct);
        }
    }
}

// ---------------------------------------------------------------- client

static class Client
{
    public static async Task<int> RunAsync(string target, int port, string name, CancellationToken ct)
    {
        IPAddress? address;
        if (!IPAddress.TryParse(target.Trim('[', ']'), out address))
        {
            try { address = (await Dns.GetHostAddressesAsync(target, ct)).FirstOrDefault(); }
            catch (SocketException) { address = null; }
            if (address is null)
            {
                Log.Error($"Couldn't resolve '{target}'. Use one of the IP addresses the host printed.");
                return 1;
            }
        }

        Log.Info($"Connecting to {address} port {port} (5 s timeout)...");
        var tcp = new TcpClient(address.AddressFamily);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await tcp.ConnectAsync(address, port, connectTimeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();
                Diagnose.Timeout(address, port);
                return 2;
            }
            catch (SocketException ex)
            {
                tcp.Dispose();
                Diagnose.Failure(ex, address, port);
                return 2;
            }
        }

        using var wire = new Wire(tcp);
        var local = (IPEndPoint)tcp.Client.LocalEndPoint!;
        var localInfo = NetInfo.LocalAddresses().FirstOrDefault(a => a.Address.Equals(local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address));
        Log.Good($"TCP connected: {wire.LocalText} -> {wire.RemoteText}" + (localInfo is null ? "" : $"  (your side: {localInfo.Describe()})"));

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stats = new PingStats();
        string reason;
        try
        {
            await wire.SendAsync(new JsonObject { ["type"] = "Join", ["app"] = App.AppId, ["version"] = 1, ["name"] = name }, ct);
            ConsoleInput.Start(line => line.StartsWith('/')
                ? Task.Run(() => Log.Warn("Commands: /quit"))
                : wire.SendAsync(new JsonObject { ["type"] = "Chat", ["text"] = line }));
            _ = PingLoopAsync(wire, stats, session.Token);

            while (true)
            {
                var msg = await wire.ReceiveAsync(ct);
                if (msg is null) { reason = "The host closed the connection."; break; }
                switch (Json.Type(msg))
                {
                    case "Welcome":
                        Log.Good($"Joined \"{Json.Str(msg, "hostName")}\" as {Json.Str(msg, "playerId")}. Game messages work both ways.");
                        break;
                    case "Lobby":
                        var names = (msg["players"] as JsonArray ?? new JsonArray())
                            .OfType<JsonObject>().Select(p => Json.Str(p, "name") ?? "?").ToList();
                        Log.Info($"Players ({names.Count}): {string.Join(", ", names)}");
                        break;
                    case "Pong":
                        stats.OnPong(Json.Num(msg, "seq"), Json.Num(msg, "sentAt"));
                        break;
                    case "Chat":
                        Log.Chat(Json.Str(msg, "from") ?? "?", Json.Str(msg, "text") ?? "");
                        break;
                    default:
                        Log.Warn($"Unknown message type '{Json.Type(msg)}'");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stats.PrintSummary();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            reason = $"Connection lost ({ex.InnerException?.Message ?? ex.Message}).";
        }
        finally
        {
            session.Cancel();
        }

        stats.PrintSummary();
        Log.Error(reason);
        return 3;
    }

    static async Task PingLoopAsync(Wire wire, PingStats stats, CancellationToken ct)
    {
        long seq = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                seq++;
                stats.OnSent();
                await wire.SendAsync(new JsonObject
                {
                    ["type"] = "Ping", ["seq"] = seq, ["sentAt"] = App.Clock.Elapsed.TotalMilliseconds,
                }, ct);
                stats.WarnIfSilent();
                await Task.Delay(1000, ct);
            }
        }
        catch { /* connection closed; the read loop reports why */ }
    }
}

sealed class PingStats
{
    readonly object _lock = new();
    long _sent, _received;
    double _min = double.MaxValue, _max, _total;
    double _lastReplyAt = App.Clock.Elapsed.TotalMilliseconds;
    bool _silentWarned;

    public void OnSent() { lock (_lock) _sent++; }

    public void OnPong(double? seq, double? sentAt)
    {
        if (sentAt is null) return;
        var now = App.Clock.Elapsed.TotalMilliseconds;
        var rtt = now - sentAt.Value;
        lock (_lock)
        {
            _received++;
            _total += rtt;
            _min = Math.Min(_min, rtt);
            _max = Math.Max(_max, rtt);
            _lastReplyAt = now;
            if (_silentWarned) { Log.Good("Host is replying again."); _silentWarned = false; }
            // Print the first few, then every 10th, plus anything slow.
            if (_received <= 5 || _received % 10 == 0 || rtt > 200)
                Log.Info($"Ping #{seq}: {rtt:0.0} ms   (min {_min:0.0} / avg {_total / _received:0.0} / max {_max:0.0}, {_received}/{_sent} replies)");
        }
    }

    public void WarnIfSilent()
    {
        lock (_lock)
        {
            var silentFor = (App.Clock.Elapsed.TotalMilliseconds - _lastReplyAt) / 1000;
            if (silentFor < 3 || _silentWarned) return;
            _silentWarned = true;
            Log.Warn($"No reply from the host for {silentFor:0} s. The connection may be dropping.");
        }
    }

    public void PrintSummary()
    {
        lock (_lock)
        {
            if (_received == 0) { Log.Info($"Pings: 0/{_sent} replies."); return; }
            Log.Info($"Pings: {_received}/{_sent} replies, min {_min:0.0} / avg {_total / _received:0.0} / max {_max:0.0} ms.");
        }
    }
}

static class Diagnose
{
    public static void Timeout(IPAddress address, int port)
    {
        Log.Error($"Couldn't connect to {address} port {port} within 5 s (no response at all). Likely causes:");
        Console.WriteLine("""
              1. This network blocks laptops from connecting to each other (common on campus Wi-Fi).
              2. Windows Firewall on the HOST blocked it. On the host: Windows Security > Firewall >
                 Allow an app, and allow "dotnet" / "NetCheck" on both Private and Public networks.
              3. You're on different networks (e.g. eduroam vs RPI_WPA2). Check both laptops' Wi-Fi name.
              4. Wrong address. Use one the host printed, ideally its Wi-Fi one.
            Next step: put both laptops on a hotspot and retry. If that works, the campus Wi-Fi is the problem.
            """);
        PrintOwnAddresses();
    }

    public static void Failure(SocketException ex, IPAddress address, int port)
    {
        switch (ex.SocketErrorCode)
        {
            case SocketError.ConnectionRefused:
                Log.Error($"Reached {address}, but nothing is listening on port {port}.");
                Console.WriteLine("  Is `host` running on that laptop, with the same --port? Is the IP right?");
                break;
            case SocketError.HostUnreachable or SocketError.NetworkUnreachable:
                Log.Error($"No route to {address} ({ex.SocketErrorCode}).");
                Console.WriteLine("  You're probably on different networks/subnets, or the IP has a typo.");
                PrintOwnAddresses();
                break;
            case SocketError.TimedOut:
                Timeout(address, port);
                break;
            default:
                Log.Error($"Couldn't connect to {address} port {port}: {ex.SocketErrorCode} ({ex.Message}).");
                break;
        }
    }

    static void PrintOwnAddresses()
    {
        Console.WriteLine("  This laptop's addresses (compare with the host's; IPv4 hosts in the same subnet share the masked prefix):");
        foreach (var a in NetInfo.LocalAddresses())
            Console.WriteLine($"    {a.Address}  ({a.Describe()})");
    }
}

// ---------------------------------------------------------------- discovery

static class Discovery
{
    public static async Task<int> RunAsync(TimeSpan duration, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, App.DiscoveryPort));
        }
        catch (SocketException ex)
        {
            Log.Error($"Couldn't listen on UDP port {App.DiscoveryPort}: {ex.SocketErrorCode}. Is another `discover` running?");
            return 1;
        }

        Log.Info($"Listening for hosts on UDP port {App.DiscoveryPort} for {duration.TotalSeconds:0} s...");
        var found = new HashSet<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(duration);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                JsonObject? beacon;
                try { beacon = JsonNode.Parse(result.Buffer) as JsonObject; }
                catch (JsonException) { continue; }
                if (beacon is null || Json.Type(beacon) != "Beacon" || Json.Str(beacon, "app") != App.AppId) continue;

                var port = (int)(Json.Num(beacon, "port") ?? App.DefaultPort);
                var ip = result.RemoteEndPoint.Address;
                if (!found.Add($"{ip}:{port}")) continue;
                var portArg = port == App.DefaultPort ? "" : $" --port {port}";
                Log.Good($"Found host \"{Json.Str(beacon, "name")}\" at {ip}. Join with: dotnet run tools/NetCheck.cs -- join {ip}{portArg}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        if (found.Count > 0) return 0;
        Log.Warn("No hosts found. UDP broadcast is probably blocked on this network (normal on campus Wi-Fi).");
        Console.WriteLine("  That's fine: use `join <ip>` with the address the host printed.");
        Console.WriteLine("  (Also check Windows Firewall isn't blocking dotnet/NetCheck on this laptop.)");
        return 4;
    }
}

// ---------------------------------------------------------------- shared plumbing

/// <summary>One TCP connection carrying newline-delimited JSON messages.</summary>
sealed class Wire : IDisposable
{
    readonly TcpClient _tcp;
    readonly StreamReader _reader;
    readonly StreamWriter _writer;
    readonly SemaphoreSlim _writeLock = new(1, 1);

    public string RemoteText { get; }
    public string LocalText { get; }

    public Wire(TcpClient tcp)
    {
        _tcp = tcp;
        tcp.NoDelay = true;
        var stream = tcp.GetStream();
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _reader = new StreamReader(stream, utf8);
        _writer = new StreamWriter(stream, utf8) { NewLine = "\n", AutoFlush = true };
        RemoteText = Text.Endpoint(tcp.Client.RemoteEndPoint);
        LocalText = Text.Endpoint(tcp.Client.LocalEndPoint);
    }

    public Task SendAsync(JsonObject message, CancellationToken ct = default) =>
        SendLineAsync(message.ToJsonString(), ct);

    public async Task SendLineAsync(string line, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try { await _writer.WriteLineAsync(line.AsMemory(), ct); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Next message, or null when the other side closed the connection.</summary>
    public async Task<JsonObject?> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct);
            if (line is null) return null;
            if (line.Length == 0) continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject obj && Json.Type(obj) != "") return obj;
            }
            catch (JsonException) { }
            Log.Warn($"Ignoring malformed message from {RemoteText}: {Text.Truncate(line, 80)}");
        }
    }

    public void Dispose() => _tcp.Dispose();
}

static class Json
{
    public static string Type(JsonObject o) => Str(o, "type") ?? "";
    public static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    public static double? Num(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out double d) ? d : null;
}

sealed record LocalAddress(IPAddress Address, IPAddress? Mask, string Interface, bool IsWifi)
{
    public string Describe() =>
        Address.AddressFamily == AddressFamily.InterNetwork
            ? $"{Interface}, IPv4, mask {Mask}"
            : $"{Interface}, IPv6";
}

static class NetInfo
{
    static bool IsLocalVirtualAdapter(NetworkInterface nic)
    {
        var text = $"{nic.Name} {nic.Description}";
        string[] markers = ["vEthernet", "Hyper-V", "VirtualBox", "VMware", "WSL"];
        return markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Usable addresses on active interfaces, Wi-Fi first.</summary>
    public static List<LocalAddress> LocalAddresses()
    {
        var list = new List<LocalAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            if (IsLocalVirtualAdapter(nic)) continue; // WSL/Hyper-V/VM adapters: other laptops can't reach these
            var isWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = ua.Address;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    if (ip.ToString().StartsWith("169.254.")) continue; // self-assigned: not really connected
                    list.Add(new LocalAddress(ip, ua.IPv4Mask, nic.Name, isWifi));
                }
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    if (ip.IsIPv6LinkLocal || ip.IsIPv6Teredo) continue; // not reachable without extra setup
                    list.Add(new LocalAddress(ip, null, nic.Name, isWifi));
                }
            }
        }
        return list
            .OrderByDescending(a => a.IsWifi)
            .ThenBy(a => a.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToList();
    }

    /// <summary>Directed broadcast address (e.g. 10.1.255.255) for each IPv4 interface.</summary>
    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        foreach (var a in LocalAddresses())
        {
            if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.Mask is null) continue;
            var ip = a.Address.GetAddressBytes();
            var mask = a.Mask.GetAddressBytes();
            if (mask.Length != 4) continue;
            var bcast = new byte[4];
            for (var i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
            yield return new IPAddress(bcast);
        }
    }
}

static class ConsoleInput
{
    /// <summary>Reads console lines on a background thread. /quit or /q stops the program.</summary>
    public static void Start(Func<string, Task> onLine)
    {
        var thread = new Thread(() =>
        {
            while (!App.Cts.IsCancellationRequested)
            {
                var line = Console.ReadLine();
                if (line is null) return; // no interactive console; keep running until Ctrl+C
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line is "/quit" or "/q") { App.Cts.Cancel(); return; }
                try { onLine(line).GetAwaiter().GetResult(); }
                catch (Exception ex) { Log.Warn($"Couldn't send: {ex.Message}"); }
            }
        }) { IsBackground = true };
        thread.Start();
    }
}

static class Opt
{
    public static string Str(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    public static int Int(string[] args, string name, int fallback) =>
        int.TryParse(Str(args, name, ""), out var v) ? v : fallback;
}

static class Text
{
    public static string Clean(string? s, string fallback)
    {
        var cleaned = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > 24) cleaned = cleaned[..24];
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public static string Endpoint(EndPoint? ep) => ep switch
    {
        IPEndPoint ip when ip.Address.IsIPv4MappedToIPv6 => $"{ip.Address.MapToIPv4()}:{ip.Port}",
        IPEndPoint ip when ip.AddressFamily == AddressFamily.InterNetworkV6 => $"[{ip.Address}]:{ip.Port}",
        _ => ep?.ToString() ?? "?",
    };
}

static class Log
{
    static readonly object Gate = new();

    public static void Info(string msg) => Write(msg, null);
    public static void Good(string msg) => Write(msg, ConsoleColor.Green);
    public static void Warn(string msg) => Write(msg, ConsoleColor.Yellow);
    public static void Error(string msg) => Write(msg, ConsoleColor.Red);
    public static void Chat(string from, string text) => Write($"<{from}> {text}", ConsoleColor.Cyan);

    static void Write(string msg, ConsoleColor? color)
    {
        lock (Gate)
        {
            if (color is { } c) Console.ForegroundColor = c;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (color is not null) Console.ResetColor();
        }
    }
}
