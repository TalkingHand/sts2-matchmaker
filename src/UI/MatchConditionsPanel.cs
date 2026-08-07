using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Runs;
using Sts2Matchmaker.Matchmaking;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Shared matching-condition inputs (community, language, game mode, player count, mod-match) used both by the
/// main matching popup and by the in-lobby "매칭" toggle. Game mode and player count are independently gated
/// (ShowGameMode / ShowMaxPlayers), not a single combined flag - an already-open room's actual GAME MODE really is
/// fixed (StartRunLobby.GameMode has no setter), so the in-lobby window hides that row, but its PLAYER COUNT here
/// is a separate "매칭 목표 인원수" (search-stop threshold), not StartRunLobby.MaxPlayers itself (also no setter,
/// fixed to whatever the room's real capacity was at creation) - so it stays meaningful and editable in-lobby too,
/// defaulting to whatever was last saved (MatchHostService.DefaultMaxPlayers on a first run). See
/// RecruitToggleInjector.CheckPlayerLimitNow and MatchConditionsWindow.OnTogglePressed, which both compare/tag
/// against this saved target rather than the lobby's own fixed capacity.
/// </summary>
public class MatchConditionsPanel : VBoxContainer
{
    /// <summary>Friendly names for the game's most common language codes (MegaCrit.Sts2.Core.Localization.
    /// LocManager.Languages, ISO 639-2 3-letter codes) - anything not listed just shows its raw code.</summary>
    private static readonly Dictionary<string, string> FriendlyLanguageNames = new()
    {
        ["eng"] = "English",
        ["kor"] = "한국어",
        ["jpn"] = "日本語",
        ["chi"] = "中文",
        ["zho"] = "中文",
        ["fre"] = "Français",
        ["fra"] = "Français",
        ["ger"] = "Deutsch",
        ["deu"] = "Deutsch",
        ["spa"] = "Español",
        ["rus"] = "Русский",
        ["por"] = "Português",
        ["ita"] = "Italiano",
        ["pol"] = "Polski",
        ["ukr"] = "Українська",
        ["tha"] = "ไทย",
    };

    private LineEdit _communityInput = null!;
    private NativeDropdownField<string> _languageField = null!;
    private NativeDropdownField<GameMode>? _gameModeField;
    private NativeDropdownField<int>? _maxPlayersField;
    private NativeDropdownField<int>? _ascensionField;

    public bool ShowGameMode { get; }
    public bool ShowMaxPlayers { get; }
    private readonly int _currentLobbyPlayerCount;
    private readonly Control? _overlayParent;

    /// <param name="currentLobbyPlayerCount">How many players are ALREADY in the room right now (0 when there's no
    /// room yet, e.g. MatchmakingWindow picking a target before hosting even starts). Options at or below this
    /// count are dropped from the "인원 수" list entirely (not just visually disabled - see BuildOrFallback's own
    /// call site below for why) since a search target the room has already met or passed isn't a real choice
    /// anymore; the smallest remaining option becomes the new default whenever the previously-selected/saved one
    /// no longer qualifies (NativeDropdownField.SetValue's own fallback-to-first-option rule already does this).</param>
    /// <param name="overlayParent">Forwarded to every NativeDropdownField.BuildOrFallback call below - see
    /// Sts2NativeDropdown.TryBuild's own doc. Should be the enclosing Sts2ModalPanel ("this" from
    /// MatchmakingWindow/MatchConditionsWindow), which sits OUTSIDE the ScrollContainer both of those wrap their
    /// row content in - without it, a long open list (승천 단계's up to 12 options, this panel's own tallest) can
    /// get silently clipped by that ScrollContainer partway through, with no way to reach the rest.</param>
    public MatchConditionsPanel(bool showGameMode, bool showMaxPlayers, int currentLobbyPlayerCount = 0, Control? overlayParent = null)
    {
        ShowGameMode = showGameMode;
        ShowMaxPlayers = showMaxPlayers;
        _currentLobbyPlayerCount = currentLobbyPlayerCount;
        _overlayParent = overlayParent;
    }

