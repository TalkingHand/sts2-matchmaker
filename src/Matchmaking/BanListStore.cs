using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using Steamworks;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Local, per-user ban list (SteamID64 -> nickname + reason), stored in user://mod_configs/sts2_matchmaker_banlist.cfg.
/// There is no shared/community list - this is purely "people I personally don't want to match with".
/// </summary>
public static class BanListStore
{
    private const string ConfigPath = "user://mod_configs/sts2_matchmaker_banlist.cfg";
    private const string Section = "bans";
    private const string FieldSeparator = "||";

    // All reads/writes go through this lock. Add/Remove do read-modify-write on the same file from several
    // independent async call sites (hosting, joining, the ban confirm dialog), and without serializing them a
    // lost-update race can happen - observed in practice: two near-simultaneous saves left the file completely
    // empty, silently wiping every existing entry rather than just dropping the newest one.
    private static readonly object Lock = new();

    public readonly record struct BanEntry(string Nickname, string Reason);

    public static Dictionary<ulong, BanEntry> Load()
    {
        lock (Lock)
        {
            return LoadUnlocked();
        }
    }

    private static Dictionary<ulong, BanEntry> LoadUnlocked()
    {
        var result = new Dictionary<ulong, BanEntry>();
        var config = new ConfigFile();
        // HasSection is required before GetSectionKeys - calling GetSectionKeys for a section that doesn't exist
        // yet (e.g. the file exists but nobody has been banned yet) logs a native Godot engine error every time.
        if (config.Load(ConfigPath) != Error.Ok || !config.HasSection(Section))
        {
            return result;
        }

        foreach (string key in config.GetSectionKeys(Section))
        {
            if (ulong.TryParse(key, out ulong steamId))
            {
                string raw = (string)config.GetValue(Section, key, string.Empty);
                result[steamId] = ParseEntry(raw);
            }
        }
        return result;
    }

    private static BanEntry ParseEntry(string raw)
    {
        int separatorIndex = raw.IndexOf(FieldSeparator, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            // Legacy format (a single free-text note, no nickname) - treat the whole thing as the reason.
            return new BanEntry(string.Empty, raw);
        }
        return new BanEntry(raw[..separatorIndex], raw[(separatorIndex + FieldSeparator.Length)..]);
    }

    public static List<ulong> GetBannedIds() => Load().Keys.ToList();

    public static bool IsBanned(ulong steamId) => Load().ContainsKey(steamId);

    /// <summary>
    /// SteamFriends.GetFriendPersonaName only reliably resolves people Steam has already cached persona data for -
    /// friends, or anyone a PersonaStateChange_t callback has already fired for this session. For a stranger you
    /// just matched with, that often hasn't happened yet, and Steam's own miss value ("[unknown]") isn't useful to
    /// store - we'd rather remember "no name yet" (empty, which BanListPanel already displays gracefully as just
    /// the SteamID) so a later re-resolve attempt has something to actually improve on. Not called at ban-time only
    /// - BanListPanel calls this again every time it opens, since by then Steam has often caught up.
    /// </summary>
    public static string ResolveNickname(ulong steamId)
    {
        string name = SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
        return name is "[unknown]" or "" ? string.Empty : name;
    }

    public static void Add(ulong steamId, string nickname, string reason)
    {
        lock (Lock)
        {
            var config = new ConfigFile();
            config.Load(ConfigPath); // best-effort - keep existing entries
            config.SetValue(Section, steamId.ToString(), $"{nickname ?? string.Empty}{FieldSeparator}{reason ?? string.Empty}");
            Error err = config.Save(ConfigPath);
            if (err != Error.Ok)
            {
                Log.Error($"[sts2_matchmaker] Failed to save ban list: {err}");
            }
        }
    }

    public static void Remove(ulong steamId)
    {
        lock (Lock)
        {
            var config = new ConfigFile();
            config.Load(ConfigPath);
            config.EraseSectionKey(Section, steamId.ToString());
            Error err = config.Save(ConfigPath);
            if (err != Error.Ok)
            {
                Log.Error($"[sts2_matchmaker] Failed to save ban list after removal: {err}");
            }
        }
    }
}
