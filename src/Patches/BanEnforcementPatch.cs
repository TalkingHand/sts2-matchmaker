using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using Sts2Matchmaker.Matchmaking;
using Steamworks;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Backstop for the ban list: NetHostGameService.OnPeerConnected is public and fires for every peer connecting to
/// any host we run (fresh/rehost, Standard/Daily/Custom, whether opened through our matching flow or the vanilla
/// Host button then later exposed via "매칭에 공개") - one patch here covers all of them, unlike the per-screen
/// UI patches. Steam has no way to block a join outright, so this can't stop them from touching the lobby at all,
/// but it disconnects them the instant they connect, before any game state has synced to them. The main defense is
/// still search-side self-filtering (MatchSearchService) - this only matters if someone connects directly by ID
/// without having searched (e.g. a stale invite, or bypassing our mod's search).
///
/// Checks both the host's own local ban list AND the lobby's shared ban tag (sts2mm_banned) - a participant has no
/// kick authority of their own, so without the shared-tag check, someone a mere participant (not the host) has
/// banned could connect to a lobby that participant is in and never get enforced against at all.
/// </summary>
[HarmonyPatch(typeof(NetHostGameService), "OnPeerConnected")]
public static class BanEnforcementPatch
{
    [HarmonyPostfix]
    public static void Postfix(NetHostGameService __instance, ulong peerId)
    {
        try
        {
            if (BanListStore.IsBanned(peerId))
            {
                Log.Info($"[sts2_matchmaker] Peer {peerId} is on the local ban list, disconnecting immediately");
                __instance.DisconnectClient(peerId, NetError.Kicked, now: true);
                return;
            }

            string? rawLobbyId = __instance.GetRawLobbyIdentifier();
            if (rawLobbyId != null && ulong.TryParse(rawLobbyId, out ulong lobbyIdValue)
                && BanTagSync.IsIdBannedInLobby(new CSteamID(lobbyIdValue), peerId))
            {
                Log.Info($"[sts2_matchmaker] Peer {peerId} is on a lobby member's shared ban list, disconnecting immediately");
                __instance.DisconnectClient(peerId, NetError.Kicked, now: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Ban enforcement check failed for peer {peerId}: {ex}");
        }
    }
}
