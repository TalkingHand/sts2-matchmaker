using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;
using Sts2Matchmaker.Localization;
using Sts2Matchmaker.Matchmaking;
using Sts2Matchmaker.UI;
using Steamworks;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Register-only ban (no kick, matches the removed in-run version) from the 도전 이력 (run history) screen -
/// confirmed via decompiling NRunHistory/NRunHistoryPlayerIcon directly (not in reference/, since this was a
/// quick lookup rather than something worth keeping around for repeated reference). NRunHistory already lets you
/// click through each past co-op teammate's portrait (%PlayerIconContainer) to inspect their deck/relics one at a
/// time (DisplayRun/SelectPlayer) - selecting a different player effectively swaps the whole screen's content
/// (potions/badges/relics/deck all reload for them), so this reads as "ban the player this page is currently
/// showing" rather than "ban a specific portrait in a row".
///
/// Positioned below %BuildLabel (the faded "BuildId: ..." line, bottom of the date/seed/mode/build text column -
/// see reference/screens/run_history_screen/run_history.tscn lines 77-185), right-aligned with that same column
/// (every entry in it, including BuildLabel, is right-aligned within one fixed-width VBox, so BuildLabel's own
/// right edge doubles as the whole column's). That column lives inside ScreenContents/Content/Container (the
/// scrollable page body, via a Container-managed HBox/VBox), NOT as a fixed screen-space overlay the way
/// %ShareButton/BackButton do (direct children of the screen root) - an earlier version of this patch anchored
/// next to %ShareButton instead using its raw Anchor/Offset values, but a Container child's actual on-screen rect
/// comes from the layout engine, not from reading those properties directly, which is why that version rendered
/// off - centered against ShareButton's full icon+label box rather than its visible icon glyph. This version reads
/// GlobalPosition/Size instead (the layout engine's actual computed result, correct for any container), deferred
/// one frame past _Ready via CallDeferred so the TopSection layout has actually settled before we read it (same
/// reasoning as RecruitToggleInjector's own KeepLastDeferred).
/// Falls back to %PlayerIconContainer if %BuildLabel is missing, so the ban feature never disappears.
/// </summary>
[HarmonyPatch(typeof(NRunHistory))]
public static class RunHistoryPlayerBanPatch
{
    /// <summary>Gap (px) from the badge row's right edge / build label's bottom edge to the ban button.</summary>
    private const float Gap = 16f;

    private const string BanButtonName = "Sts2MatchmakerRunHistoryBanButton";
    private static string RegisterTooltip => Loc.Get("현재 보고 있는 플레이어 밴 등록");

    [HarmonyPatch("_Ready")]
    [HarmonyPostfix]
    public static void ReadyPostfix(NRunHistory __instance)
    {
        try
        {
            // Explicit hex red (matches RemoteLobbyPlayerKickPatch's kick/ban/register buttons exactly) rather than
            // a hue/sat/value shader approximation - the earlier hue=0.6 approximation rendered too pink/magenta
            // against StyleBoxFlat's flat fill. BuildTextActionButton, same as RemoteLobbyPlayerKickPatch's own
            // "밴 등록" button - this is the same register-only, no-kick action, just triggered from a different
            // screen, and BanRegisterButtonHelper sets its actual "밴 등록"/"밴 해제" text below.
            Button banButton = Sts2ModalPanel.BuildTextActionButton(Loc.Get("밴 등록"), 50f, explicitColor: new Color("991816"));
            // Named for lookup from SelectPlayerPostfix below (a separate Harmony postfix, so it can't just close
            // over this local variable) - same "find our own injected node by name" idea RecruitToggleInjector uses.
            banButton.Name = BanButtonName;
            banButton.Pressed += () =>
            {
                NRunHistoryPlayerIcon? selected = Traverse.Create(__instance).Field("_selectedPlayerIcon").GetValue<NRunHistoryPlayerIcon>();
                RunHistoryPlayer? player = selected?.Player;
                if (player == null)
                {
                    return;
                }
                ulong targetId = player.Id;
                if (targetId == SteamUser.GetSteamID().m_SteamID)
                {
                    return; // can't ban yourself - shouldn't be reachable anyway since the button is hidden then
                }

                if (BanListStore.IsBanned(targetId))
                {
                    BanListStore.Remove(targetId);
                    Log.Info($"[sts2_matchmaker] Removed ban registration for peer {targetId} from run history");
                    BanRegisterButtonHelper.UpdateAppearance(banButton, targetId, RegisterTooltip);
                    return;
                }

                string nickname = BanListStore.ResolveNickname(targetId);
                string targetLabel = string.IsNullOrEmpty(nickname) ? targetId.ToString() : nickname;
                BanConfirmPanel.Show(targetLabel, alsoKick: false, reason =>
                {
                    Log.Info($"[sts2_matchmaker] Registering ban for peer {targetId} from run history (reason: {reason}), no kick");
                    BanListStore.Add(targetId, nickname, reason);
                    BanRegisterButtonHelper.UpdateAppearance(banButton, targetId, RegisterTooltip);
                });
            };

            __instance.AddChild(banButton);
            // Hidden until PositionBanButton places it AND SelectPlayerPostfix confirms the currently-focused
            // player isn't ourselves - avoids a one-frame flash at (0,0) (top-left) before the deferred call runs.
            banButton.Visible = false;
            Callable.From(() => PositionBanButton(__instance, banButton)).CallDeferred();
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to inject run-history ban button: {ex}");
        }
    }

