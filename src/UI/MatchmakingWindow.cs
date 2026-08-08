using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Sts2Matchmaker.Localization;
using Sts2Matchmaker.Matchmaking;
using Steamworks;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Matching popup, styled like the game's own popups (see Sts2ModalPanel) instead of a raw Godot Window. Picks
/// matching conditions (community/language/region/game mode/player-count, via MatchConditionsPanel) plus
/// host-capable, and hits the bottom-right confirm (check) button - searches first, becomes host only if nobody was
/// found and "호스트 허용" is checked. Always also shows "재모집 대기" (wait for a teammate to re-host a broken
/// multiplayer run, then auto-join) - unlike "재모집 시작" (which actually needs the local save to rehost FROM,
/// so it lives with the vanilla "매칭" toggle inside an already-hosted lobby instead - see RecruitToggleInjector),
/// waiting needs no save of your own, just the host's participant list to show up in (see
/// AutoMatchService.WaitForRehostAsync). Styled after settings_screen.tscn's own "Send Feedback" row
/// (MarginContainer label + pill button) rather than a bare Button, to read as a proper settings-screen row like
/// everything else on this page instead of standing out as an ad-hoc control.
///
/// Same confirm/cancel/content-blocker pattern as MatchConditionsWindow (see that class's own doc comment and
/// NCharacterSelectScreen's OnEmbarkPressed/OnUnreadyPressed): while an auto-match search or a rehost-wait is
/// pending, the confirm button and the base close button slide off-screen, the "X" cancel button takes the close
/// button's place, and the conditions/host-checkbox/rehost row are frozen behind ContentBlocker - _pendingCts is
/// shared between auto-match and rehost-wait (only one such wait makes sense at a time), and the cancel button
/// just cancels whichever one is running. The status label lives outside that frozen area (added directly to
/// "this", not into the scrollable page) so "매칭 시도 중..." stays visible while everything under it is dimmed.
///
/// Once hosting begins, this popup closes - the vanilla lobby screen takes over, and per-player kick buttons plus
/// an "매칭에 공개" toggle are injected directly into it (see Patches/*), so there's no need to keep a separate
/// management window open.
/// </summary>
public class MatchmakingWindow : Sts2ModalPanel
{
    private NSubmenuStack _stack = null!;

    private MatchConditionsPanel _conditions = null!;
    private CheckBox _canHostCheck = null!;
    private NConfirmButton? _confirmButton;
    private NBackButton? _cancelButton;
    private Button _rehostWaitButton = null!;
    private Label _statusLabel = null!;
    // Fully qualified: this file has `using System.Threading;` for CancellationToken, and System.Threading.Timer
    // would otherwise make the bare name ambiguous.
    private Godot.Timer _busyDotsTimer = null!;
    private string? _busyStatusBaseText;
    private int _busyDotsCount;

    // Only one long-running wait (auto-match or rehost-wait) makes sense at a time - starting one cancels the
    // other, and the single "X" cancel button always cancels whichever one is currently set.
    private CancellationTokenSource? _pendingCts;

    public static void ShowFor(Node _, NSubmenuStack stack)
    {
        var panel = new MatchmakingWindow { _stack = stack };
        // Tabbed "settings screen" look, matching MatchConditionsWindow - see Sts2ModalPanel.ShowAsTabbedSettingsScreen.
        // Ban list used to be its own popup (closing this window to open it, since only one modal can be open at
        // once) and the mod list was a show/hide toggle at the bottom of the conditions form - both are now just
        // tabs, so switching to them no longer interrupts an in-progress search/host flow.
        VBoxContainer[] pages = panel.ShowAsTabbedSettingsScreen(new[] { Loc.Get("매칭 설정"), Loc.Get("모드 정보"), Loc.Get("밴 목록") });
        panel.BuildContent(pages[0]);

        var modInfo = new ModInfoPanel();
        pages[1].AddChild(modInfo);
        modInfo.Build();

        var banList = new BanListPanel();
        pages[2].AddChild(banList);
        banList.Build();
    }

