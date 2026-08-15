using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using Sts2Matchmaker.Localization;
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

    /// <summary>
    /// Validates a pasted/typed SteamID64 using Steamworks' own CSteamID.IsValid()/BIndividualAccount() - not a
    /// hand-rolled length or numeric-range check. IsValid() confirms the 64-bit value actually decodes to a
    /// well-formed universe/account-type/instance per Valve's own bit layout (rejecting e.g. a short number with a
    /// zero "universe" byte outright), and BIndividualAccount() additionally rejects anything that DOES decode
    /// cleanly but isn't a personal account (a Steam group, game server, anonymous ID, etc - none of those are
    /// bannable "a person I matched with" targets here). This also happens to be exactly what makes the export
    /// template's placeholder example line (see ExportToFile) self-filtering: it's an out-of-range number that
    /// fails IsValid(), so leaving it in place and re-importing the file doesn't register a bogus ban.
    /// </summary>
    public static bool TryParseSteamId64(string text, out ulong steamId)
    {
        text = text.Trim();
        if (!ulong.TryParse(text, out steamId))
        {
            return false;
        }
        var id = new CSteamID(steamId);
        return id.IsValid() && id.BIndividualAccount();
    }

    /// <summary>Same read-modify-write as <see cref="Add"/>, but for many entries under a single lock/load/save -
    /// used by <see cref="ImportFromFile"/> so importing a large list doesn't reopen and rewrite the .cfg file
    /// once per line. Unlike Add, an empty entry.Nickname falls back to ResolveNickname (best-effort, same as a
    /// kick+ban does) rather than being stored as-is - an imported line is only expected to supply a nickname when
    /// the player's external source already had one; when it didn't, this still gets the same "try to resolve it,
    /// possibly succeed later when BanListPanel reopens" treatment every other add path gets.</summary>
    public static void AddMany(Dictionary<ulong, BanEntry> entries)
    {
        lock (Lock)
        {
            var config = new ConfigFile();
            config.Load(ConfigPath); // best-effort - keep existing entries
            foreach ((ulong steamId, BanEntry entry) in entries)
            {
                string nickname = string.IsNullOrEmpty(entry.Nickname) ? ResolveNickname(steamId) : entry.Nickname;
                config.SetValue(Section, steamId.ToString(), $"{nickname}{FieldSeparator}{entry.Reason}");
            }
            Error err = config.Save(ConfigPath);
            if (err != Error.Ok)
            {
                Log.Error($"[sts2_matchmaker] Failed to save ban list after import: {err}");
            }
        }
    }

    /// <summary>A line ImportFromFile couldn't parse into a valid SteamID64 - RawId/Nickname/Reason are whatever
    /// text sat in each comma-separated position regardless (RawId is NOT necessarily numeric, e.g. a typo or a
    /// stray word), so BanListPanel can show the player exactly what was on the offending line instead of just a
    /// count.</summary>
    public readonly record struct SkippedLine(string RawId, string Nickname, string Reason);

    public readonly record struct ImportResult(int Added, List<SkippedLine> Skipped);

    /// <summary>Deliberately not a real SteamID64 - it's numerically far too small to decode to a valid
    /// universe/account-type under Steam's own bit layout, so TryParseSteamId64 (via CSteamID.IsValid()) rejects
    /// it. The leading zero also makes it visually obvious to a human that it's a placeholder, not a real ID
    /// that happens to start low.</summary>
    private const string ExamplePlaceholderSteamId = "01234567890123456";

    /// <summary>
    /// Writes every current entry as "SteamID64", "SteamID64,nickname", or "SteamID64,nickname,reason" (one per
    /// line, trailing empty fields omitted) to an arbitrary path the player picked via a native Save dialog (see
    /// BanListPanel.OnExportPressed), preceded by a short "#"-comment header. When there are no real entries yet, a
    /// concrete example row is written into the actual list section itself (not just described in a comment) using
    /// ExamplePlaceholderSteamId - so a first-time player sees exactly what a real line looks like in place, rather
    /// than an abstract description, but importing this file unedited doesn't register a bogus ban:
    /// TryParseSteamId64 rejects the placeholder ID and ImportFromFile counts it as skipped, same as any other
    /// malformed line.
    /// </summary>
    public static void ExportToFile(string absolutePath)
    {
        var lines = new List<string>
        {
            Loc.Get("# sts2_matchmaker 밴 목록 파일"),
            "#",
            Loc.Get("# - \"#\"으로 시작하는 줄은 무시됩니다. 항목 앞에 \"#\"을 붙이면 그 줄만 임시로 제외할 수 있습니다."),
            Loc.Get("# - 형식: SteamID64,닉네임,사유 (닉네임과 사유는 생략 가능합니다. 닉네임을 비워두면 Steam에서 자동으로 가져오려 시도합니다)."),
            "#",
            Loc.Get("# 아래부터 실제 목록입니다. 새 SteamID64는 이 아래 아무 줄에나 추가하세요."),
            string.Empty,
        };

        Dictionary<ulong, BanEntry> entries = Load();
        if (entries.Count == 0)
        {
            lines.Add($"{ExamplePlaceholderSteamId},{Loc.Get("예시 닉네임")},{Loc.Get("이 줄은 예시입니다 - 실제 SteamID64로 바꾸거나 지우세요")}");
        }
        else
        {
            foreach ((ulong steamId, BanEntry entry) in entries.OrderBy(kv => kv.Key))
            {
                lines.Add(FormatExportLine(steamId, entry));
            }
        }
        System.IO.File.WriteAllLines(absolutePath, lines);
    }

    private static string FormatExportLine(ulong steamId, BanEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Reason))
        {
            return string.IsNullOrEmpty(entry.Nickname) ? steamId.ToString() : $"{steamId},{entry.Nickname}";
        }
        return $"{steamId},{entry.Nickname},{entry.Reason}";
    }

    /// <summary>
    /// Reads an arbitrary path the player picked via a native Open dialog (see BanListPanel.OnImportPressed) - same
    /// line format ExportToFile writes ("SteamID64", "SteamID64,nickname", or "SteamID64,nickname,reason", "#"
    /// lines ignored) - and adds every valid entry via <see cref="AddMany"/>. Unlike the earlier fixed-folder
    /// version of this feature, there's no file to archive/rename afterward - the player explicitly chose this
    /// exact file through the dialog each time, so there's no risk of an unnoticed leftover file getting silently
    /// re-imported later.
    /// </summary>
    public static ImportResult ImportFromFile(string absolutePath)
    {
        var toAdd = new Dictionary<ulong, BanEntry>();
        var skipped = new List<SkippedLine>();
        foreach (string rawLine in System.IO.File.ReadAllLines(absolutePath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            (string rawId, string nickname, string reason) = SplitImportLine(line);
            if (TryParseSteamId64(rawId, out ulong steamId))
            {
                toAdd[steamId] = new BanEntry(nickname, reason);
            }
            else
            {
                skipped.Add(new SkippedLine(rawId, nickname, reason));
            }
        }

        if (toAdd.Count > 0)
        {
            AddMany(toAdd);
        }

        return new ImportResult(toAdd.Count, skipped);
    }

    /// <summary>Splits into (id, nickname, reason) purely positionally - up to 3 comma-separated fields, trailing
    /// fields omitted from the line default to empty. Always returns something for RawId (never throws) even when
    /// that text isn't actually a valid SteamID64, since ImportFromFile needs it either way: to parse on success,
    /// or to show the player what was on the line on failure (see SkippedLine's own doc). No longer accepts a bare
    /// space as a field separator - only comma - since a nickname is very likely to contain spaces itself.</summary>
    private static (string RawId, string Nickname, string Reason) SplitImportLine(string line)
    {
        string[] parts = line.Split(',', 3);
        string rawId = parts[0].Trim();
        string nickname = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        string reason = parts.Length > 2 ? parts[2].Trim() : string.Empty;
        return (rawId, nickname, reason);
    }
}
