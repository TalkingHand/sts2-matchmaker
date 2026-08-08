using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Saves/loads MatchSettings to user://mod_configs/sts2_matchmaker.cfg, matching where other mods keep their
/// per-user config (see %APPDATA%\SlayTheSpire2\mod_configs\*.cfg for other installed mods' files).
/// </summary>
public static class MatchSettingsStore
{
    private const string ConfigPath = "user://mod_configs/sts2_matchmaker.cfg";
    private const string Section = "matching";

    public static MatchSettings Load()
    {
        var settings = new MatchSettings();
        var config = new ConfigFile();
        Error err = config.Load(ConfigPath);
        if (err != Error.Ok)
        {
            return settings;
        }

        settings.Community = (string)config.GetValue(Section, "community", settings.Community);
        // Language is deliberately NOT loaded here - see MatchSettings.Language's own doc.
        settings.MaxPlayers = (int)config.GetValue(Section, "max_players", settings.MaxPlayers);
        settings.RequireModMatch = (bool)config.GetValue(Section, "require_mod_match", settings.RequireModMatch);
        settings.CanHost = (bool)config.GetValue(Section, "can_host", settings.CanHost);
        settings.GameMode = (GameMode)(int)config.GetValue(Section, "game_mode", (int)settings.GameMode);
        // Ascension is deliberately NOT persisted here - see MatchSettings.Ascension's own doc.
        return settings;
    }

    public static void Save(MatchSettings settings)
    {
        var config = new ConfigFile();
        config.Load(ConfigPath); // best-effort - keep any unrelated sections if the file already exists
        config.SetValue(Section, "community", settings.Community);
        // Language is deliberately NOT saved here - see MatchSettings.Language's own doc.
        config.SetValue(Section, "max_players", settings.MaxPlayers);
        config.SetValue(Section, "require_mod_match", settings.RequireModMatch);
        config.SetValue(Section, "can_host", settings.CanHost);
        config.SetValue(Section, "game_mode", (int)settings.GameMode);
        // Ascension is deliberately NOT persisted here - see MatchSettings.Ascension's own doc.

        Error err = config.Save(ConfigPath);
        if (err != Error.Ok)
        {
            Log.Error($"[sts2_matchmaker] Failed to save match settings: {err}");
        }
    }
}
