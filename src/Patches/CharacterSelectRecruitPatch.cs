using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using Sts2Matchmaker.UI;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Lets the host of an already-open Standard-mode lobby (opened normally via the vanilla Host button among
/// friends, or via our own flow) open the remaining seats up to matchmaking search without restarting anything.
/// Patches InitializeMultiplayerAsHost (not _Ready) because that's the point where NCharacterSelectScreen.Lobby
/// actually gets assigned - patching _Ready would hit the same still-null-at-that-point bug the earlier
/// NSubmenuStack injection had.
/// </summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), "InitializeMultiplayerAsHost")]
public static class CharacterSelectRecruitPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCharacterSelectScreen __instance)
    {
        RecruitToggleInjector.Inject(__instance, __instance.Lobby);
    }
}
