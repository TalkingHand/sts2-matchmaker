using System.Collections.Generic;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>User-configurable matching preferences, persisted between sessions via MatchSettingsStore.</summary>
public sealed class MatchSettings
{
    public string Community = string.Empty;

    /// <summary>
    /// Language to match on (single select, ISO 639-2 code) - empty means "no preference" and searches without a
    /// language filter. Not persisted via MatchSettingsStore (unlike most fields here) - MatchConditionsPanel
    /// always defaults this to the game's current UI language fresh each time the window opens (see LoadFrom's
    /// own doc), so it stays in sync with whatever language the player has the game itself set to, instead of
    /// getting stuck reflecting whatever was picked in some earlier session under a different UI language.
    /// </summary>
    public string Language = string.Empty;

    // Region search always starts close and automatically widens all the way to Worldwide if nothing's found (see
    // MatchSearchService.SearchWithProgressiveRegionAsync) - unconditionally, no opt-out. STS2 is turn-based, so
    // ping isn't expected to be a real problem; if it turns out to be, that should be decided from actual data
    // rather than a pre-emptive settings toggle nobody has evidence they need yet.

    public int MaxPlayers = MatchHostService.MaxPlayers;
    public bool RequireModMatch = true;
    public bool CanHost = true;

    /// <summary>
    /// Run types to match on - any of them is fine (OR). Never empty (MatchConditionsPanel keeps at least one
    /// checked, and MatchSettingsStore falls back to Standard). Searching covers every listed mode, but hosting only
    /// happens with exactly one picked: with several there's no single mode to open the room as, so
    /// MatchmakingWindow unticks and disables "호스트 허용" and AutoMatchService refuses to host regardless.
    /// Order is the dropdown's own display order, not the order they were ticked in.
    /// </summary>
    public List<MatchMode> GameModes = new() { MatchMode.Standard };

    /// <summary>The mode a room gets created as - only meaningful while exactly one is picked (see GameModes).</summary>
    public MatchMode HostGameMode => GameModes.Count > 0 ? GameModes[0] : MatchMode.Standard;

    public bool HasMultipleGameModes => GameModes.Count > 1;

    /// <summary>
    /// Exact ascension level to match on, or MatchTags.AscensionAny for "don't care". Not persisted via
    /// MatchSettingsStore (unlike every other field here) - MatchConditionsPanel always defaults this to the
    /// player's current highest unlocked ascension fresh each time the window opens, so clearing progressively
    /// higher levels naturally raises tomorrow's default too, instead of getting stuck reflecting whatever was
    /// picked (or left at) in some earlier session.
    /// </summary>
    public int Ascension = MatchTags.AscensionAny;

    /// <summary>Same as Ascension, for Blitz rooms only - Blitz has its own unlock ladder
    /// (Progress.MaxBlitzAscension), so it gets its own pick instead of Ascension, which then covers only the
    /// other run types. Not persisted either, for the same reason.</summary>
    public int BlitzAscension = MatchTags.AscensionAny;

    /// <summary>The ascension pick that applies to rooms of this run type (Dread ignores it - see
    /// AscensionSearchFilter.For).</summary>
    public int AscensionFor(MatchMode mode) => mode == MatchMode.Blitz ? BlitzAscension : Ascension;
}
