namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Shared flag read by Patches/GuestJoinErrorSuppressionPatch to silence the game's own NErrorPopup while
/// MatchJoinService.JoinLobbyAsync is called with suppressErrorPopup:true - GuestMatchService uses that to try
/// several DC-crawled join candidates in a row without a failed attempt interrupting the player, since "try the
/// next candidate" is the correct response to a failure there, not a popup.
///
/// Plain static bool, not thread-safe by design: matches every other piece of join-flow state in this codebase
/// (e.g. NJoinFriendScreen's own _currentJoinFlow field), all of which assume the single-threaded Godot main loop.
/// </summary>
internal static class GuestJoinErrorSuppression
{
    public static bool Suppress;
}
