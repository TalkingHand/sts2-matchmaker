using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using Sts2Matchmaker.UI;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Same idea as CharacterSelectRecruitPatch/CustomRunRecruitPatch, but NDailyRunScreen doesn't fit their shared
/// pattern: its own InitializeMultiplayerAsHost(INetGameService) only stashes _netService - unlike the other two
/// screens, it does NOT synchronously create the lobby, so a postfix there would find a null Lobby. The actual
/// StartRunLobby only exists once SetupLobbyForHostOrSingleplayer (an async Task awaiting a time-server round trip,
/// fired from OnSubmenuOpened) finishes and calls AfterLobbyInitialized - which is also where
/// _remotePlayerContainer.Initialize(...) itself runs, so it's the correct "screen's multiplayer UI is now wired
/// up" moment to mirror. Also, unlike the other two screens, Lobby is a private field ("_lobby") here, not a public
/// property - confirmed by decompiling NDailyRunScreen (sts2.dll) directly, hence the Traverse read below instead
/// of __instance.Lobby.
///
/// AfterLobbyInitialized fires for solo AND client too (it's the shared finish-line for all three
/// InitializeXxx paths), so this checks NetService.Type == Host itself rather than relying on which method got
/// patched to imply that, the way CharacterSelectRecruitPatch/CustomRunRecruitPatch do.
/// </summary>
[HarmonyPatch(typeof(NDailyRunScreen), "AfterLobbyInitialized")]
public static class DailyRunRecruitPatch
{
    [HarmonyPostfix]
    public static void Postfix(NDailyRunScreen __instance)
    {
        StartRunLobby? lobby = Traverse.Create(__instance).Field("_lobby").GetValue<StartRunLobby>();
        if (lobby == null || lobby.NetService.Type != NetGameType.Host)
        {
            return;
        }
        RecruitToggleInjector.Inject(__instance, lobby);
    }
}
