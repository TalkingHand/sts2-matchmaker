using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using Sts2Matchmaker.Matchmaking;
using Steamworks;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Popup for editing matching conditions (community/language/region/mod-match) while already inside a lobby,
/// opened directly from the in-lobby "매칭" button (there is no separate gear/settings button anymore - this
/// window IS what "매칭" opens). Also owns the actual open-to-matching toggle via the game's own bottom-right
/// checkmark "confirm" button (Sts2ModalPanel.AddConfirmButton). Shares MatchSettingsStore with the main matching
/// window, so values set here carry over there too.
///
/// While a search is actually active, the UI mirrors NCharacterSelectScreen's own ready/waiting state (see
/// reference/screens/CharacterSelect/NCharacterSelectScreen.cs OnEmbarkPressed/OnUnreadyPressed): the confirm
/// button and the base close button both slide off-screen, and a second back_button.tscn instance with its icon
/// swapped to the "X" variant (AddCancelButton - same trick as that screen's own "UnreadyButton") slides in from
/// the left in the close button's place. Conditions are frozen behind ContentBlocker for the same duration, since
/// changing community/language/etc after the lobby is already tagged for search would silently desync the two.
/// </summary>
public class MatchConditionsWindow : Sts2ModalPanel
{
    private MatchConditionsPanel _conditions = null!;
    private StartRunLobby _lobby = null!;
    private NConfirmButton? _confirmButton;
    private NBackButton? _cancelButton;
    private bool _isOpenToMatching;
    private Label _statusLabel = null!;
    private Timer _busyDotsTimer = null!;
    private string? _busyStatusBaseText;
    private int _busyDotsCount;

    /// <summary>The single live instance, if this window is currently open (only one modal can be open at once, so
    /// there's never more than one) - lets RecruitToggleInjector's player-limit check force this closed from
    /// outside when the lobby fills up while it happens to be showing, without needing to know anything about
    /// NModalContainer itself. Set in ShowFor, cleared on TreeExiting (fires for every close path).</summary>
    public static MatchConditionsWindow? Current { get; private set; }

    /// <summary>Returns the created panel so callers can observe when it closes (TreeExiting fires regardless of
    /// which close path was used - back button, fallback button, etc.) - used by RecruitToggleInjector to refresh
    /// its own toggle label once this window goes away.</summary>
    public static MatchConditionsWindow ShowFor(StartRunLobby lobby)
    {
        var panel = new MatchConditionsWindow { _lobby = lobby };
        // Tabbed "settings screen" look (real tab strip + bare row list on the darkened backdrop) rather than
        // the painted popup_vertical.tres box our other dialogs use - see Sts2ModalPanel.ShowAsTabbedSettingsScreen.
        // Same three tabs as the main "매칭" window, so ban-list management and the mod-info readout are available
        // from in-lobby too, not just before hosting/joining.
        VBoxContainer[] pages = panel.ShowAsTabbedSettingsScreen(new[] { "매칭 설정", "모드 정보", "밴 목록" });

        // ShowGameMode: false - StartRunLobby.GameMode has no setter, so it's genuinely fixed for this already-open
        // room. ShowMaxPlayers: true - unlike GameMode, this is NOT StartRunLobby.MaxPlayers (also fixed, the
        // room's real capacity) but a separate "매칭 목표 인원수" search-stop threshold, meaningful and editable
        // here regardless of the room's actual size (see MatchConditionsPanel's own class doc).
        // currentLobbyPlayerCount: lobby.Players.Count - excludes any "인원 수" option the room has already met or
        // passed (see MatchConditionsPanel's own constructor doc), a snapshot taken once here at open time rather
        // than tracked live while the window stays open.
        panel._conditions = new MatchConditionsPanel(showGameMode: false, showMaxPlayers: true, currentLobbyPlayerCount: lobby.Players.Count, overlayParent: panel);
        pages[0].AddChild(panel._conditions);
        panel._conditions.Build();
        panel._conditions.LoadFrom(MatchSettingsStore.Load());

        panel._isOpenToMatching = ComputeIsOpenToMatching(lobby);
        panel._confirmButton = panel.AddConfirmButton("매칭 시작", panel.OnTogglePressed);
        panel._cancelButton = panel.AddCancelButton("매칭 취소", panel.OnTogglePressed);
        // Reflects whichever state we actually started in, not just the freshly-opened case - re-opening this
        // window while a search from an earlier press is still active should show the cancel state immediately,
        // not the confirm state.
        panel.ApplyMatchingUiState();

        // Top-level (not part of the scrollable page) so it stays visible above ContentBlocker, same positioning
        // and StyleSettingsRowLabel(28px) styling as MatchmakingWindow's own equivalent status label - this window
        // had none at all before, so a search started/cancelled here gave no feedback whatsoever.
        panel._statusLabel = StyleSettingsRowLabel(new Label { Text = string.Empty, HorizontalAlignment = HorizontalAlignment.Center });
        panel._statusLabel.AddThemeFontSizeOverride("font_size", 28);
        panel._statusLabel.SetAnchorsPreset(LayoutPreset.BottomWide);
        panel._statusLabel.OffsetTop = -150f;
        panel._statusLabel.OffsetBottom = -110f;
        panel.AddChild(panel._statusLabel);

        panel._busyDotsTimer = new Timer { WaitTime = 0.5, Autostart = false };
        panel._busyDotsTimer.Timeout += panel.OnBusyDotsTick;
        panel.AddChild(panel._busyDotsTimer);

        // Reflects whatever state ApplyMatchingUiState just did above, for the same re-opening-mid-search reason.
        if (panel._isOpenToMatching)
        {
            panel.SetBusyStatus("참가 상대를 기다리는 중");
        }

        var modInfo = new ModInfoPanel();
        pages[1].AddChild(modInfo);
        modInfo.Build();

        var banList = new BanListPanel(lobby);
        pages[2].AddChild(banList);
        banList.Build();

        Current = panel;
        panel.TreeExiting += () =>
        {
            if (Current == panel)
            {
                Current = null;
            }
        };

        return panel;
    }

