using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using Sts2Matchmaker.Localization;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Shared confirmation dialog for both ban actions (in-lobby kick+ban, and in-run register-only ban) - captures a
/// reason (preset dropdown + free-text "기타") before actually acting, so a ban is never a single misclick.
/// Confirm is fixed to the bottom of the popup box (PopupBackground, not ContentContainer) rather than the last row
/// of scrollable content, so it stays reachable regardless of how far the body text and reason picker scroll -
/// though the popup is now sized generously enough (780x650) that scrolling shouldn't normally be needed at all
/// with the current body text length.
/// No separate Cancel button - the base class's own bottom-left CloseButton (native back-arrow, always present on
/// any Sts2ModalPanel via ShowAsModal) already does exactly that (dismiss without calling onConfirmed), so a second
/// in-box Cancel button was just a redundant way to do the same thing. Confirm uses the game's own generic dialog
/// "Yes" button (AddPopupYesButton - green ribbon, same one NErrorPopup labels "버그 제보" and a plain
/// NVerticalPopup labels "확인") rather than a custom-styled BuildTextActionButton, since this dialog IS
/// functionally that same kind of Yes/No confirmation popup.
/// </summary>
public class BanConfirmPanel : Sts2ModalPanel
{
    // StyleBodyLabel's own default (BodyTheme.DefaultFontSize = 18, shared by every other window using it) reads
    // small next to StyleAsSettingsDropdown's native 28px, and next to plain body text elsewhere in the actual
    // game. 26 matches NVerticalPopup's own Description RichTextLabel exactly (reference/ui/vertical_popup.tscn) -
    // the most directly applicable reference available, since this dialog IS functionally the same kind of
    // confirmation popup NVerticalPopup is. Local override (applied per-label below), not a BodyTheme change, so
    // it doesn't affect every other window that also uses StyleBodyLabel.
    private const int BodyFontSize = 26;

