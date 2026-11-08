using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace PathFinder.Network;

// Cloud Navigator: co-op mode. One player (the navigator, "in the clouds") sees the whole map and
// talks; the other (the driver, "on the ground") sees only the exits of the intersection they're at
// and picks one step at a time. The host builds the driver's route and checks every step.
// The message types are registered on NetMessage in Messages.cs (JsonDerivedType needs them there).

/// <summary>Which game the host runs when the match starts. Sent as text, e.g. "Navigator".</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GameMode>))]
public enum GameMode
{
    /// <summary>The original mode: everyone builds a route on the map at the same time; best route wins.</summary>
    Competitive,
    /// <summary>Cloud Navigator: one team of two against the clock.</summary>
    Navigator,
}

/// <summary>A player's job in Cloud Navigator. None = not chosen; the host fills in a default at match start.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PlayerRole>))]
public enum PlayerRole
{
    None,
    Navigator,
    Driver,
}

// Client -> host
/// <summary>Lobby only: "I want to be the navigator/driver". Whoever had that role before goes back to None.</summary>
public sealed record ChooseRoleMessage(PlayerRole Role) : NetMessage;

/// <summary>
/// Driver only: drive one street, from where the driver is now to <paramref name="NodeId"/>.
/// The host ignores it unless that intersection is joined by a street to the driver's current one.
/// </summary>
public sealed record DriveToMessage(int RoundNumber, int NodeId) : NetMessage;

// Host -> all
/// <summary>
/// The driver is now at <paramref name="NodeId"/>, facing <paramref name="HeadingDegrees"/> (compass
/// degrees: 0 = north, 90 = east), having driven <paramref name="DistanceSoFar"/> metres this round.
/// Sent once when a round starts (at the start, distance 0) and after every accepted step.
/// </summary>
public sealed record DriverMovedMessage(int RoundNumber, string PlayerId, int NodeId, double HeadingDegrees, double DistanceSoFar)
    : NetMessage;

/// <summary>
/// Sent just before each round's RoundStart: who drives and who navigates this round (the team swaps
/// every round, so both players get to drive) and which way the driver faces at the start (picked at
/// random by the host, so every client shows the same thing).
/// </summary>
public sealed record NavigatorRoundMessage(int RoundNumber, string NavigatorId, string DriverId, double StartHeadingDegrees)
    : NetMessage;

// Host -> the driver only
/// <summary>
/// The host refused the driver's last DriveTo (stale click, wrong round, not a street from here...).
/// The driver's position hasn't changed; <paramref name="Reason"/> is short and can be shown as is.
/// </summary>
public sealed record DriveRejectedMessage(int RoundNumber, string Reason) : NetMessage;

// Host -> all
/// <summary>
/// Sent just before a Cloud Navigator round's RoundResults: how the team's score was worked out.
/// Score = RoutePoints x SpeedMultiplier, where SpeedMultiplier = 1 + the fraction of the round's time
/// left on arrival (1 to 2). Everything is 0 if the driver didn't arrive.
/// </summary>
public sealed record NavigatorScoreMessage(int RoundNumber, bool Arrived, double RoutePoints, double SecondsUsed,
                                           int RoundSeconds, double SpeedMultiplier, double Score, double DistanceDriven)
    : NetMessage;

/// <summary>Who is the navigator and who is the driver, resolved from the lobby's choices.</summary>
public static class NavigatorRoles
{
    /// <summary>
    /// One team from the players in join order. Explicit choices win; otherwise the host player
    /// navigates and the first other player drives. Anyone left over watches (null if nobody fits).
    /// Used by the host at match start, and by the lobby to preview the teams.
    /// TODO (navigator mode): several teams (stretch goal): pair players up instead of picking one pair.
    /// </summary>
    public static (string? NavigatorId, string? DriverId) Assign(IReadOnlyList<PlayerInfo> players)
    {
        var navigator = players.FirstOrDefault(p => p.Role == PlayerRole.Navigator)?.Id;
        var driver = players.FirstOrDefault(p => p.Role == PlayerRole.Driver && p.Id != navigator)?.Id;

        // Defaults for anyone who didn't choose: the host navigates (it's the laptop with the big map), the next player drives.
        navigator ??= players.Where(p => p.Id != driver && p.Role == PlayerRole.None)
                             .OrderByDescending(p => p.IsHost).FirstOrDefault()?.Id;
        driver ??= players.FirstOrDefault(p => p.Id != navigator && p.Role == PlayerRole.None)?.Id;
        return (navigator, driver);
    }

    /// <summary>The players with their resolved roles filled in (everyone else None), for the lobby list.</summary>
    public static List<PlayerInfo> WithRoles(IReadOnlyList<PlayerInfo> players)
    {
        var (navigator, driver) = Assign(players);
        return players.Select(p => p with
        {
            Role = p.Id == navigator ? PlayerRole.Navigator : p.Id == driver ? PlayerRole.Driver : PlayerRole.None,
        }).ToList();
    }
}
