using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PathFinder.Network;

public static class Protocol
{
    /// <summary>Bump when messages change, so mismatched builds are turned away instead of misbehaving.</summary>
    public const int Version = 7;
    public const int DefaultPort = 5050;
    public const int MinPlayers = 2;
    /// <summary>
    /// Players needed to start: <see cref="MinPlayers"/>, or 1 for a practice (solo) competitive match.
    /// Cloud Navigator always needs two (a navigator and a driver).
    /// </summary>
    public static int MinPlayersFor(GameMode mode, bool practice) =>
        practice && mode == GameMode.Competitive ? 1 : MinPlayers;
    public const int MaxPlayers = 8;

    // Match settings the host player picks in the lobby. The host clamps whatever it receives to these limits.
    public const int DefaultRounds = 3, MinRounds = 1, MaxRounds = 10;
    public const int DefaultRoundSeconds = 45, MinRoundSeconds = 15, MaxRoundSeconds = 600;
}

/// <summary>A player in the lobby. Role only matters in Cloud Navigator mode (see NavigatorMessages.cs).</summary>
public sealed record PlayerInfo(string Id, string Name, bool IsHost, PlayerRole Role = PlayerRole.None);

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
[JsonDerivedType(typeof(MatchStartMessage), "MatchStart")]
[JsonDerivedType(typeof(RoundStartMessage), "RoundStart")]
[JsonDerivedType(typeof(SubmitPathMessage), "SubmitPath")]
[JsonDerivedType(typeof(PlayerSubmittedMessage), "PlayerSubmitted")]
[JsonDerivedType(typeof(RoundResultsMessage), "RoundResults")]
[JsonDerivedType(typeof(MatchOverMessage), "MatchOver")]
// Cloud Navigator mode (defined in NavigatorMessages.cs)
[JsonDerivedType(typeof(ChooseRoleMessage), "ChooseRole")]
[JsonDerivedType(typeof(DriveToMessage), "DriveTo")]
[JsonDerivedType(typeof(DriverMovedMessage), "DriverMoved")]
public abstract record NetMessage;

// Client -> host
/// <summary>First message on every connection. HostKey is only set by the host's own client.</summary>
public sealed record JoinMessage(string Name, int ProtocolVersion, string? HostKey = null) : NetMessage;
/// <summary>
/// Host player starts the match with these settings (the host clamps them to the limits in <see cref="Protocol"/>).
/// Practice lets the host start alone (see <see cref="Protocol.MinPlayersFor"/>).
/// </summary>
public sealed record StartMatchMessage(int Rounds, int RoundSeconds, GameMode Mode = GameMode.Competitive, bool Practice = false) : NetMessage;
public sealed record PingMessage(long Seq) : NetMessage;
/// <summary>The player's one submission for the round: node IDs from start to end.</summary>
public sealed record SubmitPathMessage(int RoundNumber, List<int> NodeIds) : NetMessage;

// Host -> client
public sealed record WelcomeMessage(string PlayerId) : NetMessage;
public sealed record RejectedMessage(string Reason) : NetMessage;
public sealed record LobbyUpdateMessage(List<PlayerInfo> Players) : NetMessage;
public sealed record PongMessage(long Seq) : NetMessage;
/// <summary>
/// Sent once before round 1: the host's canonical map, so every player plays on identical IDs.
/// Each round then only sends its start/end.
/// </summary>
public sealed record MatchStartMessage(MapData Map, int TotalRounds, GameMode Mode = GameMode.Competitive) : NetMessage;
/// <summary>
/// A round begins now. Clients count down DurationSeconds from when this arrives
/// (laptop clocks don't agree, so no wall-clock times are sent).
/// </summary>
public sealed record RoundStartMessage(int RoundNumber, int TotalRounds, int StartId, int EndId, int DurationSeconds) : NetMessage;
/// <summary>Someone submitted (no path included, so no spoilers).</summary>
public sealed record PlayerSubmittedMessage(int RoundNumber, string PlayerId, int SubmittedCount, int PlayerCount) : NetMessage;
/// <summary>
/// Everyone submitted or time ran out. Results are sorted best first. The next round (or, after
/// the last round, MatchOver) follows automatically after SecondsUntilNext.
/// </summary>
public sealed record RoundResultsMessage(int RoundNumber, List<RoundResult> Results,
                                         List<int> OptimalNodeIds, double OptimalLength,
                                         int SecondsUntilNext) : NetMessage;
/// <summary>Last round finished. Standings are sorted best first.</summary>
public sealed record MatchOverMessage(List<PlayerTotal> Standings) : NetMessage;
