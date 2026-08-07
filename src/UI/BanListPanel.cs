using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using Sts2Matchmaker.Matchmaking;
using Steamworks;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Tab content for viewing/removing local ban list entries. No manual "add by SteamID64" anymore - the only way
/// onto this list is banning someone you've actually encountered (in-lobby kick+ban, or from run history), which
/// keeps every entry backed by a real nickname/encounter rather than a raw ID typed in blind. Previously its own
/// popup window (opening it meant closing the whole matching screen first, since NModalContainer only holds one
/// modal at a time - see MatchmakingWindow's old banListButton handler) - now just another tab, so switching to it
/// doesn't interrupt an in-progress search/host flow or lose the matching-conditions form's state.
/// </summary>
public class BanListPanel : VBoxContainer
{
    private readonly StartRunLobby? _lobby;
    private VBoxContainer _listContainer = null!;

    /// <summary>lobby: null when opened from MatchmakingWindow (before hosting/joining anything - there's no
    /// lobby yet to prune a ban tag from). Non-null when opened from MatchConditionsWindow (already in a lobby) -
    /// lets "밴 해제" here also prune that lobby's shared ban tag, not just the local list (see
    /// BanTagSync.PruneIdFromLobby's own doc for why this matters).</summary>
    public BanListPanel(StartRunLobby? lobby = null)
    {
        _lobby = lobby;
    }

    public void Build()
    {
        AddChild(Sts2ModalPanel.StyleBodyLabel(new Label { Text = "밴 목록:" }));

        _listContainer = new VBoxContainer();
        AddChild(_listContainer);

        RefreshList();
    }

    private void RefreshList()
    {
        foreach (Node child in _listContainer.GetChildren())
        {
            child.QueueFree();
        }

        Dictionary<ulong, BanListStore.BanEntry> bans = BanListStore.Load();
        if (bans.Count == 0)
        {
            _listContainer.AddChild(Sts2ModalPanel.StyleBodyLabel(new Label { Text = "밴 목록이 비어있습니다." }));
            return;
        }
        foreach ((ulong steamId, BanListStore.BanEntry entry) in bans.OrderBy(kv => kv.Key))
        {
            BanListStore.BanEntry entry1 = entry;
            if (string.IsNullOrEmpty(entry1.Nickname))
            {
                // Steam has often caught up on this person's persona data by the time this tab gets opened
                // (even if it hadn't at ban-time) - retry and persist the win so this only has to happen once.
                string resolved = BanListStore.ResolveNickname(steamId);
                if (!string.IsNullOrEmpty(resolved))
                {
                    BanListStore.Add(steamId, resolved, entry1.Reason);
                    entry1 = new BanListStore.BanEntry(resolved, entry1.Reason);
                }
            }

            var row = new HBoxContainer();
            string namePart = string.IsNullOrEmpty(entry1.Nickname) ? steamId.ToString() : $"{entry1.Nickname} ({steamId})";
            string label = string.IsNullOrEmpty(entry1.Reason) ? namePart : $"{namePart} - {entry1.Reason}";
            row.AddChild(Sts2ModalPanel.StyleBodyLabel(new Label { Text = label, CustomMinimumSize = new Vector2(280, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart }));

            // Same look as the other two "밴 해제" buttons (RemoteLobbyPlayerKickPatch's in-lobby toggle,
            // RunHistoryPlayerBanPatch's) - explicit red BuildTextActionButton, not a generic StyleAsSettingsButton.
            Button removeButton = Sts2ModalPanel.BuildTextActionButton("밴 해제", 50f, explicitColor: new Color("991816"));
            removeButton.Pressed += () =>
            {
                BanListStore.Remove(steamId);
                PruneFromCurrentLobbyIfAny(steamId);
                RefreshList();
            };
            row.AddChild(removeButton);

            _listContainer.AddChild(row);
        }
    }

    private void PruneFromCurrentLobbyIfAny(ulong steamId)
    {
        string? rawLobbyId = _lobby?.NetService.GetRawLobbyIdentifier();
        if (rawLobbyId != null && ulong.TryParse(rawLobbyId, out ulong lobbyIdValue))
        {
            BanTagSync.PruneIdFromLobby(new CSteamID(lobbyIdValue), steamId);
        }
    }
}
