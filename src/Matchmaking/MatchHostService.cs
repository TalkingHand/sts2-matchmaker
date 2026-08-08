using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using Sts2Matchmaker.Localization;
using Steamworks;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Starts a Steam-hosted lobby the same way the vanilla multiplayer submenu does (fresh run in any of the three
/// game modes, or a re-opened save after a broken run), but keeps a handle to the created lobby so it can be
/// widened past FriendsOnly and tagged for matchmaking search.
/// </summary>
public static class MatchHostService
{
    public const int DefaultMaxPlayers = 4;
    public const int MinPlayers = 2;
    public const int MaxPlayers = 4;

    /// <summary>Set right before InitializeMultiplayerAsHost when StartFreshHostAsync was reached via the auto-match
    /// "search first, host only if nobody found" flow (AutoMatchService), so the next
    /// CharacterSelectRecruitPatch/CustomRunRecruitPatch/DailyRunRecruitPatch postfix knows to auto-open
    /// MatchConditionsWindow once the screen actually settles - a host who explicitly configured "인원 수" and
    /// pressed 매칭 시작 should immediately see the matching-options screen showing that search is live, not land
    /// silently in a bare lobby. Consumed (reset to false) by RecruitToggleInjector.Inject - stays false for a plain
    /// vanilla Host action (never set). For Standard/Custom this postfix fires from INSIDE
    /// InitializeMultiplayerAsHost itself, before this method even gets to stack.Push; for Daily,
    /// DailyRunRecruitPatch instead fires later off AfterLobbyInitialized (see that class's own doc for why), but
    /// the flag is a static read at whatever point Inject eventually runs, so the timing difference doesn't matter
    /// here - it's still set before that point either way.</summary>
    public static bool OpenMatchingWindowOnNextInject;

    public static async Task<HostResult> StartFreshHostAsync(NSubmenuStack stack, string communityName, string language, GameMode gameMode, int maxPlayers = DefaultMaxPlayers, int ascension = MatchTags.AscensionAny, bool openMatchingWindowOnHost = false)
    {
        // Daily's own StartRunLobby setup hardcodes a 4-player lobby internally regardless of what the Steam-level
        // cap was, so a smaller custom count here would be misleading - keep vanilla's fixed 4 for Daily.
        int effectiveMaxPlayers = gameMode == GameMode.Daily ? DefaultMaxPlayers : maxPlayers;

        // We're the only member at creation time, so the room can reach exactly our own unlocked ceiling; once the
        // run screen exists, RecruitToggleInjector re-tags from StartRunLobby.MaxAscension on every roster change.
        int initialAscension = MatchLobbyTagging.ResolveRoomAscension(MatchTags.LocalMaxAscension, ascension);

        Log.Info($"[sts2_matchmaker] Starting fresh match host (mode={gameMode}, maxPlayers={effectiveMaxPlayers}, community='{communityName}', language='{language}', ascension={initialAscension})");
        (string? err, NetHostGameService? netService, CSteamID lobbyId) = await CreateAndTagLobbyAsync(communityName, language, gameMode, effectiveMaxPlayers, MatchTags.KindFresh, initialAscension);
        if (err != null || netService == null)
        {
            return HostResult.Failure(err ?? Loc.Get("알 수 없는 오류"));
        }

        try
        {
            switch (gameMode)
            {
                case GameMode.Daily:
                {
                    NDailyRunScreen screen = stack.GetSubmenuType<NDailyRunScreen>();
                    OpenMatchingWindowOnNextInject = openMatchingWindowOnHost;
                    screen.InitializeMultiplayerAsHost(netService);
                    stack.Push(screen);
                    break;
                }
                case GameMode.Custom:
                {
                    NCustomRunScreen screen = stack.GetSubmenuType<NCustomRunScreen>();
                    OpenMatchingWindowOnNextInject = openMatchingWindowOnHost;
                    screen.InitializeMultiplayerAsHost(netService, effectiveMaxPlayers);
                    stack.Push(screen);
                    break;
                }
                default:
                {
                    NCharacterSelectScreen screen = stack.GetSubmenuType<NCharacterSelectScreen>();
                    OpenMatchingWindowOnNextInject = openMatchingWindowOnHost;
                    screen.InitializeMultiplayerAsHost(netService, effectiveMaxPlayers);
                    stack.Push(screen);
                    break;
                }
            }
            Log.Info("[sts2_matchmaker] Fresh host ready, pushed run screen");
            return HostResult.Success(netService, lobbyId);
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Exception pushing run screen: {ex}");
            netService.Disconnect(NetError.InternalError);
            return HostResult.Failure(string.Format(Loc.Get("매칭 설정 중 오류: {0}"), ex.Message));
        }
    }

    // Re-hosting a saved multiplayer run is no longer triggered from here - the vanilla "세이브로 호스트" button
    // (NMultiplayerSubmenu's LoadButton) does that already, we just tag whatever lobby it creates right after the
    // fact (see Patches/VanillaRehostTagPatch.cs), instead of building a parallel custom-triggered flow that
    // duplicates vanilla's own load-screen navigation for every game mode.

    private static async Task<(string? error, NetHostGameService? netService, CSteamID lobbyId)> CreateAndTagLobbyAsync(string communityName, string language, GameMode gameMode, int maxPlayers, string kind, int ascension)
    {
        var netService = new NetHostGameService();
        NetErrorInfo? err = await netService.StartSteamHost(maxPlayers);
        if (err.HasValue)
        {
            Log.Error($"[sts2_matchmaker] StartSteamHost failed: {err.Value}");
            return (string.Format(Loc.Get("호스트 시작 실패: {0}"), err.Value), null, default);
        }

        string? rawLobbyId = netService.GetRawLobbyIdentifier();
        if (rawLobbyId == null || !ulong.TryParse(rawLobbyId, out ulong lobbyIdValue))
        {
            Log.Error("[sts2_matchmaker] StartSteamHost succeeded but returned no usable lobby id");
            netService.Disconnect(NetError.InternalError);
            return (Loc.Get("로비 ID를 가져오지 못했습니다."), null, default);
        }

        var lobbyId = new CSteamID(lobbyIdValue);
        Log.Info($"[sts2_matchmaker] Host lobby created: {lobbyId.m_SteamID} (kind={kind}, mode={gameMode})");

        try
        {
            MatchLobbyTagging.ApplyTags(lobbyId, communityName, language, gameMode, maxPlayers, kind, ascension);
            await BanTagSync.MergeMyBansIntoLobbyAsync(lobbyId);
            return (null, netService, lobbyId);
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Exception tagging lobby: {ex}");
            netService.Disconnect(NetError.InternalError);
            return (string.Format(Loc.Get("매칭 태그 설정 중 오류: {0}"), ex.Message), null, default);
        }
    }
}
