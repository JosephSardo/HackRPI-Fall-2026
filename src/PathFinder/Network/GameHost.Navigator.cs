using System;
using System.Linq;
using System.Threading.Tasks;

namespace PathFinder.Network;

// Cloud Navigator additions to the host, kept in their own file. Same rules as the rest of GameHost:
// only ever called from the event loop, so no locks; the game rules live in NavigatorMatch.
public sealed partial class GameHost
{
    /// <summary>Lobby: a player picks a role. Navigator and Driver are one player each, so whoever had it loses it.</summary>
    Task ChooseRoleAsync(Peer peer, PlayerRole role)
    {
        if (!Enum.IsDefined(role) || peer.Info is null) return Task.CompletedTask;
        if (role != PlayerRole.None)
            foreach (var other in _players.Where(p => p != peer && p.Info!.Role == role))
                other.Info = other.Info! with { Role = PlayerRole.None };
        // TODO (navigator mode): several teams (stretch goal) would need a team number here too.
        peer.Info = peer.Info with { Role = role };
        return BroadcastLobbyAsync();
    }

    /// <summary>
    /// Starts a Cloud Navigator match: settles who navigates and who drives (filling in defaults for
    /// anyone who didn't choose), tells everyone via LobbyUpdate, then MatchStart and round 1.
    /// </summary>
    async Task StartNavigatorMatchAsync(int rounds, int roundSeconds)
    {
        var resolved = NavigatorRoles.WithRoles(_players.Select(p => p.Info!).ToList());
        var navigator = resolved.FirstOrDefault(p => p.Role == PlayerRole.Navigator);
        var driver = resolved.FirstOrDefault(p => p.Role == PlayerRole.Driver);
        if (navigator is null || driver is null) return; // can't happen with 2+ players

        for (var i = 0; i < _players.Count; i++) _players[i].Info = resolved[i];
        var match = new NavigatorMatch(_rules, _map, resolved, navigator.Id, driver.Id,
                                       rounds, roundSeconds, (int)ResultsDuration.TotalSeconds);
        _match = match;
        await BroadcastLobbyAsync(); // roles first, so every client knows who drives before the round starts
        await ApplyAsync([new MatchStartMessage(_mapSnapshot, rounds, GameMode.Navigator), .. match.Start()]);
    }
}
