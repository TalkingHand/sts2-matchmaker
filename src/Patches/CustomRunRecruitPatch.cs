using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using Sts2Matchmaker.UI;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Same as CharacterSelectRecruitPatch but for Custom-mode lobbies (NCustomRunScreen), which has the same public
/// Lobby property and NRemoteLobbyPlayerContainer shape as the Standard screen.
/// </summary>
[HarmonyPatch(typeof(NCustomRunScreen), "InitializeMultiplayerAsHost")]
public static class CustomRunRecruitPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCustomRunScreen __instance)
    {
        RecruitToggleInjector.Inject(__instance, __instance.Lobby);
    }
}
