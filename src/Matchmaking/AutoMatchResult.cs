using Steamworks;

namespace Sts2Matchmaker.Matchmaking;

public enum AutoMatchOutcome
{
    JoinExisting,
    BecameHost,
    Error,
}

public readonly record struct AutoMatchResult(AutoMatchOutcome Outcome, CSteamID? LobbyId, string? Error)
{
    public static AutoMatchResult Join(CSteamID lobbyId) => new(AutoMatchOutcome.JoinExisting, lobbyId, null);
    public static AutoMatchResult Host(HostResult hostResult) => new(AutoMatchOutcome.BecameHost, null, hostResult.Error);
    public static AutoMatchResult Fail(string error) => new(AutoMatchOutcome.Error, null, error);
}
