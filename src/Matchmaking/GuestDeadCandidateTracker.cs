using System.Collections.Generic;
using System.Linq;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Remembers DC-crawled candidates (by `no`) that failed a Steam RequestLobbyData check in GuestMatchService - the
/// recruit board can keep serving a stale post for a while after the underlying Steam lobby actually closed, and
/// without this every single AutoMatch retry cycle re-pays GuestLobbyDataAwaiter's ~5s timeout for the same dead
/// post. Prune drops a `no` the moment GuestLobbyListClient's crawl stops returning it at all - if that `no` were
/// ever reused for a fresh post, it should get a clean re-check rather than staying blacklisted forever.
/// </summary>
internal static class GuestDeadCandidateTracker
{
    private static readonly HashSet<string> DeadNos = new();

    public static bool IsDead(string no) => DeadNos.Contains(no);

    public static void MarkDead(string no) => DeadNos.Add(no);

    public static void Prune(IReadOnlyList<GuestLobby> currentLobbies)
    {
        if (DeadNos.Count == 0)
        {
            return;
        }
        var currentNos = new HashSet<string>(currentLobbies.Select(lobby => lobby.No));
        DeadNos.RemoveWhere(no => !currentNos.Contains(no));
    }
}
