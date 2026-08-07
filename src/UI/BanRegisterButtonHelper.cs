using Godot;
using Sts2Matchmaker.Matchmaking;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Shared appearance logic for the register-only "밴 등록" text button (BuildTextActionButton) used both in the
/// lobby (RemoteLobbyPlayerKickPatch) and on the 도전 이력 screen (RunHistoryPlayerBanPatch) - toggles between
/// "밴 등록"/"밴 해제" based on BanListStore.IsBanned, since clicking it while the target is already registered
/// should undo that instead of re-opening the same confirm dialog. Un-registering itself mirrors BanListPanel's
/// own "삭제" button (immediate BanListStore.Remove, no confirmation prompt) - only adding a ban asks for a
/// reason, removing one doesn't need to.
/// </summary>
internal static class BanRegisterButtonHelper
{
    /// <summary>Refreshes the text/tooltip on an existing register-ban button to match the target's current ban
    /// state. Goes through Sts2ModalPanel.UpdateTextActionButtonText (not button.Text directly) - BuildTextActionButton
    /// draws its label via a child Label, not the Button's own Text, so setting button.Text directly just turns on
    /// a second, out-of-sync native text layer on top of it instead of actually changing what's shown.</summary>
    public static void UpdateAppearance(Button button, ulong targetId, string registerTooltip)
    {
        bool isBanned = BanListStore.IsBanned(targetId);
        Sts2ModalPanel.UpdateTextActionButtonText(button, isBanned ? "밴 해제" : "밴 등록");
        button.TooltipText = isBanned ? "이 사람의 밴 등록을 취소합니다" : registerTooltip;
    }
}
