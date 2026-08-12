using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using Sts2Matchmaker.Matchmaking;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Silences the native "접속 실패" popup (NErrorPopup) while GuestMatchService is working through DC-crawled join
/// candidates - see GuestJoinErrorSuppression's own doc. NErrorPopup.Create(NetErrorInfo) is the single choke point
/// every join-failure path in the game funnels through (NJoinFriendScreen.JoinGameAsync's catch block and its
/// RunSessionState.Running branch both call this exact overload), and it already has a "don't actually create a
/// popup" early-return for TestMode.IsOn - this just adds our own flag as another such gate, via prefix instead of
/// touching the game's own method body.
/// </summary>
[HarmonyPatch(typeof(NErrorPopup), nameof(NErrorPopup.Create), new[] { typeof(NetErrorInfo) })]
public static class GuestJoinErrorSuppressionPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ref NErrorPopup? __result)
    {
        if (!GuestJoinErrorSuppression.Suppress)
        {
            return true;
        }
        __result = null;
        return false;
    }
}
