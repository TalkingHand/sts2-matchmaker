using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Steam lobby metadata keys used for matchmaking search filters (SetLobbyData / AddRequestLobbyListStringFilter).
/// </summary>
public static class MatchTags
{
    public const string ActiveKey = "sts2mm_active";
    public const string ActiveValue = "1";
    public const string ModHashKey = "sts2mm_modhash";
    public const string CommunityKey = "sts2mm_community";
    public const string GameModeKey = "sts2mm_gamemode";
    public const string MaxPlayersKey = "sts2mm_maxplayers";
    public const string LanguageKey = "sts2mm_language";

    /// <summary>
    /// The ascension level this lobby will actually play at. Deliberately NOT StartRunLobby.Ascension (the host can
    /// set that to anything at any time) - it's the level the room can reach at all, i.e. StartRunLobby.MaxAscension
    /// capped by the host's own preference. See MatchLobbyTagging.ResolveRoomAscension.
    /// </summary>
    public const string AscensionKey = "sts2mm_asc";

    /// <summary>Sentinel for "don't care about ascension" - the default, and what an unset filter means.</summary>
    public const int AscensionAny = -1;

    /// <summary>Sentinel for "don't care about the 인원 수 target" - only offered where the room's own real
    /// capacity is already fixed (an already-hosted lobby, not the host-capable main matching popup - see
    /// MatchConditionsPanel's own ShowGameMode-gated "무관" option), so it can never reach
    /// MatchHostService.StartFreshHostAsync as an actual room size to create. See
    /// MatchLobbyTagging.ResolveRoomMaxPlayers, which resolves this the same way ResolveRoomAscension resolves
    /// AscensionAny.</summary>
    public const int MaxPlayersAny = -1;

    /// <summary>
    /// This account's unlocked multiplayer ascension ceiling. Distinct from singleplayer ascension, which is stored
    /// per character (CharacterStats.MaxAscension) - this is one account-wide number that only goes up by winning a
    /// multiplayer run at your current max (ProgressSaveManager.IncrementMultiplayerAscension), so most players sit
    /// at 0 and no two players are likely to share the same ceiling.
    /// </summary>
    public static int LocalMaxAscension => SaveManager.Instance.Progress.MaxMultiplayerAscension;

    /// <summary>
    /// Union of every current lobby member's personal ban list (SteamID64s, pipe-separated). Any member can update
    /// this via SteamMatchmaking.SetLobbyData - it isn't host-exclusive.
    /// </summary>
    public const string BannedKey = "sts2mm_banned";

    /// <summary>
    /// The host's own SteamID64, advertised so a search can check it against the searcher's personal ban list
    /// WITHOUT joining. GetLobbyOwner/GetLobbyMemberByIndex/GetNumLobbyMembers all require actually being a member
    /// of the lobby (confirmed against Valve's own ISteamMatchmaking docs) - they return nothing useful for a
    /// lobby you've only seen via RequestLobbyList, so there's no other way to identify a pre-join lobby's host at
    /// all. See MatchSearchService.SearchAsync.
    /// </summary>
    public const string OwnerKey = "sts2mm_owner";

    /// <summary>
    /// Pipe-separated SteamID64 list of every player who was in the save being re-hosted - the only thing a
    /// rehost search needs to check (see MatchSearchService.SearchAsync's requireParticipantId param). Written
    /// once at rehost-creation time from the save's own player list (SerializableRun.Players[].NetId) - unlike
    /// sts2mm_banned this never needs re-merging later, since eligibility here is a fixed fact about the save
    /// itself, not something that changes with who's currently connected. Community/language/ascension/mod-hash
    /// are no longer used to filter rehost search at all - the participant list alone is both necessary and
    /// sufficient (nobody outside the save can actually join anyway, per the game's own NotInSaveGame check).
    /// </summary>
    public const string RehostParticipantsKey = "sts2mm_rehost_ids";

    /// <summary>
    /// "fresh" = a brand-new lobby anyone can join. "rehost" = a re-opened lobby for a run that already has fixed
    /// players (per the vanilla NotInSaveGame check, only people already in that save can actually join it) -
    /// searched separately so strangers don't see it as a joinable fresh game.
    /// </summary>
    public const string KindKey = "sts2mm_kind";
    public const string KindFresh = "fresh";
    public const string KindRehost = "rehost";

    /// <summary>Exact-match string tags (community, language) all use the same trim+lowercase normalization.</summary>
    public static string NormalizeTag(string? raw)
    {
        return (raw ?? string.Empty).Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Hash of the locally installed gameplay-affecting mod list, using the same source list the game itself
    /// compares during the join handshake (PeerVersionInfo.gameplayAffectingMods), so a search filtered on this
    /// hash only surfaces lobbies that would actually pass the game's own mod-mismatch check.
    /// </summary>
    public static string ComputeGameplayModHash()
    {
        List<string> mods = ModManager.GetGameplayRelevantModNameList() ?? new List<string>();
        string joined = string.Join("|", mods.OrderBy(m => m, StringComparer.Ordinal));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash);
    }
}
