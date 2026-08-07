using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Steamworks;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Joins a matched lobby through the exact same entry point the game's own Steam-invite-link handler uses
/// (SteamJoinCallbackHandler.JoinToHost -&gt; NGame.Instance.MainMenu.JoinGame), instead of grabbing an
/// NJoinFriendScreen instance directly via NSubmenuStack.GetSubmenuType&lt;T&gt;() - that fetches/caches the instance
/// without ever actually pushing it through the normal NMultiplayerSubmenu -&gt; NJoinFriendScreen navigation sequence
/// that NMainMenu.JoinGame performs first. Skipping that left something in an inconsistent state that only broke
/// down deep inside vanilla's own JoinFlow with a NullReferenceException (NetError.InternalError) - reproducible
/// even with every other mod removed on both sides, but never through vanilla Host/Join or a real Steam invite
/// link, both of which go through NMainMenu.JoinGame.
/// </summary>
public static class MatchJoinService
{
    public static async Task JoinLobbyAsync(NSubmenuStack stack, CSteamID lobbyId)
    {
        Log.Info($"[sts2_matchmaker] Joining matched lobby {lobbyId.m_SteamID}");
        var initializer = SteamClientConnectionInitializer.FromLobby(lobbyId.m_SteamID);

        NMainMenu? mainMenu = NGame.Instance?.MainMenu;
        if (mainMenu == null)
        {
            throw new InvalidOperationException("메인 메뉴를 찾을 수 없어 로비에 참가할 수 없습니다.");
        }
        await mainMenu.JoinGame(initializer);

        // JoinGame() itself unconditionally pushes NMultiplayerSubmenu then NJoinFriendScreen onto the stack
        // before the actual network join even begins (see NMainMenu.JoinGame), so comparing stack.Peek() to
        // whatever was there beforehand can't tell success from failure anymore - both push the same two screens
        // first. What differs is what happens *after*: on success, NJoinFriendScreen.JoinGameAsync pushes the
        // actual gameplay screen (NCharacterSelectScreen etc.) on top; on every failure path (thrown exception,
        // silently-swallowed cancellation, or a RunSessionState.Running rejection) it never advances past
        // NJoinFriendScreen. So if we're still sitting on NJoinFriendScreen/NMultiplayerSubmenu once this returns,
        // treat it as a failure - otherwise we'd close our UI and try to tag a lobby we never actually joined.
        if (stack.Peek() is NJoinFriendScreen or NMultiplayerSubmenu)
        {
            throw new InvalidOperationException("로비 참가에 실패했습니다 (런이 이미 시작되었거나 참가가 취소됨).");
        }

        // Reaching here means we're in - contribute our own ban list to this lobby's shared tag now that we're a
        // member and can call SetLobbyData ourselves.
        await BanTagSync.MergeMyBansIntoLobbyAsync(lobbyId);
    }
}
