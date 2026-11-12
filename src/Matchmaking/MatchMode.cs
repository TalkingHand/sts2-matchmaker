namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// The mod's own "런 종류" for matching, deliberately NOT the game's MegaCrit.Sts2.Core.Runs.GameMode enum. The game
/// enum changed shape in v0.113.0 (public-beta): it was None/Standard/Daily/Custom and is now
/// None/Standard/Daily/Dread/Blitz, with "custom" moved out of the enum into a separate StartRunLobby.IsCustom /
/// SerializableRun.IsCustomGame flag. Any GameMode constant we compiled in would mean something different on the
/// other build (Custom=3 is Dread on the new one), so the game value is only ever read through GameModeCompat and
/// mapped onto this.
///
/// Numeric values are persisted (MatchSettingsStore "game_mode") and line up with the old GameMode values for the
/// three modes that existed before, so a saved "커스텀" (3) still loads as Custom. ToString() is the Steam lobby
/// tag value (MatchTags.GameModeKey) - again identical to the old GameMode names for those three.
/// </summary>
public enum MatchMode
{
    Standard = 1,
    Daily = 2,
    Custom = 3,
    Blitz = 4,

    /// <summary>Not offered in the matching UI - only ever produced by GameModeCompat for a vanilla-hosted Dread
    /// lobby/save (rehost tagging), so a Dread rehost stays findable by its own participants.</summary>
    Dread = 5,
}
