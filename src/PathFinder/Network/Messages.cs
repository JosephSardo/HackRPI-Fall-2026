using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PathFinder.Network;

public static class Protocol
{
    /// <summary>Bump when messages change, so mismatched builds are turned away instead of misbehaving.</summary>
    public const int Version = 2;
    public const int DefaultPort = 5050;
    public const int MaxPlayers = 8;
}

public sealed record PlayerInfo(string Id, string Name, bool IsHost);

/// <summary>One player's outcome for a round. NodeIds is empty and Score is 0 if they didn't submit in time.</summary>
public sealed record RoundResult(string PlayerId, string Name, bool Submitted, List<int> NodeIds, double Score, int Rank);

/// <summary>A player's running total across the match.</summary>
public sealed record PlayerTotal(string PlayerId, string Name, double TotalScore, int Rank);

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
[JsonDerivedType(typeof(PingMessage), "Ping")]
[JsonDerivedType(typeof(PongMessage), "Pong")]
[JsonDerivedType(typeof(RoundStartMessage), "RoundStart")]
[JsonDerivedType(typeof(SubmitPathMessage), "SubmitPath")]
[JsonDerivedType(typeof(PlayerSubmittedMessage), "PlayerSubmitted")]
[JsonDerivedType(typeof(RoundResultsMessage), "RoundResults")]
[JsonDerivedType(typeof(ReadyForNextMessage), "ReadyForNext")]
[JsonDerivedType(typeof(MatchOverMessage), "MatchOver")]
public abstract record NetMessage;

// Client -> host
/// <summary>First message on every connection. HostKey is only set by the host's own client.</summary>
public sealed record JoinMessage(string Name, int ProtocolVersion, string? HostKey = null) : NetMessage;
/// <summary>Host player asks to leave the lobby and start the match.</summary>
public sealed record StartMatchMessage : NetMessage;
public sealed record PingMessage(long Seq) : NetMessage;
/// <summary>The player's one submission for the round: node IDs from start to end.</summary>
public sealed record SubmitPathMessage(int RoundNumber, List<int> NodeIds) : NetMessage;
/// <summary>Player left the results screen and is ready for the next round.</summary>
public sealed record ReadyForNextMessage(int RoundNumber) : NetMessage;

// Host -> client
public sealed record WelcomeMessage(string PlayerId) : NetMessage;
public sealed record RejectedMessage(string Reason) : NetMessage;
public sealed record LobbyUpdateMessage(List<PlayerInfo> Players) : NetMessage;
public sealed record PongMessage(long Seq) : NetMessage;
/// <summary>
/// A round begins now. Clients count down DurationSeconds from when this arrives
/// (laptop clocks don't agree, so no wall-clock times are sent).
/// </summary>
public sealed record RoundStartMessage(int RoundNumber, int TotalRounds, int StartId, int EndId, int DurationSeconds) : NetMessage;
/// <summary>Someone submitted (no path included, so no spoilers).</summary>
public sealed record PlayerSubmittedMessage(int RoundNumber, string PlayerId, int SubmittedCount, int PlayerCount) : NetMessage;
/// <summary>Everyone submitted or time ran out. Results are sorted best first.</summary>
public sealed record RoundResultsMessage(int RoundNumber, List<RoundResult> Results,
                                         List<int> OptimalNodeIds, double OptimalLength) : NetMessage;
/// <summary>Last round finished. Standings are sorted best first.</summary>
public sealed record MatchOverMessage(List<PlayerTotal> Standings) : NetMessage;
