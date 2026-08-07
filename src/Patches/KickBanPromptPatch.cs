using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using Sts2Matchmaker.Matchmaking;
using Sts2Matchmaker.UI;
using Steamworks;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Adds a "밴 등록" option directly onto the native "추방당했습니다" (Kicked) error popup, so a player who just got
/// kicked can register the kicking host without ever having had a realistic chance to do it beforehand - the
/// in-lobby moderation UI (RemoteLobbyPlayerKickPatch's own "밴 등록" button) is gone the instant they're
/// disconnected, well before this popup even appears, so today there's no way at all to flag a host who kicks
/// people on sight.
///
/// The host's SteamID is NOT looked up at disconnect time - Steamworks' GetLobbyOwner requires still being a
/// lobby member (confirmed against Valve's own docs, see MatchTags.OwnerKey's doc for the same issue elsewhere in
/// this mod), and by the time a Kicked popup shows, that membership may already be gone. Instead it's cached
/// once, right when InitializeMultiplayerAsClient confirms a successful join - at that exact moment Steam lobby
/// membership is guaranteed (the P2P handshake that produced this call couldn't have started without it) - and
/// simply read back later whenever a Kicked popup shows, with no further Steam query needed at all.
///
/// Two independent captures feed the same augmentation step, since no single hook has all three pieces (host id,
/// "was this popup for a Kicked disconnect", and the popup's own live UI nodes) at once:
/// - InitializeMultiplayerAsClient (fires once per successful join) caches the host's SteamID.
/// - NErrorPopup.Create(NetErrorInfo) (fires once per popup, before it's added to the tree) remembers whether
///   THIS popup is for NetError.Kicked - the reason is unrecoverable after this point (NErrorPopup only keeps
///   the already-localized body text from here on, not the original NetError).
/// - NErrorPopup._Ready() (fires right after, once NModalContainer.Add makes the popup live) is where
///   _verticalPopup/NoButton actually exist to attach the button to.
/// </summary>
internal static class KickBanPromptShared
{
    private static ulong s_currentLobbyHostId;
    private static bool s_nextPopupIsKick;

    public static void RememberHostId(INetGameService gameService)
    {
        try
        {
            string? rawLobbyId = gameService.GetRawLobbyIdentifier();
            if (rawLobbyId == null || !ulong.TryParse(rawLobbyId, out ulong lobbyIdValue))
            {
                return;
            }
            s_currentLobbyHostId = SteamMatchmaking.GetLobbyOwner(new CSteamID(lobbyIdValue)).m_SteamID;
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to cache lobby host id on join: {ex}");
        }
    }

    public static void RememberIfKicked(NetErrorInfo info)
    {
        s_nextPopupIsKick = info.GetReason() == NetError.Kicked;
    }

    public static void AugmentIfKicked(NErrorPopup popup)
    {
        if (!s_nextPopupIsKick)
        {
            return;
        }
        s_nextPopupIsKick = false;
        ulong hostId = s_currentLobbyHostId;
        if (hostId == 0)
        {
            return;
        }
        try
        {
            NVerticalPopup? verticalPopup = Traverse.Create(popup).Field("_verticalPopup").GetValue<NVerticalPopup>();
            NPopupYesNoButton? noButton = verticalPopup?.NoButton;
            if (noButton == null || !GodotObject.IsInstanceValid(noButton))
            {
                return;
            }
            // HideNoButton() (already run by the popup's own native _Ready, since Kicked normally shows no second
            // button) just toggles Visible - the node itself is a permanent scene child, not freed, so it's safe
            // to show and re-wire here.
            noButton.Visible = true;
            noButton.SetText("밴 등록");
            noButton.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ =>
            {
                // Closes the error popup itself - NModalContainer only ever holds one modal at a time, so
                // BanConfirmPanel couldn't open on top of it anyway (same reasoning as MatchmakingWindow.JoinAsync
                // closing itself before attempting a join).
                NModalContainer.Instance?.Clear();
                string nickname = BanListStore.ResolveNickname(hostId);
                string targetLabel = string.IsNullOrEmpty(nickname) ? hostId.ToString() : nickname;
                BanConfirmPanel.Show(targetLabel, alsoKick: false, reason =>
                {
                    Log.Info($"[sts2_matchmaker] Registering ban for kicking host {hostId} (reason: {reason})");
                    BanListStore.Add(hostId, nickname, reason);
                });
            }));
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to augment kick popup with ban option: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NCharacterSelectScreen), "InitializeMultiplayerAsClient")]
public static class CharacterSelectHostIdCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(INetGameService gameService) => KickBanPromptShared.RememberHostId(gameService);
}

[HarmonyPatch(typeof(NCustomRunScreen), "InitializeMultiplayerAsClient")]
public static class CustomRunHostIdCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(INetGameService gameService) => KickBanPromptShared.RememberHostId(gameService);
}

[HarmonyPatch(typeof(NErrorPopup), "Create", new[] { typeof(NetErrorInfo) })]
public static class NErrorPopupKickCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(NetErrorInfo info) => KickBanPromptShared.RememberIfKicked(info);
}

[HarmonyPatch(typeof(NErrorPopup), "_Ready")]
public static class NErrorPopupAugmentPatch
{
    [HarmonyPostfix]
    public static void Postfix(NErrorPopup __instance) => KickBanPromptShared.AugmentIfKicked(__instance);
}
