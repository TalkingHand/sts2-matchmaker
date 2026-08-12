using System;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// One-shot Task wrapper around SteamMatchmaking.RequestLobbyData, used to pre-check a DC-crawled lobby (member
/// count, owner) before attempting to actually join it - the same mechanism Steam's own friend-invite UI uses to
/// show room state without joining. Unlike the SteamAPICall_t-based calls the game's own SteamCallResult&lt;T&gt;
/// wraps (see MatchSearchService's use of it for RequestLobbyList), RequestLobbyData reports completion via a
/// persistent Callback&lt;LobbyDataUpdate_t&gt; instead, so it needs its own small bridge.
/// </summary>
internal static class GuestLobbyDataAwaiter
{
    /// <returns>true if the lobby responded with data (GetNumLobbyMembers/GetLobbyMemberLimit/GetLobbyOwner are
    /// now safe to call for it), false on timeout, an unresponsive lobby, or cancellation.</returns>
    public static async Task<bool> RequestAsync(CSteamID lobbyId, TimeSpan timeout, CancellationToken cancelToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        using Callback<LobbyDataUpdate_t> callback = Callback<LobbyDataUpdate_t>.Create(update =>
        {
            if (update.m_ulSteamIDLobby == lobbyId.m_SteamID)
            {
                tcs.TrySetResult(update.m_bSuccess != 0);
            }
        });

        if (!SteamMatchmaking.RequestLobbyData(lobbyId))
        {
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        timeoutCts.CancelAfter(timeout);
        using CancellationTokenRegistration registration = timeoutCts.Token.Register(() => tcs.TrySetResult(false));

        bool gotData = await tcs.Task;
        // The registration above fires on either a real cancellation or a plain timeout - only the former should
        // actually throw (so GuestMatchService's caller unwinds instead of just moving on to the next candidate).
        cancelToken.ThrowIfCancellationRequested();
        return gotData;
    }
}
