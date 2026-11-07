using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PathFinder.Network;

public static class Protocol
{
    /// <summary>Bump when messages change, so mismatched builds are turned away instead of misbehaving.</summary>
    public const int Version = 1;
    public const int DefaultPort = 5050;
    public const int MaxPlayers = 8;
}

public sealed record PlayerInfo(string Id, string Name, bool IsHost);

/// <summary>
/// Everything sent over the wire. Serialized as one JSON object per line, with a "type" field,
/// e.g. {"type":"Join","name":"Ryan","protocolVersion":1}.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JoinMessage), "Join")]
[JsonDerivedType(typeof(WelcomeMessage), "Welcome")]
[JsonDerivedType(typeof(RejectedMessage), "Rejected")]
[JsonDerivedType(typeof(LobbyUpdateMessage), "LobbyUpdate")]
[JsonDerivedType(typeof(StartMatchMessage), "StartMatch")]
[JsonDerivedType(typeof(MatchStartingMessage), "MatchStarting")]
[JsonDerivedType(typeof(PingMessage), "Ping")]
[JsonDerivedType(typeof(PongMessage), "Pong")]
public abstract record NetMessage;

// Client -> host
/// <summary>First message on every connection. HostKey is only set by the host's own client.</summary>
public sealed record JoinMessage(string Name, int ProtocolVersion, string? HostKey = null) : NetMessage;
/// <summary>Host player asks to leave the lobby and start the match.</summary>
public sealed record StartMatchMessage : NetMessage;
public sealed record PingMessage(long Seq) : NetMessage;

// Host -> client
public sealed record WelcomeMessage(string PlayerId) : NetMessage;
public sealed record RejectedMessage(string Reason) : NetMessage;
public sealed record LobbyUpdateMessage(List<PlayerInfo> Players) : NetMessage;
/// <summary>Placeholder until rounds exist: tells everyone the host started the match.</summary>
public sealed record MatchStartingMessage : NetMessage;
public sealed record PongMessage(long Seq) : NetMessage;