    private void BuildContent(VBoxContainer page)
    {
        _conditions = new MatchConditionsPanel(showGameMode: true, showMaxPlayers: true, overlayParent: this);
        page.AddChild(_conditions);
        _conditions.Build();

        _canHostCheck = new CheckBox();
        StyleAsSettingsCheckbox(_canHostCheck);
        page.AddChild(BuildSettingsRow(Loc.Get("호스트 허용"), _canHostCheck, tooltip: Loc.Get("일치하는 로비가 없으면 자동으로 호스트가 됩니다."), overlayParent: this));
        page.AddChild(BuildSettingsDivider());

        MatchSettings saved = MatchSettingsStore.Load();
        _conditions.LoadFrom(saved);
        _canHostCheck.ButtonPressed = saved.CanHost;

        // Row style matches settings_screen.tscn's own "Send Feedback" row exactly now (BuildSettingsRow: label
        // left, button right in a 64px MarginContainer; BuildSettingsActionButton: same reward_skip_button.png
        // background/shader/label styling as that row's own button, not the flat-color BuildTextActionButton
        // pill used for kick/ban) - needs no hasSave gate the way "재모집 시작" does (see this class's own doc),
        // so it's just always here like any other settings row.
        // 320x64 - FeedbackButton's own exact native size, not a shrunk-down guess. The texture's real aspect
        // ratio only renders correctly (via KeepAspectCentered) when the box it's centered in is close to that
        // same aspect - an arbitrarily smaller/narrower box was the other half of why this looked "off".
        _rehostWaitButton = BuildSettingsActionButton(Loc.Get("대기"), new Vector2(320f, 64f), out _);
        WireHoverBrightness(_rehostWaitButton);
        _rehostWaitButton.Pressed += OnRehostWaitPressed;
        page.AddChild(BuildSettingsRow(Loc.Get("이어하기"), _rehostWaitButton, tooltip: Loc.Get("이 모드를 설치한 호스트가 중단된 멀티플레이 런을 다시 열면 자동으로 접속합니다."), overlayParent: this));
        page.AddChild(BuildSettingsDivider());

        _confirmButton = AddConfirmButton(Loc.Get("매칭 시작"), OnConfirmPressed);
        _cancelButton = AddCancelButton(Loc.Get("매칭 취소"), OnCancelPressed);

        // Top-level (not part of the scrollable page) so it stays visible above ContentBlocker instead of being
        // dimmed along with the conditions/rehost row it's reporting status for. StyleSettingsRowLabel (28px cream
        // + shadow), not StyleBodyLabel's outline treatment - matches the "매칭 설정" screen's own row-label
        // typography (see BuildSettingsRow's own doc) rather than the painted-popup body-copy style that's a
        // holdover from before this window switched to the tabbed settings-screen look.
        _statusLabel = StyleSettingsRowLabel(new Label { Text = string.Empty, HorizontalAlignment = HorizontalAlignment.Center });
        _statusLabel.AddThemeFontSizeOverride("font_size", 28);
        _statusLabel.SetAnchorsPreset(LayoutPreset.BottomWide);
        _statusLabel.OffsetTop = -150f;
        _statusLabel.OffsetBottom = -110f;
        AddChild(_statusLabel);

        _busyDotsTimer = new Godot.Timer { WaitTime = 0.5, Autostart = false };
        _busyDotsTimer.Timeout += OnBusyDotsTick;
        AddChild(_busyDotsTimer);

        ApplyPendingUiState();
    }

    /// <summary>Sets the status label to an in-progress message with an animated "." x1-3 suffix that cycles every
    /// half second, so a long search/wait doesn't look like the UI has frozen.</summary>
    private void SetBusyStatus(string baseText)
    {
        _busyStatusBaseText = baseText;
        _busyDotsCount = 1;
        _statusLabel.Text = baseText + ".";
        _busyDotsTimer.Start();
    }

    /// <summary>Sets the status label to a final (non-animated) message and stops the dots animation.</summary>
    private void SetFinalStatus(string text)
    {
        _busyStatusBaseText = null;
        _busyDotsTimer.Stop();
        _statusLabel.Text = text;
    }

    private void OnBusyDotsTick()
    {
        if (_busyStatusBaseText == null)
        {
            return;
        }
        _busyDotsCount = (_busyDotsCount % 3) + 1;
        _statusLabel.Text = _busyStatusBaseText + new string('.', _busyDotsCount);
    }

    private void SaveCurrentSettings()
    {
        MatchSettings settings = new();
        _conditions.ApplyTo(settings);
        settings.CanHost = _canHostCheck.ButtonPressed;
        MatchSettingsStore.Save(settings);
    }

    private void ApplyPendingUiState() => ApplyConfirmCancelState(_pendingCts != null, _confirmButton, _cancelButton);

    protected override void OnCloseRequested()
    {
        _pendingCts?.Cancel();
        SaveCurrentSettings();
        base.OnCloseRequested();
    }

    // --- Unified auto-match (search, else host if allowed) ---

