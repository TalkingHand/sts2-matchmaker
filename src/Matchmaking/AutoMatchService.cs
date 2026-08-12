using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using Steamworks;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Removes the manual host/join choice: search first, and only become host if nobody matching was found (and only
/// if canHost is true - some players want to only ever join, e.g. their machine/upload isn't fit to host). There's
/// no central coordinator, so if two host-capable players hit "매칭 시작" in the same instant they can both end up
/// hosting separate lobbies instead of one joining the other - the short random delay + re-check before committing
/// to hosting narrows that window but can't eliminate it without a real matchmaking server.
/// </summary>
public static class AutoMatchService
{
    /// <summary>Falls back to whatever language the player currently has the game itself set to (see LocManager)
    /// when no explicit preference was picked ("언어 무관").</summary>
    private static string ResolveHostLanguage(MatchSettings settings) =>
        string.IsNullOrEmpty(settings.Language) ? (LocManager.Instance?.Language ?? string.Empty) : settings.Language;

    /// <summary>"무관" ("인원 수", only offered in MatchmakingWindow - see MatchConditionsPanel's own doc) means
    /// something different depending on which of the two things this search might end up doing: while SEARCHING
    /// for an existing room to join, it means applying no maxPlayers filter at all (any room size is fine) -
    /// SearchAsync already accepts null for exactly this. See ResolveHostMaxPlayers for the OTHER meaning, if this
    /// search ends in becoming host instead.</summary>
    private static int? SearchMaxPlayers(MatchSettings settings) =>
        settings.MaxPlayers == MatchTags.MaxPlayersAny ? null : settings.MaxPlayers;

    /// <summary>The other half of SearchMaxPlayers' doc: a room being CREATED can't have "무관" as its own
    /// capacity - falls back to MatchHostService.DefaultMaxPlayers when that's what was picked, since "인원 수"
    /// mostly existing to let a search-only player avoid ever thinking about a number they don't care about.</summary>
    private static int ResolveHostMaxPlayers(MatchSettings settings) =>
        settings.MaxPlayers == MatchTags.MaxPlayersAny ? MatchHostService.DefaultMaxPlayers : settings.MaxPlayers;

    public static async Task<AutoMatchResult> AutoMatchAsync(
        NSubmenuStack stack, MatchSettings settings, CancellationToken cancelToken = default)
    {
        while (true)
        {
            cancelToken.ThrowIfCancellationRequested();
            List<MatchLobbyInfo> found = await MatchSearchService.SearchWithProgressiveRegionAsync(
                settings.Community, settings.RequireModMatch, SearchMaxPlayers(settings), MatchTags.KindFresh, settings.Language, settings.GameMode,
                AscensionSearchFilter.For(settings.Ascension), cancelToken);
            if (found.Count > 0)
            {
                Log.Info($"[sts2_matchmaker] AutoMatch found {found.Count} lobbies, joining {found[0].LobbyId.m_SteamID}");
                return AutoMatchResult.Join(found[0].LobbyId);
            }

            // Nothing hosted through this mod - fall back to vanilla lobbies crawled from the DC recruit board.
            // Tried regardless of CanHost, but only up to this point in the loop: once hosting is decided below,
            // this function returns and never runs again for this search.
            CSteamID? guestJoined = await GuestMatchService.TryJoinFromListAsync(stack, settings, cancelToken);
            if (guestJoined.HasValue)
            {
                return AutoMatchResult.Joined(guestJoined.Value);
            }

            if (!settings.CanHost)
            {
                Log.Info("[sts2_matchmaker] AutoMatch found nothing and canHost=false, waiting to retry");
                await Task.Delay(TimeSpan.FromSeconds(3), cancelToken);
                continue;
            }

            int jitterMs = Random.Shared.Next(300, 900);
            await Task.Delay(jitterMs, cancelToken);

            List<MatchLobbyInfo> recheck = await MatchSearchService.SearchWithProgressiveRegionAsync(
                settings.Community, settings.RequireModMatch, SearchMaxPlayers(settings), MatchTags.KindFresh, settings.Language, settings.GameMode,
                AscensionSearchFilter.For(settings.Ascension), cancelToken);
            if (recheck.Count > 0)
            {
                Log.Info($"[sts2_matchmaker] AutoMatch found {recheck.Count} lobbies after {jitterMs}ms jitter, joining {recheck[0].LobbyId.m_SteamID}");
                return AutoMatchResult.Join(recheck[0].LobbyId);
            }

            Log.Info("[sts2_matchmaker] AutoMatch found nothing, becoming host");
            HostResult hostResult = await MatchHostService.StartFreshHostAsync(stack, settings.Community, ResolveHostLanguage(settings), settings.GameMode, ResolveHostMaxPlayers(settings), settings.Ascension, openMatchingWindowOnHost: true);
            return hostResult.Error != null ? AutoMatchResult.Fail(hostResult.Error) : AutoMatchResult.Host(hostResult);
        }
    }

    /// <summary>
    /// For participants of a broken multiplayer run who aren't the one re-opening it: never hosts, just keeps
    /// searching kind=rehost lobbies (tagged separately from fresh ones) until a teammate reopens the save and this
    /// finds it, then joins immediately. No community name, language, or mod-match preference needed anymore -
    /// eligibility is checked directly against the lobby's advertised participant SteamID64 list (see
    /// MatchTags.RehostParticipantsKey), which is exact and can't be thrown off by mistyped/mismatched settings
    /// the way a community-name filter could. Game mode isn't filtered on by a preference either, since the save
    /// itself fixes the mode - this just tries all three so it doesn't need to know it in advance.
    /// </summary>
    public static async Task<AutoMatchResult> WaitForRehostAsync(NSubmenuStack stack, CancellationToken cancelToken = default)
    {
        ulong myId = SteamUser.GetSteamID().m_SteamID;
        while (true)
        {
            cancelToken.ThrowIfCancellationRequested();
            foreach (GameMode mode in new[] { GameMode.Standard, GameMode.Daily, GameMode.Custom })
            {
                List<MatchLobbyInfo> found = await MatchSearchService.SearchAsync(
                    communityFilter: null, requireModMatch: false, maxPlayers: null, MatchTags.KindRehost, language: null,
                    ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide, mode, AscensionSearchFilter.None, myId, cancelToken);
                if (found.Count > 0)
                {
                    Log.Info($"[sts2_matchmaker] Rehost found ({mode}), joining {found[0].LobbyId.m_SteamID}");
                    return AutoMatchResult.Join(found[0].LobbyId);
                }
            }

            // Same DC-crawler fallback as AutoMatchAsync, but restricted to is_rehost posts - see
            // GuestMatchService.TryJoinRehostFromListAsync's own doc for why no other filtering applies here.
            CSteamID? guestJoined = await GuestMatchService.TryJoinRehostFromListAsync(stack, cancelToken);
            if (guestJoined.HasValue)
            {
                return AutoMatchResult.Joined(guestJoined.Value);
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancelToken);
        }
    }
}