    public static void Show(string targetLabel, bool alsoKick, Action<string> onConfirmed)
    {
        var panel = new BanConfirmPanel();
        // Bumped up from the previous 720x600 pass to give BodyFontSize's larger text the same breathing room the
        // smaller 18px text had. Still proportional (780/650 = 720/600 = 480/400 = 1.2), same reasoning as before:
        // a disproportionate box stretches popup_vertical.tres's border/corner detail unevenly. Best estimate,
        // not a measured value - may need another pass once seen live, same as every previous size change here.
        panel.ShowAsModal(alsoKick ? Loc.Get("밴") : Loc.Get("밴 등록"), new Vector2(780f, 650f));

        // ShowAsModal's own scroll inset (40px each side) is a fixed pixel amount tuned for its original 480px
        // design width - at a wider box that's proportionally thinner, which is what made everything look packed
        // right up against the tablet's edge despite the tablet itself being the right size. Rather than touch
        // ShowAsModal (shared by every other window), this wraps an EXTRA 40px of margin around just this panel's
        // own content, on top of ShowAsModal's - 80px total each side. content is everything from here on, not
        // panel.ContentContainer directly.
        var contentPadding = new MarginContainer();
        contentPadding.AddThemeConstantOverride("margin_left", 40);
        contentPadding.AddThemeConstantOverride("margin_right", 40);
        panel.ContentContainer.AddChild(contentPadding);
        var content = new VBoxContainer();
        content.SetHSizeFlags(SizeFlags.ExpandFill);
        contentPadding.AddChild(content);

        // Free-text "기타" input stays on its own line below the row (not inline with it) - it only shows up
        // conditionally and reads as a continuation of the choice made above, not a second same-line control.
        // CustomMinimumSize.Y = 64 to match MatchConditionsPanel's "커뮤니티명" input exactly (both go through the
        // same StyleInput(LineEdit) for color/border, but that doesn't touch size - this one had no explicit size
        // at all before, so it rendered at LineEdit's own default thin height instead of matching).
        var customReasonInput = new LineEdit { PlaceholderText = Loc.Get("밴 사유를 입력하세요"), Visible = false, CustomMinimumSize = new Vector2(0f, 64f) };
        StyleInput(customReasonInput);

        string otherReasonLabel = Loc.Get("기타");
        string[] reasonOptions = { Loc.Get("봇"), Loc.Get("욕설"), Loc.Get("잠수"), otherReasonLabel };
        string selectedReason = reasonOptions[0];
        void OnReasonSelected(string value)
        {
            selectedReason = value;
            customReasonInput.Visible = value == otherReasonLabel;
        }

        // Sts2NativeDropdown reuses the game's own real settings_dropdown.tscn/language_dropdown_item.tscn (see
        // its own doc comment) instead of a themed plain OptionButton - falls back to
        // Sts2ModalPanel.StyleAsSettingsDropdown if that scene fails to load, so the reason picker never disappears.
        // overlayParent: panel - ShowAsModal also wraps its content in a real ScrollContainer (same as
        // ShowAsTabbedSettingsScreen), which would silently clip a long open list partway through with no way to
        // reach the rest (see Sts2NativeDropdown.TryBuild's own doc) - only 4 short options today so it's never
        // actually hit that here, but costs nothing to guard against it growing later.
        Control reasonControl = Sts2NativeDropdown.TryBuild(reasonOptions, reasonOptions[0], OnReasonSelected, panel)?.Control
            ?? BuildFallbackReasonDropdown(reasonOptions, OnReasonSelected);
        // BuildSettingsRow's own default (28, the real settings screen's row-label size) is already bigger than
        // this dialog's own 26px body text, so no override needed here anymore - an earlier pass bumped this to
        // BodyFontSize specifically because the row label's old default (17) read as tiny next to 26px body copy,
        // but that's moot now that the corrected default is 28.
        // Placed above the confirmation question (not below) - the dropdown's own open/close popover expands
        // downward over whatever comes after it, so putting it first keeps that popover clear of the body text
        // instead of overlapping it.
        content.AddChild(BuildSettingsRow(Loc.Get("밴 사유"), reasonControl));
        content.AddChild(new Control { CustomMinimumSize = new Vector2(0f, 12f) });
        content.AddChild(customReasonInput);

        content.AddChild(new Control { CustomMinimumSize = new Vector2(0f, 20f) });

        // Plain Fill (Label's default, no width cap) - wraps at the same right edge as the reason dropdown and
        // Confirm button below, since they all share this same content column's width. An earlier pass capped
        // this narrower to control the wrap point independently, but that made its right edge fall short of
        // everything else's, which read as misaligned rather than intentionally narrower.
        Label bodyLabel = StyleBodyLabel(new Label
        {
            Text = BuildBodyText(targetLabel, alsoKick),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        bodyLabel.AddThemeFontSizeOverride("font_size", BodyFontSize);
        content.AddChild(bodyLabel);

        // The game's own generic dialog "Yes" button (see AddPopupYesButton's own doc) - same button NErrorPopup
        // labels "버그 제보" and a plain NVerticalPopup labels "확인", just relabeled here. Added to
        // PopupBackground (see AddPopupYesButton). Position matches reference/ui/vertical_popup.tscn's own
        // YesButton node exactly: flush against the popup's right edge (offset_right = 0, no inset) and 80px up
        // from the popup's bottom edge (offset_bottom = -80) - that's the real native placement for this exact
        // button, not an approximation, and abandon_run_yes_button.tscn's own custom_minimum_size (180x72) is what
        // OffsetLeft/OffsetTop derive from here.
        Control confirmButton = panel.AddPopupYesButton(Loc.Get("확인"), () =>
        {
            string reason = selectedReason == otherReasonLabel ? customReasonInput.Text : selectedReason;
            NModalContainer.Instance?.Clear();
            onConfirmed(reason);
        });
        confirmButton.SetAnchorsPreset(LayoutPreset.BottomRight);
        confirmButton.OffsetRight = 0f;
        confirmButton.OffsetLeft = confirmButton.OffsetRight - confirmButton.CustomMinimumSize.X;
        confirmButton.OffsetBottom = -80f;
        confirmButton.OffsetTop = confirmButton.OffsetBottom - confirmButton.CustomMinimumSize.Y;
    }

    /// <summary>Rare fallback if Sts2NativeDropdown's scene load fails - a themed plain OptionButton, same as this
    /// row used before switching to the real native dropdown.</summary>
    private static Control BuildFallbackReasonDropdown(string[] options, Action<string> onSelected)
    {
        var reasonOption = new OptionButton();
        foreach (string option in options)
        {
            reasonOption.AddItem(option);
        }
        Control wrapper = StyleAsSettingsDropdown(reasonOption);
        reasonOption.ItemSelected += index => onSelected(reasonOption.GetItemText((int)index));
        return wrapper;
    }

    /// <summary>Builds the multi-line body text with real bullet glyphs baked into each localized template
    /// (Loc.cs's assets/localization.json) instead of a literal "*" - the asterisks in the originally-specified
    /// copy were meant to mark list items, not to be read as literal characters, so a plain Label (no BBCode list
    /// support needed) with an indented "•" prefix on each list line reproduces that intent directly.</summary>
    private static string BuildBodyText(string targetLabel, bool alsoKick)
    {
        string template = alsoKick
            ? Loc.Get("정말로 {0}님을 밴하시겠습니까?\n\n  •  해당 계정은 이 로비에서 즉시 강퇴됩니다.\n  •  밴 등록시 해당 계정과 더 이상 매칭되거나 멀티 플레이를 함께 진행할 수 없습니다.\n  •  이 설정은 차후 밴 목록에서 해제할 수 있습니다.")
            : Loc.Get("정말로 {0}님을 밴 등록하시겠습니까?\n\n  •  밴 등록시 해당 계정과 더 이상 매칭되거나 멀티 플레이를 함께 진행할 수 없습니다.\n  •  이 설정은 차후 밴 목록에서 해제할 수 있습니다.");
        return string.Format(template, targetLabel);
    }
}