    private void OnConfirmPressed()
    {
        if (_pendingCts != null)
        {
            return;
        }
        SaveCurrentSettings();
        _pendingCts = new CancellationTokenSource();
        // Always "매칭 시도 중", regardless of "호스트 허용": this popup always searches first and its outcome
        // (join vs host) isn't known yet at the moment this is set - "참가 상대를 기다리는 중" belongs only to
        // MatchConditionsWindow, where a room already exists and being open-to-search genuinely means waiting for
        // others to join it.
        SetBusyStatus(Loc.Get("매칭 시도 중"));
        ApplyPendingUiState();
        _ = AutoMatchAsync(_pendingCts.Token);
    }

    private void OnCancelPressed()
    {
        _pendingCts?.Cancel();
        _pendingCts = null;
        SetFinalStatus(Loc.Get("매칭 취소됨"));
        ApplyPendingUiState();
    }

    private async Task AutoMatchAsync(CancellationToken cancelToken)
    {
        try
        {
            var settings = new MatchSettings();
            _conditions.ApplyTo(settings);
            settings.CanHost = _canHostCheck.ButtonPressed;

            AutoMatchResult result = await AutoMatchService.AutoMatchAsync(_stack, settings, cancelToken);
            HandleAutoMatchResult(result);
        }
        catch (OperationCanceledException)
        {
            // user pressed 취소 - already handled by OnCancelPressed's second click.
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Unhandled exception during auto-match: {ex}");
            if (IsInstanceValid(this))
            {
                SetFinalStatus(string.Format(Loc.Get("매칭 중 예외: {0}"), ex.Message));
            }
        }
        finally
        {
            if (IsInstanceValid(this))
            {
                _pendingCts = null;
                ApplyPendingUiState();
            }
        }
    }

    private void HandleAutoMatchResult(AutoMatchResult result)
    {
        if (!IsInstanceValid(this))
        {
            return;
        }

        switch (result.Outcome)
        {
            case AutoMatchOutcome.JoinExisting:
                SetBusyStatus(Loc.Get("발견된 로비에 접속 중"));
                _ = JoinAsync(result.LobbyId!.Value);
                break;
            case AutoMatchOutcome.BecameHost:
                // Hosting started successfully - hand off to the vanilla lobby screen and close this popup.
                NModalContainer.Instance?.Clear();
                break;
            case AutoMatchOutcome.Error:
            default:
                SetFinalStatus(result.Error ?? Loc.Get("매칭 실패"));
                break;
        }
    }

    // --- Rehost wait (participant side only - see this class's own doc for why "재모집 시작" isn't here) ---

    private void OnRehostWaitPressed()
    {
        if (_pendingCts != null)
        {
            return;
        }
        SaveCurrentSettings();
        _pendingCts = new CancellationTokenSource();
        SetBusyStatus(Loc.Get("팀원이 이어하기를 열 때까지 대기 중"));
        ApplyPendingUiState();
        _ = RehostWaitAsync(_pendingCts.Token);
    }

    private async Task RehostWaitAsync(CancellationToken cancelToken)
    {
        try
        {
            AutoMatchResult result = await AutoMatchService.WaitForRehostAsync(cancelToken);
            if (IsInstanceValid(this) && result.Outcome == AutoMatchOutcome.JoinExisting)
            {
                SetBusyStatus(Loc.Get("이어하기 로비 발견, 접속 중"));
                _ = JoinAsync(result.LobbyId!.Value);
            }
        }
        catch (OperationCanceledException)
        {
            // user pressed 취소.
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Unhandled exception during rehost wait: {ex}");
            if (IsInstanceValid(this))
            {
                SetFinalStatus(string.Format(Loc.Get("이어하기 대기 중 예외: {0}"), ex.Message));
            }
        }
        finally
        {
            if (IsInstanceValid(this))
            {
                _pendingCts = null;
                ApplyPendingUiState();
            }
        }
    }

    private async Task JoinAsync(CSteamID lobbyId)
    {
        // Close our own popup before attempting the join, not after - NModalContainer only holds one modal at a
        // time, so if the join fails and the game tries to show its own native error dialog, it would otherwise
        // silently no-op while we're still open, leaving the player looking at nothing. See JoinFailurePanel.
        NModalContainer.Instance?.Clear();
        try
        {
            await MatchJoinService.JoinLobbyAsync(_stack, lobbyId);
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to join lobby {lobbyId.m_SteamID}: {ex}");
            JoinFailurePanel.Show(string.Format(Loc.Get("로비 참가에 실패했습니다.\n\n{0}"), ex.Message));
        }
    }
}
