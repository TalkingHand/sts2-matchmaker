using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Runs;
using Steamworks;

namespace Sts2Matchmaker.Matchmaking;

public readonly struct MatchLobbyInfo
{
    public readonly CSteamID LobbyId;
    public readonly CSteamID OwnerId;
    public readonly string Community;
    public readonly int MemberCount;
    public readonly int MaxMembers;
    public readonly string Kind;

    public MatchLobbyInfo(CSteamID lobbyId, CSteamID ownerId, string community, int memberCount, int maxMembers, string kind)
    {
        LobbyId = lobbyId;
        OwnerId = ownerId;
        Community = community;
        MemberCount = memberCount;
        MaxMembers = maxMembers;
        Kind = kind;
    }
}

/// <summary>
/// How a search treats a lobby's advertised ascension level. Two separate jobs, which is why one nullable int
/// isn't enough:
///
/// - <see cref="Exact" />: the player picked a level and only wants rooms already sitting at exactly that.
/// - <see cref="SustainableCap" />: my own unlocked ceiling, applied even when the player doesn't care about
///   ascension at all. Joining a room recomputes its level as the minimum across members, so a player with a
///   lower ceiling silently demotes any room above it - including one whose members picked that level on
///   purpose. Staying out of those rooms is what actually protects them, so this is a correctness filter rather
///   than a preference.
///
/// At most one of the two is ever set: an exact pick can't exceed the picker's own ceiling, so it already
/// implies the cap.
/// </summary>
public readonly struct AscensionSearchFilter
{
    public readonly int? Exact;
    public readonly int? SustainableCap;

    private AscensionSearchFilter(int? exact, int? sustainableCap)
    {
        Exact = exact;
        SustainableCap = sustainableCap;
    }

    /// <summary>No ascension filtering whatsoever - for rehost search, where membership is already restricted to
    /// the people who were in that save (and who therefore already cleared its ascension).</summary>
    public static AscensionSearchFilter None => new(null, null);

    public static AscensionSearchFilter For(int preferredAscension) =>
        preferredAscension == MatchTags.AscensionAny
            ? new AscensionSearchFilter(null, MatchTags.LocalMaxAscension)
            : new AscensionSearchFilter(preferredAscension, null);
}

/// <summary>
/// Searches Steam's own lobby list (SteamMatchmaking.RequestLobbyList) for lobbies tagged as matchmaking-active,
/// optionally filtered to an exact community name, language, player-count preference, game mode, and/or an exact
/// gameplay-mod-set match. Region uses Steam's own geolocation-based distance filter rather than a custom tag.
/// Defaults to fresh lobbies only - rehost lobbies only accept players who were already in that save, so they're
/// excluded unless explicitly requested.
/// </summary>
public static class MatchSearchService
{
    public static async Task<List<MatchLobbyInfo>> SearchAsync(
        string? communityFilter,
        bool requireModMatch,
        int? maxPlayers = null,
        string kind = MatchTags.KindFresh,
        string? language = null,
        ELobbyDistanceFilter region = ELobbyDistanceFilter.k_ELobbyDistanceFilterDefault,
        GameMode gameMode = GameMode.Standard,
        AscensionSearchFilter ascension = default,
        ulong? requireParticipantId = null,
        CancellationToken cancelToken = default)
    {
        SteamMatchmaking.AddRequestLobbyListStringFilter(MatchTags.ActiveKey, MatchTags.ActiveValue, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListStringFilter(MatchTags.KindKey, kind, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListStringFilter(MatchTags.GameModeKey, gameMode.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
        // General and public-beta are different game builds - always filtered, never a togglable preference. See
        // MatchTags.CurrentVersionTag.
        string versionTag = MatchTags.CurrentVersionTag();
        SteamMatchmaking.AddRequestLobbyListStringFilter(MatchTags.VersionKey, versionTag, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(region);

        if (requireModMatch)
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(MatchTags.ModHashKey, MatchTags.ComputeGameplayModHash(), ELobbyComparison.k_ELobbyComparisonEqual);
        }

        if (maxPlayers.HasValue)
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(MatchTags.MaxPlayersKey, maxPlayers.Value.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
        }

        string normalizedCommunity = MatchTags.NormalizeTag(communityFilter);
        if (!string.IsNullOrEmpty(normalizedCommunity))
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(MatchTags.CommunityKey, normalizedCommunity, ELobbyComparison.k_ELobbyComparisonEqual);
        }

        string normalizedLanguage = MatchTags.NormalizeTag(language);
        if (!string.IsNullOrEmpty(normalizedLanguage))
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(MatchTags.LanguageKey, normalizedLanguage, ELobbyComparison.k_ELobbyComparisonEqual);
        }

        if (ascension.Exact.HasValue)
        {
            SteamMatchmaking.AddRequestLobbyListNumericalFilter(MatchTags.AscensionKey, ascension.Exact.Value, ELobbyComparison.k_ELobbyComparisonEqual);
        }
        else if (ascension.SustainableCap.HasValue)
        {
            SteamMatchmaking.AddRequestLobbyListNumericalFilter(MatchTags.AscensionKey, ascension.SustainableCap.Value, ELobbyComparison.k_ELobbyComparisonEqualToOrLessThan);
        }

        // Rehost lobbies still want open slots checked here even though membership is further gated by save data.
        SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);
        SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);