    /// <summary>
    /// Fires every time a different player's portrait is selected (DisplayRun calls this once up front too, for
    /// whichever player is shown first) - the only place that actually knows who's focused right now, so this is
    /// where visibility gets decided rather than recomputing it ad hoc elsewhere. Banning yourself doesn't mean
    /// anything, so the button simply isn't there while your own entry is focused.
    /// </summary>
    [HarmonyPatch("SelectPlayer")]
    [HarmonyPostfix]
    public static void SelectPlayerPostfix(NRunHistory __instance, NRunHistoryPlayerIcon playerIcon)
    {
        try
        {
            Button? banButton = __instance.GetNodeOrNull<Button>(BanButtonName);
            if (banButton == null)
            {
                return; // not positioned yet (or injection failed) - PositionBanButton will set the right state
            }
            ulong targetId = playerIcon.Player.Id;
            banButton.Visible = targetId != SteamUser.GetSteamID().m_SteamID;
            if (banButton.Visible)
            {
                BanRegisterButtonHelper.UpdateAppearance(banButton, targetId, RegisterTooltip);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to update run-history ban button visibility: {ex}");
        }
    }

    private static void PositionBanButton(NRunHistory instance, Button banButton)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(banButton))
            {
                return;
            }
            // %BuildLabel is the bottom-most entry in the date/seed/mode/build text column (RightAlignedStuff) -
            // every entry in that column is right-aligned within the same fixed-width VBox, so BuildLabel's own
            // right edge is also the whole column's right edge, no separate node needed for that.
            Control? buildLabel = instance.GetNodeOrNull<Control>("%BuildLabel");
            if (buildLabel != null)
            {
                float columnRightEdge = buildLabel.GlobalPosition.X + buildLabel.Size.X;
                // banButton.Size.X (its actual current rendered width), not CustomMinimumSize.X - unlike the old
                // fixed-square icon button, BuildTextActionButton auto-sizes to its own text ("밴 등록" vs "밴
                // 해제" aren't necessarily the same width), so CustomMinimumSize.X (0, unset) wouldn't reflect
                // what's actually on screen.
                banButton.GlobalPosition = new Vector2(
                    columnRightEdge - banButton.Size.X,
                    buildLabel.GlobalPosition.Y + buildLabel.Size.Y + Gap);
            }
            else
            {
                Log.Error("[sts2_matchmaker] Could not find %BuildLabel on NRunHistory - falling back to %PlayerIconContainer");
                Control? playerIconContainer = instance.GetNodeOrNull<Control>("%PlayerIconContainer");
                if (playerIconContainer == null)
                {
                    Log.Error("[sts2_matchmaker] Could not find %PlayerIconContainer fallback either - ban button not shown");
                    return;
                }
                banButton.GetParent()?.RemoveChild(banButton);
                playerIconContainer.AddChild(banButton);
            }

            // Visibility is otherwise owned by SelectPlayerPostfix, but that may well have already fired (DisplayRun
            // selects the initial player synchronously during _Ready-time setup, before this deferred call runs) -
            // re-derive the same self/other check here from current selection so whichever of the two runs last
            // still lands on the correct state instead of one blindly overwriting the other with "visible".
            NRunHistoryPlayerIcon? selected = Traverse.Create(instance).Field("_selectedPlayerIcon").GetValue<NRunHistoryPlayerIcon>();
            ulong? selectedId = selected?.Player.Id;
            banButton.Visible = selectedId != SteamUser.GetSteamID().m_SteamID;
            if (banButton.Visible && selectedId.HasValue)
            {
                BanRegisterButtonHelper.UpdateAppearance(banButton, selectedId.Value, RegisterTooltip);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to position run-history ban button: {ex}");
        }
    }
}
