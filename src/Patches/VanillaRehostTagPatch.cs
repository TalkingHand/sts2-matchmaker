using System;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Saves;
using Sts2Matchmaker.Matchmaking;
using Steamworks;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Gives the vanilla "세이브로 호스트" button (NMultiplayerSubmenu.LoadButton -&gt; StartLoad -&gt; StartHostAsync)
/// the same search-tag treatment MatchHostService.StartFreshHostAsync already gives a fresh host, instead of the
/// mod building a whole separate "재모집 시작" trigger/lobby-creation path that duplicates vanilla's own
/// per-game-mode load-screen navigation. Vanilla's own rehost leaves the lobby FriendsOnly and untagged, so
/// without this every participant needs a fresh Steam invite link hand-delivered by the host each time - exactly
/// the inefficiency this whole feature exists to remove.
///
/// Hooks InitializeAsHost(INetGameService, SerializableRun) - the same method MatchHostService's old
/// StartRehostAsync used to call directly - on all three per-game-mode load screens, mirroring how
/// CharacterSelectRecruitPatch/CustomRunRecruitPatch hook InitializeMultiplayerAsHost for a FRESH host. This
/// method only exists on the HOST-side load flow (guests get InitializeAsClient instead, never patched here), so
/// there's no risk of tagging a lobby someone is merely joining into.
///
/// Also hooks each screen's own BeginRun (fires once the ready-check gate - ShouldAllowRunToBegin - actually lets
/// the reopened run start) to un-tag the lobby right then, rather than leaving it search-visible for the rest of
/// the run and relying solely on Steam's own slots-available filter to stop surfacing it once full - see
/// VanillaRehostTagShared.ClearRehostSearchTagIfPending's own doc.
/// </summary>
internal static class VanillaRehostTagShared
{
    // Sees only this client's OWN rehost attempts (TagAsRehostLobby only ever runs off InitializeAsHost, which
    // only fires for the host, never a joining guest) - a bare nullable is enough since a player can only be
    // hosting one rehost at a time. Consumed (cleared) by ClearRehostSearchTagIfPending once the run actually
    // begins; left set (and the tag left active) for as long as the room is still short a player, matching
    // CheckPlayerLimitNow's same "still legitimately searching" reasoning for a fresh lobby.
    private static CSteamID? s_pendingRehostLobbyId;

    public static void TagAsRehostLobby(INetGameService gameService, SerializableRun run)
    {
        try
        {
            string? rawLobbyId = gameService.GetRawLobbyIdentifier();
            if (rawLobbyId == null || !ulong.TryParse(rawLobbyId, out ulong lobbyIdValue))
            {
                Log.Error("[sts2_matchmaker] Vanilla rehost has no usable lobby id to tag");
                return;
            }

            var lobbyId = new CSteamID(lobbyIdValue);
            string language = LocManager.Instance?.Language ?? string.Empty;
            MatchLobbyTagging.ApplyTags(lobbyId, string.Empty, language, run.GameMode, MatchHostService.DefaultMaxPlayers, MatchTags.KindRehost, run.Ascension);
            MatchLobbyTagging.SetRehostParticipants(lobbyId, run.Players.Select(p => p.NetId));
            _ = BanTagSync.MergeMyBansIntoLobbyAsync(lobbyId);
            s_pendingRehostLobbyId = lobbyId;
            Log.Info($"[sts2_matchmaker] Tagged vanilla rehost lobby {lobbyId.m_SteamID} for search (participants={run.Players.Count})");
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to tag vanilla rehost lobby: {ex}");
        }
    }

    /// <summary>
    /// Un-tags the lobby the instant the reopened run actually begins (BeginRun, called once
    /// ShouldAllowRunToBegin's ready-check passes - same OnEmbarkPressed/OnUnreadyPressed-style gate
    /// NCharacterSelectScreen uses), rather than only relying on Steam's own slots-available search filter to
    /// stop surfacing a full room. A guest's own client never sets s_pendingRehostLobbyId in the first place (see
    /// its own doc) so this is a silent no-op for everyone except whichever client is actually hosting.
    /// </summary>
    public static void ClearRehostSearchTagIfPending()
    {
        if (s_pendingRehostLobbyId is not { } lobbyId)
        {
            return;
        }
        s_pendingRehostLobbyId = null;
        try
        {
            MatchLobbyTagging.RemoveFromSearch(lobbyId);
            Log.Info($"[sts2_matchmaker] Rehosted run began - closed lobby {lobbyId.m_SteamID} from matching search");
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to close rehost lobby from search on run start: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), "InitializeAsHost")]
public static class MultiplayerLoadGameRehostTagPatch
{
    [HarmonyPostfix]
    public static void Postfix(INetGameService gameService, SerializableRun run) => VanillaRehostTagShared.TagAsRehostLobby(gameService, run);
}

[HarmonyPatch(typeof(NDailyRunLoadScreen), "InitializeAsHost")]
public static class DailyRunLoadRehostTagPatch
{
    [HarmonyPostfix]
    public static void Postfix(INetGameService gameService, SerializableRun run) => VanillaRehostTagShared.TagAsRehostLobby(gameService, run);
}

[HarmonyPatch(typeof(NCustomRunLoadScreen), "InitializeAsHost")]
public static class CustomRunLoadRehostTagPatch
{
    [HarmonyPostfix]
    public static void Postfix(INetGameService gameService, SerializableRun run) => VanillaRehostTagShared.TagAsRehostLobby(gameService, run);
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), "BeginRun")]
public static class MultiplayerLoadGameRehostBeginPatch
{
    [HarmonyPostfix]
    public static void Postfix() => VanillaRehostTagShared.ClearRehostSearchTagIfPending();
}

[HarmonyPatch(typeof(NDailyRunLoadScreen), "BeginRun")]
public static class DailyRunLoadRehostBeginPatch
{
    [HarmonyPostfix]
    public static void Postfix() => VanillaRehostTagShared.ClearRehostSearchTagIfPending();
}

[HarmonyPatch(typeof(NCustomRunLoadScreen), "BeginRun")]
public static class CustomRunLoadRehostBeginPatch
{
    [HarmonyPostfix]
    public static void Postfix() => VanillaRehostTagShared.ClearRehostSearchTagIfPending();
}
