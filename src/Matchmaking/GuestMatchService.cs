using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Steamworks;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Tries the DC-crawler lobby list (see GuestLobbyListClient) as a fallback candidate source when this mod's own
/// Steam-tag-based search (MatchSearchService) finds nothing - these are vanilla lobbies from hosts who aren't
/// running this mod, so unlike a tagged search result there's no guarantee any given candidate is actually
/// joinable. Candidates are tried oldest-post-first (ascending `no`), each pre-checked via Steam lobby metadata
/// (open slot, owner not banned) and then actually joined with the native failure popup suppressed - a failed
/// attempt here just means "try the next candidate", not something the player needs to see.
/// </summary>
public static class GuestMatchService
{
    private static readonly Regex LobbyUrlPattern = new(@"^steam://joinlobby/2868840/(\d+)/(\d+)$", RegexOptions.Compiled);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Fresh-match entry point: excludes is_rehost posts (those are for AttemptRehostAsync instead) and
    /// filters on the player's own matching preferences.</summary>
    public static async Task<CSteamID?> TryJoinFromListAsync(NSubmenuStack stack, MatchSettings settings, CancellationToken cancelToken)
    {
        IReadOnlyList<GuestLobby> lobbies = await GuestLobbyListClient.GetAsync(cancelToken);
        GuestDeadCandidateTracker.Prune(lobbies);
        IEnumerable<GuestLobby> candidates = lobbies
            .Where(lobby => !lobby.IsRehost && MatchesFreshCriteria(lobby, settings))
            .OrderBy(ParseNo);
        return await TryJoinCandidatesAsync(stack, candidates, cancelToken);
    }

    /// <summary>Rehost-wait entry point: only is_rehost posts, no other filtering - unlike this mod's own
    /// RehostParticipantsKey-tagged search, there's no way to pre-check whether the local player actually belongs
    /// to a given DC-crawled rehost post's save, so every candidate just gets tried and a NotInSaveGame-style
    /// failure is treated the same as any other "move on to the next one".</summary>
    public static async Task<CSteamID?> TryJoinRehostFromListAsync(NSubmenuStack stack, CancellationToken cancelToken)
    {
        IReadOnlyList<GuestLobby> lobbies = await GuestLobbyListClient.GetAsync(cancelToken);
        GuestDeadCandidateTracker.Prune(lobbies);
        IEnumerable<GuestLobby> candidates = lobbies.Where(lobby => lobby.IsRehost).OrderBy(ParseNo);
        return await TryJoinCandidatesAsync(stack, candidates, cancelToken);
    }

