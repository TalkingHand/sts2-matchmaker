using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using Sts2Matchmaker.Localization;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Adds a "링크 복사" (copy link) button to a rehost load screen (NMultiplayerLoadGameScreen/NDailyRunLoadScreen/
/// NCustomRunLoadScreen), pinned to the exact same screen coordinates the vanilla "초대" (invite) button occupies
/// on the FRESH-lobby screens (NCharacterSelectScreen/NCustomRunScreen) - offset_left=36, offset_top=45 off the
/// screen's own top-left corner, confirmed straight from remote_player_container.tscn's own RemotePlayerContainer
/// node (only offset_bottom gets overridden per-screen there, so 36/45 is the real shared origin every fresh
/// screen actually uses).
///
/// A "재초대" (re-invite, bulk SteamFriends.InviteUserToGame to missing original participants) button was tried
/// above this one and pulled back out: Valve's own docs confirm InviteUserToGame only reaches actual Steam
/// friends or shared-group members (not "recently played with" alone, which is a separate, display-only
/// SetPlayedWith/GetCoplayFriend tracking that doesn't bypass that check), and worse, its accept path fires
/// GameRichPresenceJoinRequested_t (or a bare command-line launch) - neither of which this game's own
/// SteamJoinCallbackHandler listens for (it only handles GameLobbyJoinRequested_t and +connect_lobby). Making
/// that reliable would mean registering our own GameRichPresenceJoinRequested_t callback and bridging into the
/// game's internal join flow (SteamClientConnectionInitializer.FromLobby + NGame.Instance.MainMenu.JoinGame) -
/// a much bigger, untested-without-two-live-accounts change than this feature was worth for now.
///
/// Deliberately NOT nested inside NRemoteLoadLobbyPlayerContainer's own "Container" FlowContainer the way
/// RecruitToggleInjector nests into the fresh screens' equivalent - checked all three rehost .tscn files directly
/// and that container sits in a different spot on every single one of them (screen-center on the multiplayer
/// load screen, inside a CenterContainer on the daily-run one, mid-left-panel on the custom-run one), so
/// docking onto it would put this button in three different places depending on which screen is open. Added as a
/// direct child of the screen root instead and positioned with an absolute top-left anchor, which is the only way
/// to land on the same spot on all three regardless of how each one lays out its own player-card list.
/// </summary>
public static class RehostLinkInjector
{
    public static void Inject(Control screenNode, LoadRunLobby lobby)
    {
        // Screens here are pooled/reused across separate hosting attempts the same way the fresh-lobby screens
        // are (see RecruitToggleInjector's own doc) - re-entering must replace, not stack, any row already there.
        Node? existingRow = screenNode.FindChild("Sts2CopyLinkRow", recursive: true, owned: false);
        existingRow?.Free();

        var wrapper = new Control { Name = "Sts2CopyLinkRow", CustomMinimumSize = new Vector2(200f, 50f) };
        wrapper.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        wrapper.OffsetLeft = 36f;
        wrapper.OffsetTop = 45f;
        wrapper.OffsetRight = 236f;
        wrapper.OffsetBottom = 95f;
        var buttonRow = new HBoxContainer();
        buttonRow.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        wrapper.AddChild(buttonRow);

        // Same 200x50 + value:1f as RecruitToggleInjector's own toggle button - see that class's doc for why
        // value:1f (not BuildPillButton's usual 0.9 default) is what actually matches the vanilla Invite button's
        // steady-state look once focus/unfocus has touched it, which happens the instant it's shown.
        Button copyLinkButton = Sts2ModalPanel.BuildPillButton(new Vector2(200f, 50f), out RichTextLabel label, value: 1f);
        buttonRow.AddChild(copyLinkButton);
        Sts2ModalPanel.WireHoverBrightness(copyLinkButton);

        label.Text = Loc.Get("링크 복사");
        copyLinkButton.Pressed += () => OnPressed(lobby, label);

        screenNode.AddChild(wrapper);
    }

    /// <summary>label is BuildPillButton's own RichTextLabel out-param (not MatchConditionsWindow's plain Label),
    /// swapped directly the same way that window's own OnCopyInviteLinkPressed does for its "복사됨!" feedback.</summary>
    private static void OnPressed(LoadRunLobby lobby, RichTextLabel label)
    {
        if (!Sts2ModalPanel.CopyJoinLinkToClipboard(lobby.NetService))
        {
            return;
        }
        label.Text = Loc.Get("복사됨!");
        label.GetTree().CreateTimer(1.5).Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(label))
            {
                label.Text = Loc.Get("링크 복사");
            }
        };
    }
}