    public void Build()
    {
        // 320x64 - matches the native select rows' own settings_dropdown.tscn size, so this row's control column
        // lines up with every other row instead of looking noticeably narrower/shorter than its neighbors.
        _communityInput = new LineEdit
        {
            PlaceholderText = "예: myguild",
            CustomMinimumSize = new Vector2(320f, 64f),
        };
        Sts2ModalPanel.StyleInput(_communityInput);
        AddChild(Sts2ModalPanel.BuildSettingsRow("커뮤니티명", _communityInput, tooltip: "내용이 일치하는 대상과 매칭됩니다.", overlayParent: _overlayParent));
        AddChild(Sts2ModalPanel.BuildSettingsDivider());

        var languageOptions = new List<(string Label, string Value)> { ("언어 무관 (전체)", string.Empty) };
        string currentLanguage = LocManager.Instance?.Language ?? string.Empty;
        List<string> allLanguages = LocManager.Languages ?? new List<string>();
        foreach (string code in allLanguages)
        {
            string label = FriendlyLanguageNames.TryGetValue(code, out string? friendly) ? friendly : code;
            languageOptions.Add((label, code));
        }
        _languageField = NativeDropdownField<string>.BuildOrFallback(languageOptions, currentLanguage, overlayParent: _overlayParent);
        AddChild(Sts2ModalPanel.BuildSettingsRow("언어", _languageField.Control));
        AddChild(Sts2ModalPanel.BuildSettingsDivider());

        // Order from here down is deliberately 런 종류 -> 승천 단계 -> 인원 수 (then, outside this panel entirely,
        // MatchmakingWindow's own 호스트 허용 row right after Build() returns) - not the order these were
        // originally added in.
        if (ShowGameMode)
        {
            var gameModeOptions = new List<(string Label, GameMode Value)>
            {
                ("스탠다드", GameMode.Standard),
                ("데일리", GameMode.Daily),
                ("커스텀", GameMode.Custom),
            };
            _gameModeField = NativeDropdownField<GameMode>.BuildOrFallback(gameModeOptions, GameMode.Standard, _ => UpdateMaxPlayersEnabled(), _overlayParent);
            AddChild(Sts2ModalPanel.BuildSettingsRow("런 종류", _gameModeField.Control));
            AddChild(Sts2ModalPanel.BuildSettingsDivider());
        }

        BuildAscensionRow();

        if (ShowMaxPlayers)
        {
            // Highest first, matching BuildAscensionRow's own ordering (and reasoning) below. Options at or below
            // _currentLobbyPlayerCount are excluded outright, not shown-but-disabled - the native dropdown item
            // scene has no confirmed per-item disable API to fall back on safely (unlike the whole-field Disabled
            // this class already uses for the Daily case below), and dropping the option achieves the same "can't
            // select it" requirement without needing one.
            var maxPlayersOptions = new List<(string Label, int Value)>();
            for (int n = MatchHostService.MaxPlayers; n >= MatchHostService.MinPlayers; n--)
            {
                if (n > _currentLobbyPlayerCount)
                {
                    maxPlayersOptions.Add(($"{n}인", n));
                }
            }
            if (maxPlayersOptions.Count == 0)
            {
                // Room is already at (or someone reported past) MatchHostService.MaxPlayers - every real option
                // would otherwise be filtered out, which BuildOrFallback isn't meant to handle gracefully. Falls
                // back to the unfiltered list rather than leaving this row silently absent - functionally moot
                // anyway once the room's actually full (CheckPlayerLimitNow closes the search immediately).
                for (int n = MatchHostService.MaxPlayers; n >= MatchHostService.MinPlayers; n--)
                {
                    maxPlayersOptions.Add(($"{n}인", n));
                }
            }
            // "무관" only when ShowGameMode - i.e. only in the host-capable main matching popup (MatchmakingWindow),
            // NOT the already-hosted-lobby context (MatchConditionsWindow). That one's room already exists at a
            // fixed real capacity (StartRunLobby.MaxPlayers), so picking "무관" there would resolve to the exact
            // same target as just picking that room's own real size directly (see
            // MatchLobbyTagging.ResolveRoomMaxPlayers) - a redundant option, not a meaningfully different one.
            // In MatchmakingWindow, "무관" is a genuinely different choice: AutoMatchService.AutoMatchAsync applies
            // it as "no maxPlayers filter at all" while SEARCHING (any room size is fine), but still needs a real
            // number if this search ends in becoming host instead (falls back to MatchHostService.
            // DefaultMaxPlayers there specifically - a room can't be created with no capacity). Not filtered out
            // by _currentLobbyPlayerCount above since it isn't a specific headcount to compare against that.
            if (ShowGameMode)
            {
                maxPlayersOptions.Add(("무관", MatchTags.MaxPlayersAny));
            }
            _maxPlayersField = NativeDropdownField<int>.BuildOrFallback(maxPlayersOptions, MatchHostService.DefaultMaxPlayers, overlayParent: _overlayParent);
            AddChild(Sts2ModalPanel.BuildSettingsRow("인원 수", _maxPlayersField.Control));
            AddChild(Sts2ModalPanel.BuildSettingsDivider());
            UpdateMaxPlayersEnabled();
        }
    }

