using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using Sts2Matchmaker.UI;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Injects RehostLinkInjector's "링크 복사" button into every rehost load screen. Patches each screen's own
/// OnSubmenuOpened (not NRemoteLoadLobbyPlayerContainer.Initialize, which all three also call into) because the
/// button needs the screen's own root Control to anchor against a fixed screen position - the shared container
/// sits in a different spot on every one of these three screens (see RehostLinkInjector's own doc), so a patch on
/// it alone would only ever have that per-screen sub-control to add the button under, not the actual screen root.
///
/// _runLobby/_lobby are private fields, same as RehostAutoReadyPatch already reads via Traverse for these same
/// three classes (field name itself differs: NMultiplayerLoadGameScreen uses "_runLobby", the other two use
/// "_lobby") - not accessed here through any new reflection path, just the one this codebase already established.
/// </summary>
[HarmonyPatch(typeof(NMultiplayerLoadGameScreen))]
public static class MultiplayerLoadGameLinkPatch
{
    [HarmonyPatch("OnSubmenuOpened")]
    [HarmonyPostfix]
    public static void Postfix(NMultiplayerLoadGameScreen __instance)
    {
        var lobby = Traverse.Create(__instance).Field("_runLobby").GetValue<LoadRunLobby>();
        if (lobby != null)
        {
            RehostLinkInjector.Inject(__instance, lobby);
        }
    }
}

[HarmonyPatch(typeof(NDailyRunLoadScreen))]
public static class DailyRunLoadLinkPatch
{
    [HarmonyPatch("OnSubmenuOpened")]
    [HarmonyPostfix]
    public static void Postfix(NDailyRunLoadScreen __instance)
    {
        var lobby = Traverse.Create(__instance).Field("_lobby").GetValue<LoadRunLobby>();
        if (lobby != null)
        {
            RehostLinkInjector.Inject(__instance, lobby);
        }
    }
}

[HarmonyPatch(typeof(NCustomRunLoadScreen))]
public static class CustomRunLoadLinkPatch
{
    [HarmonyPatch("OnSubmenuOpened")]
    [HarmonyPostfix]
    public static void Postfix(NCustomRunLoadScreen __instance)
    {
        var lobby = Traverse.Create(__instance).Field("_lobby").GetValue<LoadRunLobby>();
        if (lobby != null)
        {
            RehostLinkInjector.Inject(__instance, lobby);
        }
    }
}
