using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using Sts2Matchmaker.Helpers;
using Sts2Matchmaker.UI;
using Godot;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Blitz-mode (NBlitzScreen, v0.113.0+) counterparts of CharacterSelectRecruitPatch and
/// CharacterSelectHostIdCapturePatch. NBlitzScreen has the same InitializeMultiplayerAsHost/AsClient + public
/// Lobby shape as NCharacterSelectScreen, but the type doesn't exist on older game builds, so it can't appear in a
/// [HarmonyPatch(typeof(...))] attribute (that would break loading this whole assembly there - see
/// GameModeCompat). The target is resolved by name instead, and Prepare() returning false makes Harmony skip the
/// patch cleanly on builds without Blitz rather than logging a failed patch.
///
/// Also covers a vanilla-hosted Blitz room (host menu -&gt; 블리츠), the same way the Standard/Custom recruit
/// patches cover vanilla-hosted rooms of their own modes.
/// </summary>
[HarmonyPatch]
public static class BlitzRunRecruitPatch
{
    public static bool Prepare() => GameModeCompat.BlitzScreenType != null;

    public static MethodBase TargetMethod() => AccessTools.Method(GameModeCompat.BlitzScreenType, "InitializeMultiplayerAsHost");

    [HarmonyPostfix]
    public static void Postfix(object __instance)
    {
        StartRunLobby? lobby = Traverse.Create(__instance).Property("Lobby").GetValue<StartRunLobby>();
        if (lobby == null)
        {
            return;
        }
        RecruitToggleInjector.Inject((Node)__instance, lobby);
    }
}

[HarmonyPatch]
public static class BlitzRunHostIdCapturePatch
{
    public static bool Prepare() => GameModeCompat.BlitzScreenType != null;

    public static MethodBase TargetMethod() => AccessTools.Method(GameModeCompat.BlitzScreenType, "InitializeMultiplayerAsClient");

    [HarmonyPostfix]
    public static void Postfix(INetGameService gameService) => KickBanPromptShared.RememberHostId(gameService);
}
