using MegaCrit.Sts2.Core.Multiplayer;
using Steamworks;

namespace Sts2Matchmaker.Matchmaking;

/// <returns>error: null on success, else a user-facing message. netService/lobbyId are set only on success -
/// callers need netService afterwards to run a kick list while recruiting.</returns>
public readonly record struct HostResult(string? Error, NetHostGameService? NetService, CSteamID? LobbyId)
{
    public static HostResult Failure(string error) => new(error, null, null);
    public static HostResult Success(NetHostGameService netService, CSteamID lobbyId) => new(null, netService, lobbyId);
}
