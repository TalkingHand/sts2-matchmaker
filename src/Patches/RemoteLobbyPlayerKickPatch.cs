using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using Sts2Matchmaker.Helpers;
using Sts2Matchmaker.Localization;
using Sts2Matchmaker.Matchmaking;
using Sts2Matchmaker.UI;
using Steamworks;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Injects per-player moderation buttons into the vanilla remote-player row of the fresh-lobby character select
/// screen (NRemoteLobbyPlayerContainer), instead of maintaining a separate kick-list popup. Host sees "킥"/"밴"
/// (kick-only vs kick+register) - "밴 등록" (register-only, no kick) would be redundant for a host who can already
/// kick+register in one action via "밴", so it's hidden for them; a guest (no kick authority) sees "밴 등록" only,
/// so they can still personally avoid someone in future matches even though they can't remove them from this
/// lobby. Either way, at most 2 buttons ever show, and never next to your own card - moderating yourself doesn't
/// mean anything.
/// </summary>
[HarmonyPatch(typeof(NRemoteLobbyPlayerContainer), "OnPlayerConnected")]
public static class RemoteLobbyPlayerKickPatch
{
    [HarmonyPostfix]
    public static void Postfix(NRemoteLobbyPlayerContainer __instance, object player)
    {
        try
        {
            // player's declared type (LobbyPlayer on general, StartRunLobbyPlayer on beta) differs between game
            // branches - same struct shape, different name - so it's received as object and read via
            // LobbyPlayerCompat instead of baking either type name into this method's signature (which would
            // throw ReflectionTypeLoadException on whichever branch doesn't have that exact type).
            ulong peerId = LobbyPlayerCompat.GetId(player);

            StartRunLobby? lobby = Traverse.Create(__instance).Field("_lobby").GetValue<StartRunLobby>();
            if (lobby == null)
            {
                return;
            }

            bool isHost = lobby.NetService.Type == NetGameType.Host;
            bool isSelf = peerId == lobby.NetService.NetId;
            if (isSelf)
            {
                return;
            }

            List<NRemoteLobbyPlayer>? nodes = Traverse.Create(__instance).Field("_nodes").GetValue<List<NRemoteLobbyPlayer>>();
            NRemoteLobbyPlayer? row = nodes?.LastOrDefault(n => n.PlayerId == peerId);
            if (row == null || row.GetNodeOrNull("Sts2MatchmakerModerationRow") != null)
            {
                return;
            }

            // RE-APPLIED (was briefly reverted while the invite-button disappearance was suspected to be caused by
            // this positioning - currently being independently verified as possibly unrelated to this code).
            // Per remote_player_container.tscn, the actual parent (%Container, a vertical FlowContainer) is
            // 518x258 - each 192-wide card only uses a fraction of that width, leaving ~326px of genuinely empty
            // space to its right (cards stack vertically, so nothing else ever occupies that space). And
            // remote_lobby_player.tscn's root Control never sets clip_contents, so a child positioned past x=192
            // renders normally out there instead of being cut off. So instead of shrinking buttons to fit inside
            // the card, this places them just to the RIGHT of it, in that free space - full 50x50 (matching the
            // "⚙" settings button exactly, as originally requested) with zero risk of covering the nameplate or
            // portrait, since it's not even in the same horizontal territory as either.
            var moderationRow = new HBoxContainer { Name = "Sts2MatchmakerModerationRow" };
            moderationRow.AddThemeConstantOverride("separation", 12);
            moderationRow.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            // 8px gap after the card's own 192px width. Vertically centered for a 50-tall row in the 68-tall card
            // ((68-50)/2 = 9). No fixed OffsetRight/Bottom (unlike the old fixed-3-icon-width version) - these are
            // now BuildTextActionButton, which auto-sizes to its own text, so the HBoxContainer sizes itself to
            // whatever its 1-2 children actually need instead of a width we'd have to keep in sync by hand.
            moderationRow.OffsetLeft = 200f;
            moderationRow.OffsetTop = 9f;
            row.AddChild(moderationRow);

            // Explicit hex red instead of a hue/sat/value shader approximation - the approximated hue=0.6 (a
            // reasonable "measured red" against the pill texture) rendered too pink/magenta once actually seen
            // against StyleBoxFlat's flat fill, so this uses the exact requested color directly instead.
            var buttonColor = new Color("991816");
            const float buttonHeight = 50f; // matches the "⚙" settings button's height

            if (isHost)
            {
                var hostService = (INetHostGameService)lobby.NetService;

                Button kickButton = Sts2ModalPanel.BuildTextActionButton(Loc.Get("킥"), buttonHeight, explicitColor: buttonColor);
                kickButton.TooltipText = Loc.Get("킥 (밴 없이 내보내기만)");
                kickButton.Pressed += () =>
                {
                    Log.Info($"[sts2_matchmaker] Kicking peer {peerId} from lobby UI");
                    hostService.DisconnectClient(peerId, NetError.Kicked, now: true);
                };
                moderationRow.AddChild(kickButton);

                Button banButton = Sts2ModalPanel.BuildTextActionButton(Loc.Get("밴"), buttonHeight, explicitColor: buttonColor);
                banButton.TooltipText = Loc.Get("밴 (즉시 킥 + 밴 목록 등록)");
                banButton.Pressed += () =>
                {
                    // Empty nickname is stored as-is (see BanListStore.ResolveNickname) so a later BanListPanel
                    // open can re-resolve it - but the confirm dialog still needs something to show right now.
                    string nickname = BanListStore.ResolveNickname(peerId);
                    string targetLabel = string.IsNullOrEmpty(nickname) ? peerId.ToString() : nickname;
                    BanConfirmPanel.Show(targetLabel, alsoKick: true, reason =>
                    {
                        Log.Info($"[sts2_matchmaker] Banning peer {peerId} from lobby UI (reason: {reason})");
                        BanListStore.Add(peerId, nickname, reason);
                        hostService.DisconnectClient(peerId, NetError.Kicked, now: true);

                        string? rawLobbyId = lobby.NetService.GetRawLobbyIdentifier();
                        if (rawLobbyId != null && ulong.TryParse(rawLobbyId, out ulong lobbyIdValue))
                        {
                            _ = BanTagSync.MergeMyBansIntoLobbyAsync(new CSteamID(lobbyIdValue));
                        }
                    });
                };
                moderationRow.AddChild(banButton);
            }
            else
            {
                // Register-only ("밴 등록"/"밴 해제" toggle) - a host already has "밴" for kick+register in one
                // action, so showing this too would just be a redundant second way to do a subset of that.
                string registerTooltip = Loc.Get("밴 등록 (지금 킥하지 않고, 다음부터 매칭 제외)");
                Button registerButton = Sts2ModalPanel.BuildTextActionButton(Loc.Get("밴 등록"), buttonHeight, explicitColor: buttonColor);
                registerButton.Pressed += () =>
                {
                    if (BanListStore.IsBanned(peerId))
                    {
                        BanListStore.Remove(peerId);
                        string? unbanRawLobbyId = lobby.NetService.GetRawLobbyIdentifier();
                        if (unbanRawLobbyId != null && ulong.TryParse(unbanRawLobbyId, out ulong unbanLobbyIdValue))
                        {
                            BanTagSync.PruneIdFromLobby(new CSteamID(unbanLobbyIdValue), peerId);
                        }
                        Log.Info($"[sts2_matchmaker] Removed ban registration for peer {peerId} from lobby UI");
                        BanRegisterButtonHelper.UpdateAppearance(registerButton, peerId, registerTooltip);
                        return;
                    }
                    string nickname = BanListStore.ResolveNickname(peerId);
                    string targetLabel = string.IsNullOrEmpty(nickname) ? peerId.ToString() : nickname;
                    BanConfirmPanel.Show(targetLabel, alsoKick: false, reason =>
                    {
                        Log.Info($"[sts2_matchmaker] Registering ban for peer {peerId} from lobby UI (reason: {reason}), no kick");
                        BanListStore.Add(peerId, nickname, reason);
                        BanRegisterButtonHelper.UpdateAppearance(registerButton, peerId, registerTooltip);

                        string? rawLobbyId = lobby.NetService.GetRawLobbyIdentifier();
                        if (rawLobbyId != null && ulong.TryParse(rawLobbyId, out ulong lobbyIdValue))
                        {
                            _ = BanTagSync.MergeMyBansIntoLobbyAsync(new CSteamID(lobbyIdValue));
                        }
                    });
                };
                // Reflects reality immediately - this player might already be on the ban list from a previous
                // session (e.g. they reconnected), not just from clicking the button just now.
                BanRegisterButtonHelper.UpdateAppearance(registerButton, peerId, registerTooltip);
                moderationRow.AddChild(registerButton);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to inject kick/ban buttons: {ex}");
        }
    }
}
