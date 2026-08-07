using MegaCrit.Sts2.Core.Runs;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>User-configurable matching preferences, persisted between sessions via MatchSettingsStore.</summary>
public sealed class MatchSettings
{
    public string Community = string.Empty;

    /// <summary>Language to match on (single select, ISO 639-2 code) - empty means "no preference" and searches
    /// without a language filter. MatchConditionsPanel defaults the dropdown to the game's current UI language on
    /// first use, but this field itself carries whatever the player last explicitly picked.</summary>
    public string Language = string.Empty;

    // Region search always starts close and automatically widens all the way to Worldwide if nothing's found (see
    // MatchSearchService.SearchWithProgressiveRegionAsync) - unconditionally, no opt-out. STS2 is turn-based, so
    // ping isn't expected to be a real problem; if it turns out to be, that should be decided from actual data
    // rather than a pre-emptive settings toggle nobody has evidence they need yet.

    public int MaxPlayers = MatchHostService.MaxPlayers;
    public bool RequireModMatch = true;
    public bool CanHost = true;
    public GameMode GameMode = GameMode.Standard;

    /// <summary>
    /// Exact ascension level to match on, or MatchTags.AscensionAny for "don't care". Not persisted via
    /// MatchSettingsStore (unlike every other field here) - MatchConditionsPanel always defaults this to the
    /// player's current highest unlocked ascension fresh each time the window opens, so clearing progressively
    /// higher levels naturally raises tomorrow's default too, instead of getting stuck reflecting whatever was
    /// picked (or left at) in some earlier session.
    /// </summary>
    public int Ascension = MatchTags.AscensionAny;
}
