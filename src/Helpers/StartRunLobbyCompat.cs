using System.Collections;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace Sts2Matchmaker.Helpers;

/// <summary>
/// StartRunLobby.Players is List&lt;LobbyPlayer&gt; on general but List&lt;StartRunLobbyPlayer&gt; on beta - same
/// property name, different closed generic type. Unlike a plain type/member-name mismatch, this doesn't throw at
/// mod-load time: the JIT only resolves get_Players()'s exact signature the first time a compiled call site
/// actually runs, so it surfaces as a MissingMethodException deep in whatever screen first reads .Players (found
/// via MatchConditionsWindow.ShowFor crashing on general - see git history). Read the count through here instead
/// of "lobby.Players.Count" so this assembly's metadata never names either closed generic type.
/// </summary>
public static class StartRunLobbyCompat
{
    public static int GetPlayerCount(StartRunLobby lobby)
    {
        object players = typeof(StartRunLobby).GetProperty("Players")!.GetValue(lobby)!;
        return ((ICollection)players).Count;
    }
}