    /// <summary>Force-closes this window if (and only if) it's the one currently open - a no-op otherwise. Used by
    /// RecruitToggleInjector when the lobby fills up mid-search: the matching-conditions screen showing a "매칭
    /// 취소" state the player can no longer act on (searching already auto-stopped) would be confusing left open.</summary>
    public static void CloseIfOpen()
    {
        if (Current != null && GodotObject.IsInstanceValid(Current))
        {
            NModalContainer.Instance?.Clear();
        }
    }

    /// <summary>Same "is this lobby currently tagged as open" read used by RecruitToggleInjector to seed its own
    /// label - kept in one place so both callers agree on what "open" means.</summary>
    public static bool ComputeIsOpenToMatching(StartRunLobby lobby)
    {
        string? rawLobbyId = lobby.NetService.GetRawLobbyIdentifier();
        return rawLobbyId != null
            && ulong.TryParse(rawLobbyId, out ulong lobbyIdValue)
            && SteamMatchmaking.GetLobbyData(new CSteamID(lobbyIdValue), MatchTags.ActiveKey) == MatchTags.ActiveValue;
    }

    private void OnTogglePressed()
    {
        string? rawLobbyId = _lobby.NetService.GetRawLobbyIdentifier();
        if (rawLobbyId == null || !ulong.TryParse(rawLobbyId, out ulong lobbyIdValue))
        {
            Log.Error("[sts2_matchmaker] Could not read raw lobby id when toggling matchmaking visibility");
            return;
        }
        var lobbyId = new CSteamID(lobbyIdValue);
        MatchSettings settings = SaveCurrentSettings();

        if (!_isOpenToMatching)
        {
            string language = string.IsNullOrEmpty(settings.Language) ? (LocManager.Instance?.Language ?? string.Empty) : settings.Language;
            // settings.MaxPlayers (the "인원 수" configured on this very screen), resolved through
            // ResolveRoomMaxPlayers - NOT used as-is, since it can be MatchTags.MaxPlayersAny ("무관", only offered
            // here, never in the host-capable main matching popup - see MatchConditionsPanel's own class doc) or a
            // real pick that still needs clamping to what the room can actually hold (_lobby.MaxPlayers, the room's
            // real fixed-at-creation capacity - StartRunLobby.MaxPlayers has no setter). See
            // RecruitToggleInjector.CheckPlayerLimitNow, which resolves the same saved setting the same way for the
            // auto-stop-when-full check.
            MatchLobbyTagging.ApplyTags(lobbyId, settings.Community, language, _lobby.GameMode,
                MatchLobbyTagging.ResolveRoomMaxPlayers(_lobby.MaxPlayers, settings.MaxPlayers), MatchTags.KindFresh,
                MatchLobbyTagging.ResolveRoomAscension(_lobby.MaxAscension, settings.Ascension));
            _ = BanTagSync.MergeMyBansIntoLobbyAsync(lobbyId);
            Log.Info($"[sts2_matchmaker] Opened existing lobby {lobbyIdValue} to matchmaking search");
            // This window only ever exists for a room that already exists (host already committed) - being open to
            // search here always genuinely means waiting for others to find and join it, unlike MatchmakingWindow's
            // own "매칭 시도 중" where the outcome (join vs host) isn't decided yet.
            SetBusyStatus("참가 상대를 기다리는 중");
        }
        else
        {
            MatchLobbyTagging.RemoveFromSearch(lobbyId);
            Log.Info($"[sts2_matchmaker] Closed lobby {lobbyIdValue} from matchmaking search");
            SetFinalStatus("매칭 취소됨");
        }
        _isOpenToMatching = !_isOpenToMatching;
        ApplyMatchingUiState();
    }

    private void ApplyMatchingUiState() => ApplyConfirmCancelState(_isOpenToMatching, _confirmButton, _cancelButton);

    /// <summary>Sets the status label to an in-progress message with an animated "." x1-3 suffix that cycles every
    /// half second - mirrors MatchmakingWindow's own SetBusyStatus/SetFinalStatus/OnBusyDotsTick trio exactly.</summary>
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

    /// <summary>Loads the persisted settings (rather than starting from a blank MatchSettings like
    /// MatchmakingWindow's own SaveCurrentSettings) because this narrower panel never shows the game-mode/host
    /// fields (see MatchConditionsPanel's own class doc for why game mode specifically stays hidden here) -
    /// reloading first preserves whatever the main matching window last set for those.</summary>
    private MatchSettings SaveCurrentSettings()
    {
        MatchSettings settings = MatchSettingsStore.Load();
        _conditions.ApplyTo(settings);
        MatchSettingsStore.Save(settings);
        return settings;
    }

    protected override void OnCloseRequested()
    {
        SaveCurrentSettings();
        // Must stay open for the whole duration of an active search - ApplyConfirmCancelState already disables
        // CloseButton while _isOpenToMatching (so a normal click can't reach here), but this is a defensive second
        // layer against any OTHER path that might call OnCloseRequested directly (e.g. an engine-level cancel/ESC
        // input action bound globally, not gated on any one button's own enabled state). The real way out of an
        // active search is the in-window "매칭 취소" cancel button (OnTogglePressed), which flips
        // _isOpenToMatching back to false and re-enables this same close path immediately after.
        if (_isOpenToMatching)
        {
            return;
        }
        base.OnCloseRequested();
    }
}
