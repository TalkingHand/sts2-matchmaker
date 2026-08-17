using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using Sts2Matchmaker.Helpers;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Auto-readies the local player the instant every original participant is back in a rehosted run's load screen
/// (NMultiplayerLoadGameScreen/NDailyRunLoadScreen/NCustomRunLoadScreen - the only place InitializeAsHost/
/// InitializeAsClient with a LoadRunLobby get called at all, so this is inherently rehost-only, never touching a
/// fresh character-select embark). Unlike fresh hosting's character/ascension picks, there's nothing left to
/// decide here - the save already fixes everyone's character, deck, and progress - so once the roster matches
/// the save's own player count there's no reason to make each player click the embark button by hand; only
/// players who have this mod installed get the shortcut, everyone else still clicks normally and nothing about
/// their own control over readying (the native "취소" un-ready button) is touched.
///
/// LoadRunLobby.SetReady(bool) is called directly (a public, data-level method) rather than reflecting into the
/// native OnEmbarkPressed(NButton) UI handler - same end state, without depending on a specific button reference
/// or unverified click-handler side effects.
/// </summary>
internal static class RehostAutoReadyShared
{
    public static void ReadyUpIfRosterComplete(LoadRunLobby? lobby)
    {
        try
        {
            if (lobby?.Run == null)
            {
                return;
            }
            int connectedPlayerCount = LoadRunLobbyCompat.GetConnectedPlayerCount(lobby);
            if (connectedPlayerCount < lobby.Run.Players.Count)
            {
                return;
            }
            Log.Info($"[sts2_matchmaker] Rehost roster complete ({connectedPlayerCount}/{lobby.Run.Players.Count}) - auto-readying");
            lobby.SetReady(true);
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to auto-ready rehost lobby: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen))]
public static class MultiplayerLoadGameAutoReadyPatch
{
    [HarmonyPatch("InitializeAsHost")]
    [HarmonyPostfix]
    public static void HostPostfix(NMultiplayerLoadGameScreen __instance) =>
        Callable.From(() => RehostAutoReadyShared.ReadyUpIfRosterComplete(Traverse.Create(__instance).Field("_runLobby").GetValue<LoadRunLobby>())).CallDeferred();

    [HarmonyPatch("InitializeAsClient")]
    [HarmonyPostfix]
    public static void ClientPostfix(NMultiplayerLoadGameScreen __instance) =>
        Callable.From(() => RehostAutoReadyShared.ReadyUpIfRosterComplete(Traverse.Create(__instance).Field("_runLobby").GetValue<LoadRunLobby>())).CallDeferred();

    [HarmonyPatch("PlayerConnected")]
    [HarmonyPostfix]
    public static void PlayerConnectedPostfix(NMultiplayerLoadGameScreen __instance) =>
        RehostAutoReadyShared.ReadyUpIfRosterComplete(Traverse.Create(__instance).Field("_runLobby").GetValue<LoadRunLobby>());
}

[HarmonyPatch(typeof(NDailyRunLoadScreen))]
public static class DailyRunLoadAutoReadyPatch
{
    [HarmonyPatch("InitializeAsHost")]
    [HarmonyPostfix]
    public static void HostPostfix(NDailyRunLoadScreen __instance) =>
        Callable.From(() => RehostAutoReadyShared.ReadyUpIfRosterComplete(Traverse.Create(__instance).Field("_lobby").GetValue<LoadRunLobby>())).CallDeferred();

    [HarmonyPatch("InitializeAsClient")]
    [HarmonyPostfix]
    public static void ClientPostfix(NDailyRunLoadScreen __instance) =>
        Callable.From(() => RehostAutoReadyShared.ReadyUpIfRosterComplete(Traverse.Create(__instance).Field("_lobby").GetValue<LoadRunLobby>())).CallDeferred();

    [HarmonyPatch("PlayerConnected")]
    [HarmonyPostfix]
    public static void PlayerConnectedPostfix(NDailyRunLoadScreen __instance) =>
        RehostAutoReadyShared.ReadyUpIfRosterComplete(Traverse.Create(__instance).Field("_lobby").GetValue<LoadRunLobby>());
}

[HarmonyPatch(typeof(NCustomRunLoadScreen))]
public static class CustomRunLoadAutoReadyPatch
{
    [HarmonyPatch("InitializeAsHost")]
    [HarmonyPostfix]
    public static void HostPostfix(NCustomRunLoadScreen __instance) =>
        Callable.From(() => RehostAutoReadyShared.ReadyUpIfRosterComplete(Traverse.Create(__instance).Field("_lobby").GetValue<LoadRunLobby>())).CallDeferred();

    [HarmonyPatch("InitializeAsClient")]
    [HarmonyPostfix]
    public static void ClientPostfix(NCustomRunLoadScreen __instance) =>
        Callable.From(() => RehostAutoReadyShared.ReadyUpIfRosterComplete(Traverse.Create(__instance).Field("_lobby").GetValue<LoadRunLobby>())).CallDeferred();

    [HarmonyPatch("PlayerConnected")]
    [HarmonyPostfix]
    public static void PlayerConnectedPostfix(NCustomRunLoadScreen __instance) =>
        RehostAutoReadyShared.ReadyUpIfRosterComplete(Traverse.Create(__instance).Field("_lobby").GetValue<LoadRunLobby>());
}
