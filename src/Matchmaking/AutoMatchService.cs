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

    /// <summary>
    /// Own-mod search (MatchSearchService) and the DC-crawled guest fallback (GuestMatchService) run as two
    /// independent loops racing each other, instead of the guest side only getting checked once per (slow) search
    /// pass. SearchWithProgressiveRegionAsync's 4 region tiers with a 1.5s delay between empty ones cost ~6-8s per
    /// pass whenever this mod has no lobbies of its own to find (the common case early on), which used to gate how
    /// often the guest list even got looked at - now the guest loop polls on its own cadence
    /// (GuestLobbyListClient.MinFetchInterval, matched to the DC crawler's own refresh rate) independent of how
    /// far along the search is. Both the initial search AND the post-jitter recheck search race against it, not
    /// just the first one.
    ///
    /// Safe to race like this because GuestMatchService is the ONLY side that ever touches `stack` (pushing/popping
    /// NMultiplayerSubmenu+NJoinFriendScreen mid-join-attempt) - MatchSearchService is a pure Steam lobby-list
    /// query with no UI of its own, so cancelling it early whenever the guest loop wins has nothing to unwind.
    /// MatchHostService.StartFreshHostAsync is the one exception: it DOES push a screen, and it can't be
    /// cancelled once started - so it's only ever called from CommitToHostAsync, which stops and fully awaits the
    /// guest loop first (see that method's own doc for why this is structural, not just a step every caller has
    /// to remember). Every early return that hands a search-found candidate back to the caller (who will join it
    /// separately) does the same handshake via TryGuestWinAsync first.
    ///
    /// The same handshake is also enforced as a last resort in `finally`, since the loop can exit through paths
    /// that don't go through one of those explicit stops (e.g. the outer cancelToken firing while a search is in
    /// flight) - the caller must never see this method return while guestLoopTask is still capable of touching
    /// `stack` on its own.
    /// </summary>
    public static async Task<AutoMatchResult> AutoMatchAsync(
        NSubmenuStack stack, MatchSettings settings, CancellationToken cancelToken = default)
    {
        using var guestCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        Task<CSteamID> guestLoopTask = GuestPollLoopAsync(ct => GuestMatchService.TryJoinFromListAsync(stack, settings, ct), guestCts.Token);
        try
        {
            while (true)
            {
                cancelToken.ThrowIfCancellationRequested();

                using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
                Task<List<MatchLobbyInfo>> searchTask = MatchSearchService.SearchWithProgressiveRegionAsync(
                    settings.Community, settings.RequireModMatch, SearchMaxPlayers(settings), MatchTags.KindFresh, settings.Language, settings.GameMode,
                    AscensionSearchFilter.For(settings.Ascension), searchCts.Token);

                Task winner = await Task.WhenAny(guestLoopTask, searchTask);
                if (winner == guestLoopTask)
                {
                    searchCts.Cancel(); // best-effort - pure Steam query, nothing to unwind
                    await AwaitQuietlyAsync(searchTask);
                    return AutoMatchResult.Joined(await guestLoopTask);
                }

                List<MatchLobbyInfo> found = await searchTask;
                if (found.Count > 0)
                {
                    if (await TryGuestWinAsync(guestCts, guestLoopTask, cancelToken) is { } guestWin)
                    {
                        return guestWin;
                    }
                    Log.Info($"[sts2_matchmaker] AutoMatch found {found.Count} lobbies, joining {found[0].LobbyId.m_SteamID}");
                    return AutoMatchResult.Join(found[0].LobbyId);
                }

                if (!settings.CanHost)
                {
                    // No extra fixed delay here - a full progressive-region search pass already took a while, and
                    // adding a flat wait on top of it just stacked further behind the guest loop's own cadence
                    // for no benefit.
                    continue;
                }

                int jitterMs = Random.Shared.Next(300, 900);
                await Task.Delay(jitterMs, cancelToken);

                // Same race as the first search - this pass used to be a plain sequential await, which meant a
                // guest win during these ~6-8s wasn't noticed until the whole recheck finished.
                using var recheckCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
                Task<List<MatchLobbyInfo>> recheckTask = MatchSearchService.SearchWithProgressiveRegionAsync(
                    settings.Community, settings.RequireModMatch, SearchMaxPlayers(settings), MatchTags.KindFresh, settings.Language, settings.GameMode,
                    AscensionSearchFilter.For(settings.Ascension), recheckCts.Token);

                Task recheckWinner = await Task.WhenAny(guestLoopTask, recheckTask);
                if (recheckWinner == guestLoopTask)
                {
                    recheckCts.Cancel();
                    await AwaitQuietlyAsync(recheckTask);
                    return AutoMatchResult.Joined(await guestLoopTask);
                }

                List<MatchLobbyInfo> recheck = await recheckTask;
                if (recheck.Count > 0)
                {
                    if (await TryGuestWinAsync(guestCts, guestLoopTask, cancelToken) is { } guestWin)
                    {
                        return guestWin;
                    }
                    Log.Info($"[sts2_matchmaker] AutoMatch found {recheck.Count} lobbies after {jitterMs}ms jitter, joining {recheck[0].LobbyId.m_SteamID}");
                    return AutoMatchResult.Join(recheck[0].LobbyId);
                }

                return await CommitToHostAsync(stack, settings, guestCts, guestLoopTask, cancelToken);
            }
        }
        finally
        {
            // Last-resort safety net for exit paths that skip every stop-the-guest-loop call above (e.g. the
            // outer cancelToken firing while a search task is in flight, which throws straight out of `await
            // searchTask`/`recheckTask` past all of them) - guestLoopTask is already fully awaited on every path
            // that reaches here normally, so this is a no-op then; it only does real work on those skipped paths,
            // where it's the only thing standing between this method returning and GuestMatchService still being
            // mid-attempt on `stack`.
            guestCts.Cancel();
            await AwaitQuietlyAsync(guestLoopTask);
        }
    }

    /// <summary>Polls a guest-match function - GuestMatchService.TryJoinFromListAsync or TryJoinRehostFromListAsync,
    /// wrapped as a delegate so both AutoMatchAsync and WaitForRehostAsync share this one loop instead of each
    /// carrying a near-identical copy - on its own fixed cadence (GuestLobbyListClient.MinFetchInterval) until it
    /// joins something or gets cancelled, decoupled from whatever the caller's own-mod search loop is doing so a
    /// slow search pass never delays the next guest-list check. Return type is deliberately non-nullable: this
    /// only ever completes by actually finding something or throwing OperationCanceledException (GuestMatchService
    /// itself wraps every per-candidate check so nothing else can fault it) - typing it CSteamID? just to satisfy
    /// the inner tryJoin call's own per-attempt "found nothing yet" signal would leak a null state callers can
    /// never actually observe, forcing pointless null-forgiving at every await site.</summary>
    private static async Task<CSteamID> GuestPollLoopAsync(Func<CancellationToken, Task<CSteamID?>> tryJoin, CancellationToken cancelToken)
    {
        while (true)
        {
            cancelToken.ThrowIfCancellationRequested();
            CSteamID? guestJoined = await tryJoin(cancelToken);
            if (guestJoined.HasValue)
            {
                return guestJoined.Value;
            }
            await Task.Delay(GuestLobbyListClient.MinFetchInterval, cancelToken);
        }
    }

    /// <summary>Cancels the guest loop and waits for it to fully unwind before returning - required before
    /// touching `stack` again, since GuestMatchService might be mid-attempt on that same stack. Returns whatever
    /// it managed to join in the process, if anything (a last-second success right as cancellation landed still
    /// counts, and the caller should prefer it over whatever prompted the stop). Note this can take a moment if
    /// the guest loop is mid-join: MatchJoinService.JoinLobbyAsync has no cancellation token of its own, so
    /// cancelling here only stops the loop between candidates, not a connection attempt already underway.
    ///
    /// A cancelled guestLoopTask is only swallowed into a null result when `guestCts` is what caused it (i.e. we
    /// are the ones stopping it on purpose, because the other side already won). If the caller's own `cancelToken`
    /// is the real reason (the player pressed 취소), that has to propagate instead of being silently treated as
    /// "guest found nothing" - swallowing it right before MatchHostService.StartFreshHostAsync would mean
    /// creating an uncancellable lobby after the player already cancelled matching.</summary>
    private static async Task<CSteamID?> StopGuestLoopAsync(CancellationTokenSource guestCts, Task<CSteamID> guestLoopTask, CancellationToken cancelToken)
    {
        guestCts.Cancel();
        try
        {
            return await guestLoopTask;
        }
        catch (OperationCanceledException)
        {
            cancelToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>Thin wrapper around StopGuestLoopAsync for the common case: stop the guest loop, and if it turns
    /// out to have won in that exact window, hand that back as the whole method's final result - null means it
    /// found nothing, so the caller is clear to proceed with whatever prompted the stop (joining a search-found
    /// candidate, or - via CommitToHostAsync - becoming host).</summary>
    private static async Task<AutoMatchResult?> TryGuestWinAsync(CancellationTokenSource guestCts, Task<CSteamID> guestLoopTask, CancellationToken cancelToken)
    {
        CSteamID? guestWonInstead = await StopGuestLoopAsync(guestCts, guestLoopTask, cancelToken);
        return guestWonInstead.HasValue ? AutoMatchResult.Joined(guestWonInstead.Value) : null;
    }

    /// <summary>Awaits a task purely to observe it (so a genuine fault doesn't become an unobserved-task-exception
    /// later, and so the caller knows it's truly finished) without caring about its result or how it ended -
    /// cancellation, a real exception, and success are all equally fine here.</summary>
    private static async Task AwaitQuietlyAsync<T>(Task<T> task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Swallowed intentionally - see summary above.
        }
    }

    /// <summary>The one uncancellable, `stack`-touching commitment in this whole flow. Bundled together with its
    /// own guest-loop stop-and-check rather than leaving "call TryGuestWinAsync first" as a step every future
    /// caller has to remember on its own - the only way to reach MatchHostService.StartFreshHostAsync from
    /// anywhere in this class is through here, so there's no code path left where that call could ever happen
    /// without the guest loop having been stopped first.</summary>
    private static async Task<AutoMatchResult> CommitToHostAsync(
        NSubmenuStack stack, MatchSettings settings, CancellationTokenSource guestCts, Task<CSteamID> guestLoopTask, CancellationToken cancelToken)
    {
        if (await TryGuestWinAsync(guestCts, guestLoopTask, cancelToken) is { } guestWin)
        {
            return guestWin;
        }

        Log.Info("[sts2_matchmaker] AutoMatch found nothing, becoming host");
        HostResult hostResult = await MatchHostService.StartFreshHostAsync(stack, settings.Community, ResolveHostLanguage(settings), settings.GameMode, ResolveHostMaxPlayers(settings), settings.Ascension, openMatchingWindowOnHost: true);
        return hostResult.Error != null ? AutoMatchResult.Fail(hostResult.Error) : AutoMatchResult.Host(hostResult);
    }

    /// <summary>
    /// For participants of a broken multiplayer run who aren't the one re-opening it: never hosts, just keeps
    /// searching kind=rehost lobbies (tagged separately from fresh ones) until a teammate reopens the save and this
    /// finds it, then joins immediately. No community name, language, or mod-match preference needed anymore -
    /// eligibility is checked directly against the lobby's advertised participant SteamID64 list (see
    /// MatchTags.RehostParticipantsKey), which is exact and can't be thrown off by mistyped/mismatched settings
    /// the way a community-name filter could. Game mode isn't filtered on by a preference either, since the save
    /// itself fixes the mode - this just tries all three so it doesn't need to know it in advance.
    ///
    /// Races its own-mod search against the DC-crawled guest fallback the same way AutoMatchAsync does (see that
    /// method's own doc, including the finally-block safety net) - simpler here since this path never hosts, so
    /// there's no uncancellable stack-touching step to guard against; only a found search candidate needs the
    /// guest loop stopped (and double-checked) first.
    /// </summary>
    public static async Task<AutoMatchResult> WaitForRehostAsync(NSubmenuStack stack, CancellationToken cancelToken = default)
    {
        ulong myId = SteamUser.GetSteamID().m_SteamID;
        using var guestCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        Task<CSteamID> guestLoopTask = GuestPollLoopAsync(ct => GuestMatchService.TryJoinRehostFromListAsync(stack, ct), guestCts.Token);
        try
        {
            while (true)
            {
                cancelToken.ThrowIfCancellationRequested();

                using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
                Task<MatchLobbyInfo?> searchTask = SearchRehostAcrossModesAsync(myId, searchCts.Token);

                Task winner = await Task.WhenAny(guestLoopTask, searchTask);
                if (winner == guestLoopTask)
                {
                    searchCts.Cancel();
                    await AwaitQuietlyAsync(searchTask);
                    return AutoMatchResult.Joined(await guestLoopTask);
                }

                MatchLobbyInfo? found = await searchTask;
                if (found.HasValue)
                {
                    if (await TryGuestWinAsync(guestCts, guestLoopTask, cancelToken) is { } guestWin)
                    {
                        return guestWin;
                    }
                    Log.Info($"[sts2_matchmaker] Rehost found, joining {found.Value.LobbyId.m_SteamID}");
                    return AutoMatchResult.Join(found.Value.LobbyId);
                }
            }
        }
        finally
        {
            // See AutoMatchAsync's own finally block - same safety net, same reasoning.
            guestCts.Cancel();
            await AwaitQuietlyAsync(guestLoopTask);
        }
    }

    /// <summary>One pass across all 3 modes, then a pacing delay before returning empty - unlike the fresh-match
    /// progressive-region search, this has no built-in inter-step delay of its own (just 3 plain Steam queries), so
    /// it needs one explicitly to avoid hammering RequestLobbyList in a tight loop once decoupled from the guest
    /// loop's own cadence.</summary>
    private static async Task<MatchLobbyInfo?> SearchRehostAcrossModesAsync(ulong myId, CancellationToken cancelToken)
    {
        foreach (GameMode mode in new[] { GameMode.Standard, GameMode.Daily, GameMode.Custom })
        {
            List<MatchLobbyInfo> found = await MatchSearchService.SearchAsync(
                communityFilter: null, requireModMatch: false, maxPlayers: null, MatchTags.KindRehost, language: null,
                ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide, mode, AscensionSearchFilter.None, myId, cancelToken);
            if (found.Count > 0)
            {
                return found[0];
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(3), cancelToken);
        return null;
    }
}
