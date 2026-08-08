using System.Diagnostics;
using Steamworks;

if (!Packsize.Test())
{
    Console.WriteLine("Packsize.Test() failed - wrong steam_api64.dll bitness?");
    return 1;
}
if (!DllCheck.Test())
{
    Console.WriteLine("DllCheck.Test() failed - steam_api64.dll build mismatch?");
    return 1;
}
if (!SteamAPI.Init())
{
    Console.WriteLine("SteamAPI.Init() failed - is Steam running and are you logged in?");
    return 1;
}

Console.WriteLine("Steam initialized. Searching for sts2_matchmaker lobbies (sts2mm_active=1)...");

bool done = false;
CallResult<LobbyMatchList_t> callResult = CallResult<LobbyMatchList_t>.Create((result, ioError) =>
{
    if (ioError)
    {
        Console.WriteLine("RequestLobbyList IO error.");
        done = true;
        return;
    }

    Console.WriteLine($"Found {result.m_nLobbiesMatching} matching lobbies:");
    for (int i = 0; i < result.m_nLobbiesMatching; i++)
    {
        CSteamID lobbyId = SteamMatchmaking.GetLobbyByIndex(i);
        CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobbyId);
        string kind = SteamMatchmaking.GetLobbyData(lobbyId, "sts2mm_kind");
        string community = SteamMatchmaking.GetLobbyData(lobbyId, "sts2mm_community");
        string language = SteamMatchmaking.GetLobbyData(lobbyId, "sts2mm_language");
        string gameMode = SteamMatchmaking.GetLobbyData(lobbyId, "sts2mm_gamemode");
        string ascension = SteamMatchmaking.GetLobbyData(lobbyId, "sts2mm_asc");
        string maxPlayersTag = SteamMatchmaking.GetLobbyData(lobbyId, "sts2mm_maxplayers");
        string version = SteamMatchmaking.GetLobbyData(lobbyId, "sts2mm_version");
        string modHash = SteamMatchmaking.GetLobbyData(lobbyId, "sts2mm_modhash");
        int members = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
        int maxMembers = SteamMatchmaking.GetLobbyMemberLimit(lobbyId);
        Console.WriteLine($"  lobby={lobbyId.m_SteamID} owner={owner.m_SteamID} kind={kind} members={members}/{maxMembers} (tag maxPlayers={maxPlayersTag}) community='{community}' language='{language}' gameMode={gameMode} ascension={ascension} version='{version}' modHash={modHash}");
    }
    done = true;
});

SteamMatchmaking.AddRequestLobbyListStringFilter("sts2mm_active", "1", ELobbyComparison.k_ELobbyComparisonEqual);
SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
callResult.Set(call);

var stopwatch = Stopwatch.StartNew();
while (!done && stopwatch.Elapsed.TotalSeconds < 15)
{
    SteamAPI.RunCallbacks();
    Thread.Sleep(100);
}

if (!done)
{
    Console.WriteLine("Timed out waiting for RequestLobbyList response.");
}

SteamAPI.Shutdown();
return 0;
