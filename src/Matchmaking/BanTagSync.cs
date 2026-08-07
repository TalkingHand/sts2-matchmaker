using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Logging;
using Steamworks;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Merges the local player's personal ban list into a lobby's shared sts2mm_banned tag (read-current, union in
/// mine, write back), so that anyone I've banned - whether I'm hosting or just a participant - self-excludes this
/// lobby from their own search results (see MatchSearchService).
///
/// SetLobbyData has no atomic append/compare-and-swap: if two lobby members read-merge-write around the same time,
/// whichever write lands last on Steam's backend wins outright, silently dropping the other's contribution (a
/// classic lost-update race). We can't eliminate that without a real coordinating server, so instead we retry:
/// after writing, wait briefly (long enough for the game's normal Steam callback pump to receive other members'
/// concurrent updates - it already runs continuously via SteamInitializer), then re-check whether our own IDs are
/// still present and redo the merge if not. This shrinks the loss window a lot without needing to build a proper
/// server round-trip (RequestLobbyData + waiting on a LobbyDataUpdate_t callback) for what's a low-stakes feature.
/// </summary>
public static class BanTagSync
{
    private const char Separator = '|';
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromMilliseconds(1200);

    public static async Task MergeMyBansIntoLobbyAsync(CSteamID lobbyId)
    {
        List<ulong> myBans = BanListStore.GetBannedIds();
        if (myBans.Count == 0)
        {
            return;
        }

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            HashSet<ulong> current = ParseIds(SteamMatchmaking.GetLobbyData(lobbyId, MatchTags.BannedKey));
            bool alreadyComplete = myBans.All(current.Contains);
            if (alreadyComplete && attempt > 1)
            {
                // A previous attempt's write already stuck (nothing raced it away) - nothing left to do.
                return;
            }

            current.UnionWith(myBans);
            SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.BannedKey, string.Join(Separator, current));

            if (attempt == MaxAttempts)
            {
                break;
            }

            await Task.Delay(RecheckDelay);

            HashSet<ulong> afterDelay = ParseIds(SteamMatchmaking.GetLobbyData(lobbyId, MatchTags.BannedKey));
            if (myBans.All(afterDelay.Contains))
            {
                return;
            }
            Log.Warn($"[sts2_matchmaker] Ban tag merge for lobby {lobbyId.m_SteamID} looked like it raced with another writer, retrying (attempt {attempt})");
        }
    }

    /// <summary>
    /// Removes a single SteamID64 from a lobby's shared ban tag - the other half of MergeMyBansIntoLobbyAsync,
    /// needed because BanListStore.Remove (un-banning) only touches the LOCAL personal list. Without this, a lobby
    /// I'd already merged that ID into (while they were still banned) kept excluding them forever even after I
    /// un-banned them - the shared tag just sits there once written, nothing ever prunes it.
    ///
    /// Trade-off: the tag is a flat union with no per-contributor breakdown, so if another CURRENT lobby member
    /// independently banned the same person too, this drops their contribution as well - acceptable for what's a
    /// low-stakes feature (see MergeMyBansIntoLobbyAsync's own doc); they can just re-ban with one click.
    /// </summary>
    public static void PruneIdFromLobby(CSteamID lobbyId, ulong steamId)
    {
        HashSet<ulong> current = ParseIds(SteamMatchmaking.GetLobbyData(lobbyId, MatchTags.BannedKey));
        if (current.Remove(steamId))
        {
            SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.BannedKey, string.Join(Separator, current));
        }
    }

    public static bool IsLocalPlayerBannedFromLobby(CSteamID lobbyId)
    {
        ulong myId = SteamUser.GetSteamID().m_SteamID;
        return IsIdBannedInLobby(lobbyId, myId);
    }

    /// <summary>
    /// Checks the lobby's shared ban tag for an arbitrary SteamID64, not just the local player - used by
    /// BanEnforcementPatch so a host also enforces bans contributed by its participants (who have no kick
    /// authority of their own), not just its own personal ban list.
    /// </summary>
    public static bool IsIdBannedInLobby(CSteamID lobbyId, ulong steamId)
    {
        return ParseIds(SteamMatchmaking.GetLobbyData(lobbyId, MatchTags.BannedKey)).Contains(steamId);
    }

    private static HashSet<ulong> ParseIds(string? raw)
    {
        var set = new HashSet<ulong>();
        if (string.IsNullOrEmpty(raw))
        {
            return set;
        }
        foreach (string part in raw.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ulong.TryParse(part, out ulong id))
            {
                set.Add(id);
            }
        }
        return set;
    }
}