    private static async Task<CSteamID?> TryJoinCandidatesAsync(NSubmenuStack stack, IEnumerable<GuestLobby> sortedCandidates, CancellationToken cancelToken)
    {
        foreach (GuestLobby lobby in sortedCandidates)
        {
            cancelToken.ThrowIfCancellationRequested();

            // steam://joinlobby/2868840/{lobbyId}/{steamId64} - group 1 is the lobby, group 2 is just the
            // inviter's SteamID64 (unused here).
            Match urlMatch = LobbyUrlPattern.Match(lobby.LobbyUrl);
            if (!urlMatch.Success || !ulong.TryParse(urlMatch.Groups[1].Value, out ulong lobbyIdValue))
            {
                continue;
            }
            var lobbyId = new CSteamID(lobbyIdValue);

            // Already confirmed dead (didn't respond to a metadata request) on a previous cycle - the recruit
            // board can keep serving a stale post well after the underlying lobby closed, so skip straight past it
            // instead of re-paying GuestLobbyDataAwaiter's ~5s timeout every single retry. GuestDeadCandidateTracker
            // forgets it again once the board itself stops listing this `no` (see Prune, called above).
            if (GuestDeadCandidateTracker.IsDead(lobby.No))
            {
                continue;
            }

            // Pre-check via Steam lobby metadata: is it even reachable, does it have an open slot, is the owner
            // someone we've personally banned - any of these being wrong means skip straight to the next
            // candidate without ever attempting a real connection.
            if (!await GuestLobbyDataAwaiter.RequestAsync(lobbyId, MetadataTimeout, cancelToken))
            {
                Log.Info($"[sts2_matchmaker] Guest candidate {lobbyIdValue} (no={lobby.No}) didn't respond to a metadata request, skipping");
                GuestDeadCandidateTracker.MarkDead(lobby.No);
                continue;
            }
            int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
            int memberLimit = SteamMatchmaking.GetLobbyMemberLimit(lobbyId);
            if (memberLimit > 0 && memberCount >= memberLimit)
            {
                Log.Info($"[sts2_matchmaker] Guest candidate {lobbyIdValue} (no={lobby.No}) is full ({memberCount}/{memberLimit}), skipping");
                continue;
            }
            if (BanTagSync.IsLocalPlayerBannedFromLobby(lobbyId))
            {
                continue;
            }
            ulong ownerId = SteamMatchmaking.GetLobbyOwner(lobbyId).m_SteamID;
            if (BanListStore.IsBanned(ownerId))
            {
                continue;
            }

            try
            {
                await MatchJoinService.JoinLobbyAsync(stack, lobbyId, suppressErrorPopup: true);
                Log.Info($"[sts2_matchmaker] Guest-matched into lobby {lobbyIdValue} (DC no={lobby.No})");
                return lobbyId;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Info($"[sts2_matchmaker] Guest candidate {lobbyIdValue} (no={lobby.No}) failed, trying next: {ex.Message}");
                // JoinLobbyAsync's underlying NMainMenu.JoinGame() unconditionally pushed NMultiplayerSubmenu +
                // NJoinFriendScreen before failing (see MatchJoinService's own doc) - pop back to where we
                // started so the next candidate begins from a clean stack instead of piling up dead screens.
                if (stack.Peek() is NJoinFriendScreen or NMultiplayerSubmenu)
                {
                    stack.Pop();
                    stack.Pop();
                }
            }
        }
        return null;
    }

    private static bool MatchesFreshCriteria(GuestLobby lobby, MatchSettings settings)
    {
        if (!string.Equals(lobby.GameMode, settings.GameMode.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (settings.Ascension != MatchTags.AscensionAny && !AscensionInRange(lobby, settings.Ascension))
        {
            return false;
        }
        if (settings.MaxPlayers != MatchTags.MaxPlayersAny && lobby.MaxPlayers.HasValue && lobby.MaxPlayers.Value != settings.MaxPlayers)
        {
            return false;
        }

        string normalizedSettingsCommunity = MatchTags.NormalizeTag(settings.Community);
        string normalizedLobbyCommunity = MatchTags.NormalizeTag(lobby.Community);
        if (string.IsNullOrEmpty(normalizedSettingsCommunity))
        {
            // Public/no-preference search - mirrors MatchSearchService.SearchAsync's own rule that a public
            // search excludes lobbies advertising a community of their own.
            if (!string.IsNullOrEmpty(normalizedLobbyCommunity))
            {
                return false;
            }
        }
        else if (normalizedLobbyCommunity != normalizedSettingsCommunity)
        {
            return false;
        }

        string normalizedSettingsLanguage = MatchTags.NormalizeTag(settings.Language);
        if (!string.IsNullOrEmpty(normalizedSettingsLanguage) && MatchTags.NormalizeTag(lobby.Lang) != normalizedSettingsLanguage)
        {
            return false;
        }

        return true;
    }

    /// <summary>Ascension range check against the doc's own recommendation to clamp joke titles ("100승천" etc.)
    /// into the 0-20 range before trusting them. Both bounds missing means the post had no ascension info at all -
    /// permissive (allow it) rather than rejecting on missing data.</summary>
    private static bool AscensionInRange(GuestLobby lobby, int wanted)
    {
        if (!lobby.MinAsc.HasValue && !lobby.MaxAsc.HasValue)
        {
            return true;
        }
        int min = Math.Clamp(lobby.MinAsc ?? 0, 0, 20);
        int max = Math.Clamp(lobby.MaxAsc ?? 20, 0, 20);
        return wanted >= min && wanted <= max;
    }

    private static long ParseNo(GuestLobby lobby) => long.TryParse(lobby.No, out long value) ? value : long.MaxValue;
}
