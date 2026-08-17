using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace Sts2Matchmaker.Helpers;

/// <summary>
/// LoadRunLobby exposes the connected-player count under a different member depending on game branch: beta has
/// an int PlayerCount property, general instead has a HashSet&lt;ulong&gt; ConnectedPlayerIds collection with no
/// PlayerCount at all. Read via reflection so this assembly's metadata never names whichever member the running
/// branch lacks (a direct reference would throw ReflectionTypeLoadException on that branch).
/// </summary>
public static class LoadRunLobbyCompat
{
    public static int GetConnectedPlayerCount(LoadRunLobby lobby)
    {
        PropertyInfo? playerCountProp = typeof(LoadRunLobby).GetProperty("PlayerCount");
        if (playerCountProp != null)
        {
            return (int)playerCountProp.GetValue(lobby)!;
        }
        PropertyInfo connectedIdsProp = typeof(LoadRunLobby).GetProperty("ConnectedPlayerIds")!;
        return ((ICollection)connectedIdsProp.GetValue(lobby)!).Count;
    }
}
