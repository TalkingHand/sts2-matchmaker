using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Remembers the last few distinct community names actually used (see MatchConditionsPanel.ApplyTo), so the
/// community field can offer a quick-pick list instead of relying on retyping the exact same text every time -
/// community matching is exact-string equality (see MatchTags.NormalizeTag/MatchSearchService's own empty-vs-
/// non-empty filtering), so a single typo creates an invisible new "community" nobody else can find; picking
/// from history avoids that entirely. Shares MatchSettingsStore's own config file (a separate key in the same
/// "matching" section) rather than a new file, same reasoning MatchSettingsStore itself uses for best-effort
/// Load-before-Save (keep unrelated sections/keys intact either way).
/// </summary>
public static class RecentCommunityStore
{
    private const string ConfigPath = "user://mod_configs/sts2_matchmaker.cfg";
    private const string Section = "matching";
    private const string Key = "recent_communities";
    private const int MaxEntries = 8;

    public static List<string> Load()
    {
        var config = new ConfigFile();
        if (config.Load(ConfigPath) != Error.Ok)
        {
            return new List<string>();
        }
        var raw = (string[])config.GetValue(Section, Key, Array.Empty<string>());
        return raw.ToList();
    }

    /// <summary>Moves communityName to the front of the recent list (deduped via MatchTags.NormalizeTag's own
    /// trim+lowercase comparison, so "MyGuild" and "myguild " collapse to one entry), trims to MaxEntries most
    /// recent. No-op for an empty name - there's nothing useful to remember about "no community filter".</summary>
    public static void Record(string communityName)
    {
        string trimmed = communityName.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }
        List<string> recent = Load();
        recent.RemoveAll(existing => MatchTags.NormalizeTag(existing) == MatchTags.NormalizeTag(trimmed));
        recent.Insert(0, trimmed);
        if (recent.Count > MaxEntries)
        {
            recent.RemoveRange(MaxEntries, recent.Count - MaxEntries);
        }

        var config = new ConfigFile();
        config.Load(ConfigPath); // best-effort - keep any unrelated sections/keys if the file already exists
        config.SetValue(Section, Key, recent.ToArray());
        Error err = config.Save(ConfigPath);
        if (err != Error.Ok)
        {
            Log.Error($"[sts2_matchmaker] Failed to save recent community list: {err}");
        }
    }
}
