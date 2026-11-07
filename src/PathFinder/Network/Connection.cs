using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PathFinder.Network;

/// <summary>One TCP connection carrying newline-delimited JSON <see cref="NetMessage"/>s.</summary>
public sealed class Connection : IDisposable
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    readonly TcpClient _tcp;
    readonly StreamReader _reader;
    readonly StreamWriter _writer;
    readonly SemaphoreSlim _writeLock = new(1, 1);

    public Connection(TcpClient tcp)
    {
        _tcp = tcp;
        tcp.NoDelay = true;
        var stream = tcp.GetStream();
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _reader = new StreamReader(stream, utf8);
        _writer = new StreamWriter(stream, utf8) { NewLine = "\n", AutoFlush = true };
        RemoteEndPoint = tcp.Client.RemoteEndPoint as IPEndPoint;
    }

    public IPEndPoint? RemoteEndPoint { get; }

    public static string Serialize(NetMessage message) => JsonSerializer.Serialize(message, JsonOptions);

    public Task SendAsync(NetMessage message, CancellationToken ct = default) => SendLineAsync(Serialize(message), ct);

    /// <summary>Sends an already-serialized message (lets broadcasts serialize once).</summary>
    public async Task SendLineAsync(string line, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try { await _writer.WriteLineAsync(line.AsMemory(), ct); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Next message, or null when the other side closed the connection. Skips malformed lines.</summary>
    public async Task<NetMessage?> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct);
            if (line is null) return null;
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize<NetMessage>(line, JsonOptions) is { } message) return message;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                Debug.WriteLine($"[net] Ignoring bad message from {RemoteEndPoint}: {ex.Message}");
            }
        }
    }

    public void Dispose() => _tcp.Dispose();
}

/// <summary>A connection problem with a message that can be shown to the player as-is.</summary>
public sealed class NetworkException(string message, Exception? inner = null) : Exception(message, inner);
