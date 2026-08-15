using System;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using Sts2Matchmaker.Localization;
using Sts2Matchmaker.Matchmaking;
using Steamworks;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Builds and injects the in-lobby "매칭" button shared by the Standard (NCharacterSelectScreen) and Custom
/// (NCustomRunScreen) lobby screens - both expose a public StartRunLobby Lobby property and use the same
/// NRemoteLobbyPlayerContainer, so a single implementation covers both via their respective thin Harmony patches.
///
/// There is no separate "⚙" settings button anymore - clicking this button always opens MatchConditionsWindow
/// (conditions + the actual open/close-to-matching toggle live there now), rather than toggling directly. Always
/// labeled "매칭", never "매칭 취소" - MatchConditionsWindow can't be closed while a search is actually active (see
/// its own OnCloseRequested override), so this row is never visible while there's anything to reflect as "in
/// progress" in the first place.
///
/// Styled to match the vanilla "초대" (invite) button using the exact same background texture/shader/fonts (see
/// remote_player_container.tscn), rather than cloning NInvitePlayersButton itself - that node overrides its own
/// click behavior internally (opens the Steam invite dialog), so duplicating it risks firing that alongside our
/// own handler.
///
/// Positioning: the invite button lives inside a vertical FlowContainer ("Container") that also holds each
/// connected player's card, and the game moves the invite button's wrapper to stay the last child every time
/// someone connects (see NRemoteLobbyPlayerContainer.OnPlayerConnected) - that's what makes it visually slide down
/// as the room fills. We add our own row as a child of that same FlowContainer and re-assert our position as last
/// on every roster change too, so Godot's own layout keeps us directly under the invite button automatically
/// instead of us tracking its GlobalPosition by hand (which goes stale the instant the roster changes).
/// </summary>
public static class RecruitToggleInjector
{
    public static void Inject(Node screenNode, StartRunLobby lobby)
    {
        try
        {
            // The row actually lives several levels deep (inside the FlowContainer, not as a direct child of
            // screenNode), so this must search recursively - a plain GetNodeOrNull("Sts2MatchmakerRow") only checks
            // direct children and always misses it, which is why re-entering this screen kept stacking up more
            // copies instead of detecting the one already there. owned:false because our dynamically-created nodes
            // never got an Owner assigned (that's only set for nodes saved as part of a scene).
            //
            // GetSubmenuType<NCharacterSelectScreen>() reuses the SAME pooled screen instance across separate
            // hosting attempts (e.g. re-matching after a failed search), so a stale row from a PREVIOUS lobby can
            // still be sitting here - if we just skipped reinjection, its toggle button would stay bound to that
            // old, already-abandoned StartRunLobby forever, and every click would silently fail with "Could not
            // read raw lobby id" since that lobby's NetService is no longer live. Remove it and rebuild fresh,
            // bound to the CURRENT lobby, every time.
            //
            // Free() (not QueueFree()) is deliberate: QueueFree only marks the node for removal at end-of-frame, so
            // it's still a live sibling named "Sts2MatchmakerRow" at the exact moment AddChild below runs. Godot
            // auto-renames a newly added child to avoid a name collision with an existing sibling (e.g. to
            // "Sts2MatchmakerRow2") - so the freshly built row would silently get renamed, and every FUTURE call to
            // this exact FindChild would stop matching it. That row becomes permanently invisible to this cleanup
            // and just sits there forever while each re-entry adds yet another (also-renamed) row on top of it -
            // this is what "buttons pile up after going back to the main menu and re-hosting" actually was.
            Node? existingRow = screenNode.FindChild("Sts2MatchmakerRow", recursive: true, owned: false);
            existingRow?.Free();

            NRemoteLobbyPlayerContainer? remoteContainer =
                screenNode.GetNodeOrNull<NRemoteLobbyPlayerContainer>("RemotePlayerContainer")
                ?? screenNode.GetNodeOrNull<NRemoteLobbyPlayerContainer>("%RemotePlayerContainer");
            Control? flowContainer = remoteContainer?.GetNodeOrNull<Control>("Container");

            var wrapper = new Control { Name = "Sts2MatchmakerRow", CustomMinimumSize = new Vector2(200f, 50f) };
            var buttonRow = new HBoxContainer();
            buttonRow.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            wrapper.AddChild(buttonRow);

            RichTextLabel toggleLabel;
            // 200x50 matches the vanilla Invite button's own custom_minimum_size exactly (remote_player_container.tscn)
            // - this row sits directly under Invite, so mismatched width looked off. value:1f matches
            // NInvitePlayersButton.OnUnfocus's own steady-state reset (v=1) rather than BuildPillButton's usual
            // default of 0.9 - the scene's own baked-in default is actually v=0.8, but OnFocus/OnUnfocus normalize
            // it to 1/1.1 the moment the button's focus state is touched at all, so 0.8 is never what a player
            // actually sees in practice.
            Button toggleButton = Sts2ModalPanel.BuildPillButton(new Vector2(200f, 50f), out toggleLabel, value: 1f);
            buttonRow.AddChild(toggleButton);
            Sts2ModalPanel.WireHoverBrightness(toggleButton);

            // Always "매칭", never "매칭 취소" - MatchConditionsWindow itself can no longer be closed while a
            // search is actually active (see its own OnCloseRequested override), so there's no reachable state
            // where this row is visible AND a search is live at the same time to reflect in the first place. A
            // label that flips to "매칭 취소" but doesn't actually cancel anything when clicked (it just reopens
            // the same window, same as every other press) was just confusing without adding real function.
            toggleLabel.Text = Loc.Get("매칭");
            toggleButton.Pressed += () => MatchConditionsWindow.ShowFor(lobby);

            // Keep the ascension tag honest as people come and go. StartRunLobby.MaxAscension is the minimum unlocked
            // multiplayer ascension across current members, so someone less progressed arriving through a
            // friends-list invite - never touching our search filters - quietly lowers what this room can play at,
            // and their leaving raises it back.
            //
            // Deferred rather than read inline because the two edges fire in opposite orders: joining recomputes
            // MaxAscension before raising PlayerConnected, but OnDisconnectedFromClientAsHost raises
            // PlayerDisconnected FIRST and only then recomputes - so an inline read would tag the stale
            // pre-departure value. Waiting a frame makes both edges see the settled number.
            //
            // Host-only on purpose: clients never recompute MaxAscension on departure at all (their
            // HandlePlayerLeftMessage skips it), so a participant's copy stays stuck at the low-water mark.
            void RetagAscensionNow()
            {
                if (!GodotObject.IsInstanceValid(wrapper) || lobby.NetService.Type != NetGameType.Host)
                {
                    return;
                }
                string? rawId = lobby.NetService.GetRawLobbyIdentifier();
                if (rawId == null || !ulong.TryParse(rawId, out ulong idValue))
                {
                    return;
                }
                var id = new CSteamID(idValue);
                if (SteamMatchmaking.GetLobbyData(id, MatchTags.ActiveKey) != MatchTags.ActiveValue)
                {
                    return; // not open to matching right now - ApplyTags writes a fresh value when it is
                }
                int roomAscension = MatchLobbyTagging.ResolveRoomAscension(lobby.MaxAscension, MatchSettingsStore.Load().Ascension);
                MatchLobbyTagging.UpdateAscensionTag(id, roomAscension);
                Log.Info($"[sts2_matchmaker] Roster changed, re-tagged lobby {idValue} ascension={roomAscension} (room ceiling {lobby.MaxAscension})");
            }
            void OnRosterChangedRetagAscension(StartRunLobbyPlayer _) => Callable.From(RetagAscensionNow).CallDeferred();

            lobby.PlayerConnected += OnRosterChangedRetagAscension;
            lobby.PlayerDisconnected += OnRosterChangedRetagAscension;
            wrapper.TreeExiting += () =>
            {
                lobby.PlayerConnected -= OnRosterChangedRetagAscension;
                lobby.PlayerDisconnected -= OnRosterChangedRetagAscension;
            };

            // Auto-stop matching once the room reaches the "인원 수" configured in matching options - primarily NOT
            // StartRunLobby.MaxPlayers (the room's own real, fixed-at-creation capacity - has no public setter, see
            // reference/Multiplayer/Game/Lobby/StartRunLobby.cs), since "인원 수" is meant to stay editable from the
            // in-lobby matching-options window too (a room already open with, say, 4 real seats might only want to
            // keep SEARCHING until 2 people show up). ResolveRoomMaxPlayers only falls back to lobby.MaxPlayers
            // when the saved setting is "무관" - see its own doc. Reading MatchSettingsStore fresh here (not a
            // captured value) so a change made the next time the matching-options window is opened takes effect on
            // THIS lobby's very next roster-changed check too. Someone joining through search after the room's
            // real capacity is hit would just fail anyway (Steam's own lobby member limit already blocks it), but
            // leaving "매칭에 공개" lit is misleading in the meantime - other searchers would still see and try to
            // join a room that's no longer actually looking. Host-only, same reasoning as RetagAscensionNow (only
            // the host can legitimately manage the search tag).
            void CheckPlayerLimitNow()
            {
                if (!GodotObject.IsInstanceValid(wrapper) || lobby.NetService.Type != NetGameType.Host)
                {
                    return;
                }
                string? rawId = lobby.NetService.GetRawLobbyIdentifier();
                if (rawId == null || !ulong.TryParse(rawId, out ulong idValue))
                {
                    return;
                }
                var id = new CSteamID(idValue);
                // ResolveRoomMaxPlayers, not the saved setting as-is: it can be MatchTags.MaxPlayersAny ("무관",
                // in which case the effective target just falls back to the room's own real capacity - see that
                // method's own doc for why this mirrors ResolveRoomAscension's handling of AscensionAny). The
                // room's real capacity comes from Steam's own lobby member limit, not StartRunLobby (which no
                // longer exposes MaxPlayers at all - it's a private field there now).
                int targetMaxPlayers = MatchLobbyTagging.ResolveRoomMaxPlayers(SteamMatchmaking.GetLobbyMemberLimit(id), MatchSettingsStore.Load().MaxPlayers);
                if (!MatchConditionsWindow.ComputeIsOpenToMatching(lobby) || lobby.Players.Count < targetMaxPlayers)
                {
                    return;
                }
                MatchLobbyTagging.RemoveFromSearch(id);
                Log.Info($"[sts2_matchmaker] Lobby {idValue} reached its matching target ({lobby.Players.Count}/{targetMaxPlayers}) - auto-closed from matching search");
                MatchSfx.PlayMatchFoundSfx(wrapper);

                // If the matching-conditions screen happens to be open right now, it was blocking its own close
                // while the search was active (see MatchConditionsWindow.OnCloseRequested) - now that there's
                // nothing left to search for, force it closed directly (bypassing that guard, not the close
                // button) back to the plain lobby screen instead of leaving it showing a now-meaningless "매칭
                // 취소" control.
                MatchConditionsWindow.CloseIfOpen();
            }
            void OnRosterChangedCheckPlayerLimit(StartRunLobbyPlayer _) => Callable.From(CheckPlayerLimitNow).CallDeferred();

            lobby.PlayerConnected += OnRosterChangedCheckPlayerLimit;
            wrapper.TreeExiting += () =>
            {
                lobby.PlayerConnected -= OnRosterChangedCheckPlayerLimit;
            };

            if (flowContainer != null)
            {
                flowContainer.AddChild(wrapper);

                void KeepLastNow()
                {
                    if (GodotObject.IsInstanceValid(wrapper) && GodotObject.IsInstanceValid(flowContainer))
                    {
                        flowContainer.MoveChild(wrapper, flowContainer.GetChildCount() - 1);
                    }
                }
                void KeepLastDeferred() => Callable.From(KeepLastNow).CallDeferred();
                void OnPlayerRosterChanged(StartRunLobbyPlayer _) => KeepLastDeferred();
                void OnSiblingEnteredTree(Node node)
                {
                    // SoloLabel/InviteButtonContainer/player cards can finish populating a frame after we run (our
                    // Harmony postfix fires before NRemoteLobbyPlayerContainer.Initialize's own deferred setup),
                    // which is what pushed us to the top the first time - re-assert last position whenever
                    // anything else shows up in the container, not just on explicit roster change events.
                    if (node != wrapper)
                    {
                        KeepLastDeferred();
                    }
                }

                flowContainer.ChildEnteredTree += OnSiblingEnteredTree;
                lobby.PlayerConnected += OnPlayerRosterChanged;
                lobby.PlayerDisconnected += OnPlayerRosterChanged;
                KeepLastDeferred();

                wrapper.TreeExiting += () =>
                {
                    flowContainer.ChildEnteredTree -= OnSiblingEnteredTree;
                    lobby.PlayerConnected -= OnPlayerRosterChanged;
                    lobby.PlayerDisconnected -= OnPlayerRosterChanged;
                };
            }
            else
            {
                // Fallback: unexpected scene layout, couldn't find the FlowContainer - park it in a fixed corner
                // instead of leaving it unpositioned at (0,0) on top of other UI.
                screenNode.AddChild(wrapper);
                wrapper.SetAnchorsPreset(Control.LayoutPreset.TopRight);
                wrapper.OffsetLeft = -262f;
                wrapper.OffsetTop = 12f;
                wrapper.OffsetRight = -12f;
                wrapper.OffsetBottom = 62f;
            }

            // Consumes MatchHostService.OpenMatchingWindowOnNextInject - set right before this same host call
            // reached InitializeMultiplayerAsHost (the Harmony postfix that led here fires from INSIDE that call,
            // before stack.Push(screen) even runs), so the auto-matched host lands directly on the matching-options
            // screen showing their search is live, instead of a bare lobby they'd have to manually reopen "매칭"
            // on. Reset immediately regardless of whether this branch actually runs, so a stale true left over from
            // some other path never leaks into a later, unrelated vanilla host. Deferred because the underlying
            // screen hasn't been pushed onto the stack yet at this point in the call chain - showing a modal over
            // a screen that isn't on-screen yet would be premature.
            if (MatchHostService.OpenMatchingWindowOnNextInject)
            {
                MatchHostService.OpenMatchingWindowOnNextInject = false;
                Callable.From(() => MatchConditionsWindow.ShowFor(lobby)).CallDeferred();
            }

            Log.Info($"[sts2_matchmaker] Injected in-lobby matching toggle into {screenNode.GetType().Name}");
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to inject recruit-to-matching row into {screenNode.GetType().Name}: {ex}");
        }
    }
}
