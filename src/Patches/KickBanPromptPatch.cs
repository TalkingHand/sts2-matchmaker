using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
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

            // Appends to whatever NErrorPopup._Ready already set via SetText(KICKED.body) - %Description is
            // MegaRichTextLabel's own unique name in vertical_popup.tscn, reachable directly off verticalPopup
            // (the scene root that owns it), no reflection needed. Going through the shadowing `Text` setter
            // (MegaRichTextLabel's own, not RichTextLabel's base one) matters - that's what re-triggers the
            // auto font-size fit for the now-longer body (see MegaRichTextLabel.SetTextAutoSize).
            MegaRichTextLabel? bodyLabel = verticalPopup?.GetNodeOrNull<MegaRichTextLabel>("%Description");
            if (bodyLabel != null)
            {
                bodyLabel.Text = $"{bodyLabel.Text}\n호스트를 밴 하시겠습니까?";
            }

            // The native "좋아요"/OK button (InitYesButton's GENERIC_POPUP.ok case, since Kicked isn't a
            // report-bug-eligible reason) just dismissed the popup - AddCloseButton below now covers that same
            // plain dismissal, so keeping this too would just be a second, differently-styled control doing the
            // same thing. Hidden, not freed - same reasoning as the NoButton reuse below.
            NPopupYesNoButton? yesButton = verticalPopup?.YesButton;
            if (yesButton != null)
            {
                yesButton.Visible = false;
            }

            // Moved into YesButton's own native slot (flush against the popup's right edge) now that YesButton
            // is hidden - same offsets vertical_popup.tscn gives YesButton (see reference/ui/vertical_popup.tscn),
            // same approach BanConfirmPanel's own Confirm button uses to sit in that exact slot.
            noButton.Visible = true;
            noButton.SetText("밴 등록");
            noButton.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            noButton.OffsetLeft = -180f;
            noButton.OffsetRight = 0f;
            noButton.OffsetTop = -152f;
            noButton.OffsetBottom = -80f;

            // abandon_run_no_button.tscn's ribbon art is NOT the same texture as abandon_run_yes_button.tscn with
            // a flip_h flag already set - popup_cancel_button(_outline).tres and popup_confirm_button(_outline).tres
            // are two separately-drawn textures, and every internal offset (Visuals/Label/HotkeyIcon, plus
            // Visuals' own pivot_offset) is hand-authored as a left-slot mirror of Yes's right-slot numbers
            // (e.g. No's Label is offset_left=15/offset_right=-31, Yes's is the exact swap: 31/-15; No's pivot_offset
            // is (5,50), Yes's is (191,50) = 196-5, Visuals' own width). Simply moving noButton's own outer
            // anchors/offsets (above) repositions the whole 180x72 box but leaves all of THIS untouched, so the
            // red ribbon's notch/taper still faces the way it was drawn for the left slot - backwards for a button
            // now sitting on the right. FlipH on just the Outline/Image TextureRects mirrors the drawn ribbon
            // shape in place (Label/HotkeyIcon aren't textures, so FlipH doesn't touch them or mirror the Korean
            // text); their own offsets/pivot are set to Yes's exact authored numbers so they land where a real
            // right-slot button's text/hotkey-icon would, instead of where a left-slot button's do.
            TextureRect? outline = noButton.GetNodeOrNull<TextureRect>("%Outline");
            if (outline != null)
            {
                outline.FlipH = true;
            }
            TextureRect? image = noButton.GetNodeOrNull<TextureRect>("%Image");
            if (image != null)
            {
                image.FlipH = true;
            }
            Control? visuals = noButton.GetNodeOrNull<Control>("%Visuals");
            if (visuals != null)
            {
                visuals.OffsetLeft = -17f;
                visuals.OffsetTop = -8f;
                visuals.OffsetRight = 179f;
                visuals.OffsetBottom = 88f;
                visuals.PivotOffset = new Vector2(191f, 50f);
            }
            Control? label = noButton.GetNodeOrNull<Control>("%Label");
            if (label != null)
            {
                label.OffsetLeft = 31f;
                label.OffsetTop = -3f;
                label.OffsetRight = -15f;
                label.OffsetBottom = -3f;
            }
            Control? hotkeyIcon = noButton.GetNodeOrNull<Control>("%HotkeyIcon");
            if (hotkeyIcon != null)
            {
                hotkeyIcon.OffsetLeft = 150.99998f;
                hotkeyIcon.OffsetTop = 44.999996f;
                hotkeyIcon.OffsetRight = 198.99998f;
                hotkeyIcon.OffsetBottom = 93f;
            }

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

            // YesButton covered plain dismissal before it was hidden above - without a replacement, this popup
            // would otherwise have no way to close except registering a ban. Same native NBackButton
            // Sts2ModalPanel.AddCloseButton uses, just attached directly to the popup itself - also a FullRect
            // Control at origin (error_popup.tscn's root, anchors_preset=15), which is what NBackButton's own
            // window-relative self-positioning requires of its parent.
            Sts2ModalPanel.BuildCloseButton(popup, () => NModalContainer.Instance?.Clear());
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
