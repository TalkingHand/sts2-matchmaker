using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Logging;

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
        settings.GameModes = LoadGameModes(config);
        // Ascension is deliberately NOT persisted here - see MatchSettings.Ascension's own doc.
        return settings;
    }

    /// <summary>"game_modes" (comma-separated MatchMode numbers) since multi-select; a config written before that
    /// only has the single "game_mode", which still loads as a one-item list. Unknown numbers are dropped, and an
    /// empty result falls back to Standard so GameModes is never empty.</summary>
    private static List<MatchMode> LoadGameModes(ConfigFile config)
    {
        var modes = new List<MatchMode>();
        if (config.HasSectionKey(Section, "game_modes"))
        {
            foreach (string part in ((string)config.GetValue(Section, "game_modes", string.Empty)).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out int value) && Enum.IsDefined(typeof(MatchMode), value) && !modes.Contains((MatchMode)value))
                {
                    modes.Add((MatchMode)value);
                }
            }
        }
        else if (config.HasSectionKey(Section, "game_mode"))
        {
            int value = (int)config.GetValue(Section, "game_mode", (int)MatchMode.Standard);
            if (Enum.IsDefined(typeof(MatchMode), value))
            {
                modes.Add((MatchMode)value);
            }
        }
        if (modes.Count == 0)
        {
            modes.Add(MatchMode.Standard);
        }
        return modes;
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
        config.SetValue(Section, "game_modes", string.Join(",", settings.GameModes.Select(mode => (int)mode)));
        // Still written so an older mod version reading this file keeps a sensible single mode.
        config.SetValue(Section, "game_mode", (int)settings.HostGameMode);
        // Ascension is deliberately NOT persisted here - see MatchSettings.Ascension's own doc.

        Error err = config.Save(ConfigPath);
        if (err != Error.Ok)
        {
            Log.Error($"[sts2_matchmaker] Failed to save match settings: {err}");
        }
    }
}
