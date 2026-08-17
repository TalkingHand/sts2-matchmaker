using HarmonyLib;

namespace Sts2Matchmaker.Helpers;

/// <summary>
/// The lobby-player struct passed around by NRemoteLobbyPlayerContainer's events is named LobbyPlayer on general
/// and StartRunLobbyPlayer on beta (same shape, different type name - see GameEventCompat), so call sites receive
/// it as object and read fields through here instead of a compile-time struct reference.
/// </summary>
public static class LobbyPlayerCompat
{
    public static ulong GetId(object player) => Traverse.Create(player).Field("id").GetValue<ulong>();
}
