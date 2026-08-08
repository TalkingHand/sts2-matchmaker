using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Runs;
using Steamworks;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Shared SetLobbyType/SetLobbyData calls, used both when a lobby is first created for matchmaking and when an
/// already-open (vanilla-hosted or friends-only) lobby is later opened up to fill remaining seats via search.
/// </summary>
public static class MatchLobbyTagging
{
    public static void ApplyTags(CSteamID lobbyId, string communityName, string language, GameMode gameMode, int maxPlayers, string kind, int ascension)
    {
        // Vanilla/default hosting creates a FriendsOnly lobby (invisible to RequestLobbyList search).
        SteamMatchmaking.SetLobbyType(lobbyId, ELobbyType.k_ELobbyTypePublic);
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.ActiveKey, MatchTags.ActiveValue);
        // Whoever calls ApplyTags IS the host (fresh hosting, or an already-hosted lobby's owner opening it back
        // up to search) - see MatchTags.OwnerKey's own doc for why this can't just be read off the lobby at
        // search time instead.
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.OwnerKey, SteamUser.GetSteamID().m_SteamID.ToString());
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.ModHashKey, MatchTags.ComputeGameplayModHash());
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.VersionKey, MatchTags.CurrentVersionTag());
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.CommunityKey, MatchTags.NormalizeTag(communityName));
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.LanguageKey, MatchTags.NormalizeTag(language));
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.GameModeKey, gameMode.ToString());
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.MaxPlayersKey, maxPlayers.ToString());
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.KindKey, kind);
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.AscensionKey, ascension.ToString());
    }

    /// <summary>
    /// The level to advertise: what the room can actually reach, capped by the host's own preference. "무관"
    /// contributes no cap of its own and just advertises whatever the room can reach.
    /// </summary>
    public static int ResolveRoomAscension(int lobbyMaxAscension, int preferred) =>
        preferred == MatchTags.AscensionAny ? lobbyMaxAscension : Math.Min(lobbyMaxAscension, preferred);

    /// <summary>
    /// The 인원 수 target to advertise/compare against: same "무관" resolution as ResolveRoomAscension - "무관"
    /// contributes no target of its own and just falls back to the room's own real capacity (StartRunLobby.
    /// MaxPlayers, fixed at creation), same as an explicit pick still gets clamped to what's actually achievable
    /// (can't target more players than the room can physically hold). Used both for the search tag
    /// (MatchLobbyTagging.ApplyTags) and the local auto-stop-when-full check
    /// (RecruitToggleInjector.CheckPlayerLimitNow), so "무관" means the same thing in both places: never an
    /// artificial ceiling below the room's own real size.
    /// </summary>
    public static int ResolveRoomMaxPlayers(int lobbyMaxPlayers, int preferred) =>
        preferred == MatchTags.MaxPlayersAny ? lobbyMaxPlayers : Math.Min(lobbyMaxPlayers, preferred);

    /// <summary>
    /// Tags a freshly-created rehost lobby with exactly who's allowed to find it - see
    /// MatchTags.RehostParticipantsKey's own doc. Kept separate from ApplyTags (not folded into its param list) -
    /// this is rehost-only and has nothing to do with the community/language/ascension/maxPlayers tags fresh
    /// hosting still needs.
    /// </summary>
    public static void SetRehostParticipants(CSteamID lobbyId, IEnumerable<ulong> participantIds)
    {
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.RehostParticipantsKey, string.Join('|', participantIds));
    }

    /// <summary>
    /// Re-writes only the ascension tag, for when the roster changes under an already-tagged lobby.
    ///
    /// StartRunLobby.MaxAscension is the minimum unlocked ascension across every current member, so it drops the
    /// moment a less-progressed player arrives - including one who came in through a friends-list invite and never
    /// passed our search filters at all - and rises again when they leave. Host-only: the client-side
    /// HandlePlayerLeftMessage never calls UpdateMaxMultiplayerAscension, so a participant's local MaxAscension
    /// stays stuck at the low-water mark after that player leaves.
    /// </summary>
    public static void UpdateAscensionTag(CSteamID lobbyId, int roomAscension)
    {
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.AscensionKey, roomAscension.ToString());
    }

    /// <summary>Reverts a lobby back to FriendsOnly and marks it inactive, so it stops showing up in search.</summary>
    public static void RemoveFromSearch(CSteamID lobbyId)
    {
        SteamMatchmaking.SetLobbyData(lobbyId, MatchTags.ActiveKey, "0");
        SteamMatchmaking.SetLobbyType(lobbyId, ELobbyType.k_ELobbyTypeFriendsOnly);
    }
}
