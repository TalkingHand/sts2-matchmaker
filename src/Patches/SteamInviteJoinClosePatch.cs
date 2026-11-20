using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Platform.Steam;
using Sts2Matchmaker.UI;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Accepting a Steam invite (or "Join game" from the friends list) while one of our popups is open goes through
/// SteamJoinCallbackHandler.JoinToHost, which pops the whole main-menu submenu stack and joins the new lobby - but
/// never touches NModalContainer, so our popup stayed on top of the new lobby. Worse, MatchmakingWindow's
/// auto-match search kept running underneath and could later join/host somewhere else, pulling the player back
/// out of the room they just chose to join. Closing the popup here also cancels that search (see
/// MatchmakingWindow.OnCloseRequested).
///
/// Prefix on the async method runs synchronously at the call, before the vanilla stack pops. JoinToHost's in-run
/// branch adds its own confirmation popup to NModalContainer, but our popups only exist on the main menu, so
/// nothing of ours can be open in that case.
/// </summary>
[HarmonyPatch(typeof(SteamJoinCallbackHandler), "JoinToHost")]
public static class SteamInviteJoinClosePatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        if (NModalContainer.Instance?.OpenModal is not Sts2ModalPanel panel)
        {
            return;
        }
        Log.Info($"[sts2_matchmaker] Steam invite join while {panel.GetType().Name} is open - closing it");
        panel.ForceCloseForExternalJoin();
    }
}