        Log.Info($"[sts2_matchmaker] Searching lobbies (kind={kind}, mode={gameMode}, community='{normalizedCommunity}', language='{normalizedLanguage}', version='{versionTag}', region={region}, maxPlayers={maxPlayers}, requireModMatch={requireModMatch}, ascension=exact:{ascension.Exact}/cap:{ascension.SustainableCap})");
        SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
        using var callResult = new SteamCallResult<LobbyMatchList_t>(call, cancelToken);
        LobbyMatchList_t result = await callResult.Task;
        Log.Info($"[sts2_matchmaker] Search returned {result.m_nLobbiesMatching} lobbies");

        var lobbies = new List<MatchLobbyInfo>((int)result.m_nLobbiesMatching);
        for (int i = 0; i < result.m_nLobbiesMatching; i++)
        {
            CSteamID lobbyId = SteamMatchmaking.GetLobbyByIndex(i);
            if (BanTagSync.IsLocalPlayerBannedFromLobby(lobbyId))
            {
                // Someone in this lobby (host or participant) has banned me - stay out of it entirely.
                continue;
            }
            // GetLobbyOwner/GetLobbyMemberByIndex/GetNumLobbyMembers all require actually being a member of the
            // lobby (confirmed against Valve's own ISteamMatchmaking docs) - for a lobby only seen via
            // RequestLobbyList they return nothing usable (owner comes back as CSteamID.Nil, member count as 0),
            // so a previous member-by-member ban scan here silently never matched anything. The advertised owner
            // tag (see MatchTags.OwnerKey) is the only reliable way to know who's hosting before joining.
            ulong.TryParse(SteamMatchmaking.GetLobbyData(lobbyId, MatchTags.OwnerKey), out ulong ownerSteamId);
            if (BanListStore.IsBanned(ownerSteamId))
            {
                // I've personally banned this lobby's host. A ban on a mere PARTICIPANT (not the owner) can't be
                // checked this same way pre-join - Steamworks has no way to list a lobby's members without
                // joining it - so that case relies entirely on the shared tag (sts2mm_banned) instead, via
                // IsLocalPlayerBannedFromLobby above and whichever member already merged their own bans into it.
                continue;
            }
            if (requireParticipantId.HasValue && !IsParticipant(lobbyId, requireParticipantId.Value))
            {
                // Rehost search only: this lobby's advertised participant list (MatchTags.RehostParticipantsKey)
                // doesn't include me, so I was never in the save it's re-hosting - not for me, regardless of
                // what else about it might look like a match.
                continue;
            }
            var ownerId = new CSteamID(ownerSteamId);
            string community = SteamMatchmaking.GetLobbyData(lobbyId, MatchTags.CommunityKey);
            string lobbyKind = SteamMatchmaking.GetLobbyData(lobbyId, MatchTags.KindKey);
            int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
            int maxMembers = SteamMatchmaking.GetLobbyMemberLimit(lobbyId);
            lobbies.Add(new MatchLobbyInfo(lobbyId, ownerId, community, memberCount, maxMembers, lobbyKind));
        }
        return lobbies;
    }

    /// <summary>Checks a rehost lobby's advertised participant tag (see MatchTags.RehostParticipantsKey) for a
    /// given SteamID64 - read straight off GetLobbyData, so (like the owner tag) it works pre-join.</summary>
    private static bool IsParticipant(CSteamID lobbyId, ulong steamId)
    {
        string raw = SteamMatchmaking.GetLobbyData(lobbyId, MatchTags.RehostParticipantsKey);
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }
        foreach (string part in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ulong.TryParse(part, out ulong id) && id == steamId)
            {
                return true;
            }
        }
        return false;
    }

    private static readonly ELobbyDistanceFilter[] RegionTiersNearToFar =
    {
        ELobbyDistanceFilter.k_ELobbyDistanceFilterClose,
        ELobbyDistanceFilter.k_ELobbyDistanceFilterDefault,
        ELobbyDistanceFilter.k_ELobbyDistanceFilterFar,
        ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide,
    };

    /// <summary>
    /// Most matchmaking systems (Overwatch, Valorant, etc.) don't ask the player to pick a search radius up front -
    /// they search nearby first and only widen the net automatically if nothing turns up, so players get the best
    /// ping available without having to reason about a "how far am I willing to go" tradeoff themselves. This does
    /// the same: tries Close, then Default, then Far, then Worldwide, unconditionally (no opt-out - STS2 is
    /// turn-based, so ping isn't expected to be a real problem; revisit with actual data if it turns out to be),
    /// pausing briefly between tiers, and returns as soon as a tier finds anything (or whatever the last tier
    /// found, even if empty, once all tiers are exhausted).
    /// </summary>
    public static async Task<List<MatchLobbyInfo>> SearchWithProgressiveRegionAsync(
        string? communityFilter,
        bool requireModMatch,
        int? maxPlayers,
        string kind,
        string? language,
        GameMode gameMode,
        AscensionSearchFilter ascension = default,
        CancellationToken cancelToken = default)
    {
        for (int i = 0; i < RegionTiersNearToFar.Length; i++)
        {
            List<MatchLobbyInfo> results = await SearchAsync(
                communityFilter, requireModMatch, maxPlayers, kind, language, RegionTiersNearToFar[i], gameMode, ascension, cancelToken: cancelToken);
            bool isLastTier = i == RegionTiersNearToFar.Length - 1;
            if (results.Count > 0 || isLastTier)
            {
                return results;
            }
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancelToken);
        }
        return new List<MatchLobbyInfo>();
    }
}