    /// <summary>
    /// Ascension picker, shown only to players who've actually unlocked some multiplayer ascension - the vanilla
    /// NAscensionPanel hides itself the same way (Visible = maxAscension > 0), and for the majority still sitting at
    /// 0 the dropdown would offer exactly one meaningful choice.
    ///
    /// Note this filters on an exact level rather than a range: a room plays at the *lowest* ceiling among its
    /// members, so "at least N" would be satisfied by a room that stops being a match the moment anyone else joins.
    ///
    /// Highest level first, "무관" last (the reverse of a plain 0..max/"무관"-first listing) and defaults to the
    /// player's own highest unlocked level, not "무관" - the intent is clearing progressively higher ascensions in
    /// order, so leading with (and defaulting to) the top of that ladder matches what most players actually want to
    /// pick most of the time. Never loaded from MatchSettingsStore either (see LoadFrom, and MatchSettings.Ascension's
    /// own doc) - always freshly this-session's actual ceiling, which only ever moves up as levels get cleared.
    /// </summary>
    private void BuildAscensionRow()
    {
        int maxAscension = MatchTags.LocalMaxAscension;
        if (maxAscension <= 0)
        {
            return;
        }

        var ascensionOptions = new List<(string Label, int Value)>();
        for (int level = maxAscension; level >= 0; level--)
        {
            ascensionOptions.Add((level == 0 ? "승천 없음" : $"승천 {level}", level));
        }
        ascensionOptions.Add(("무관", MatchTags.AscensionAny));
        _ascensionField = NativeDropdownField<int>.BuildOrFallback(ascensionOptions, maxAscension, overlayParent: _overlayParent);
        AddChild(Sts2ModalPanel.BuildSettingsRow("승천 단계", _ascensionField.Control));
        AddChild(Sts2ModalPanel.BuildSettingsDivider());
    }

    private void UpdateMaxPlayersEnabled()
    {
        if (_gameModeField == null || _maxPlayersField == null)
        {
            return;
        }
        // Daily hosting always forces 4 players internally (see MatchHostService) - the selector would be misleading.
        bool isDaily = _gameModeField.Value == GameMode.Daily;
        _maxPlayersField.Disabled = isDaily;
    }

    public string Community => _communityInput.Text;

    public string Language => _languageField.Value;

    /// <summary>Always AscensionAny for a player with nothing unlocked - the row isn't built for them at all.</summary>
    public int SelectedAscension => _ascensionField?.Value ?? MatchTags.AscensionAny;

    /// <summary>Always required - no longer a user-facing toggle (mismatched mods breaking a run isn't something
    /// anyone would deliberately opt out of), but MatchSettings.RequireModMatch and everything downstream that
    /// reads it (tagging, search filters) is untouched.</summary>
    public bool RequireModMatch => true;

    public GameMode SelectedGameMode => _gameModeField?.Value ?? GameMode.Standard;
    public int SelectedMaxPlayers => _maxPlayersField?.Value ?? MatchHostService.DefaultMaxPlayers;

    public void ApplyTo(MatchSettings settings)
    {
        settings.Community = Community;
        settings.Language = Language;
        settings.RequireModMatch = RequireModMatch;
        settings.Ascension = SelectedAscension;
        if (ShowGameMode)
        {
            settings.GameMode = SelectedGameMode;
        }
        if (ShowMaxPlayers)
        {
            settings.MaxPlayers = SelectedMaxPlayers;
        }
    }

    /// <summary>Deliberately does NOT touch _ascensionField - see BuildAscensionRow's own doc for why that one
    /// always keeps whatever fresh default Build() gave it (the player's current highest unlocked level) instead
    /// of being restored from a previous session's saved settings.Ascension the way every other field here is.</summary>
    public void LoadFrom(MatchSettings settings)
    {
        _communityInput.Text = settings.Community;

        // A stored code no longer in the list (or empty, meaning "no preference" was saved) resolves to "언어
        // 무관" - NativeDropdownField.SetValue's own fallback-to-first-option rule handles that.
        _languageField.SetValue(settings.Language);

        if (_gameModeField != null)
        {
            _gameModeField.SetValue(settings.GameMode);
        }
        if (_maxPlayersField != null)
        {
            _maxPlayersField.SetValue(settings.MaxPlayers);
        }
        UpdateMaxPlayersEnabled();
    }
}
