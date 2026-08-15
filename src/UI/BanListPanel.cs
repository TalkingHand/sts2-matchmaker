using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using Sts2Matchmaker.Localization;
using Sts2Matchmaker.Matchmaking;
using Steamworks;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Tab content for viewing/removing local ban list entries, plus two ways to add entries without having actually
/// encountered someone: typing a SteamID64 directly, or importing a text file of them. (Manual add-by-ID was
/// removed at one point specifically to keep every entry backed by a real nickname/encounter - it's back because
/// some players maintain their own external blocklists, e.g. shared with friends or carried over from another
/// tool, and having no way to bulk-load one made this mod strictly worse for that use case than just not banning
/// via it at all.) Previously its own popup window (opening it meant closing the whole matching screen first, since
/// NModalContainer only holds one modal at a time - see MatchmakingWindow's old banListButton handler) - now just
/// another tab, so switching to it doesn't interrupt an in-progress search/host flow or lose the matching-conditions
/// form's state.
/// </summary>
public class BanListPanel : VBoxContainer
{
    private readonly StartRunLobby? _lobby;
    private VBoxContainer _listContainer = null!;
    private LineEdit _steamIdInput = null!;
    private LineEdit _nicknameInput = null!;
    private LineEdit _reasonInput = null!;
    private Label _addStatusLabel = null!;
    private Label _importStatusLabel = null!;

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
        BuildManualAddSection();
        AddChild(Sts2ModalPanel.BuildSettingsDivider());

        AddChild(Sts2ModalPanel.StyleBodyLabel(new Label { Text = Loc.Get("밴 목록:") }));

        _listContainer = new VBoxContainer();
        AddChild(_listContainer);

        RefreshList();

        AddChild(Sts2ModalPanel.BuildSettingsDivider());
        BuildImportSection();
    }

    /// <summary>SteamID64 + optional nickname + optional reason, added directly without an in-game encounter -
    /// see the class doc for why this exists again. Fields are ordered SteamID64/nickname/reason throughout this
    /// whole panel (this row, the list rows below, and the import/export file format) - it used to be
    /// nickname-first in the list display specifically, which read inconsistently next to this row and the file
    /// format both already being ID-first. Leaving the nickname field blank falls back to ResolveNickname (the
    /// same best-effort Steam lookup RefreshList retries on its own next time this tab opens), so a typed-in ID
    /// Steam can't currently resolve isn't treated any differently from one added via kick+ban.</summary>
    private void BuildManualAddSection()
    {
        AddChild(Sts2ModalPanel.StyleBodyLabel(new Label { Text = Loc.Get("SteamID64로 직접 추가") }));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        _steamIdInput = new LineEdit { PlaceholderText = Loc.Get("SteamID64"), CustomMinimumSize = new Vector2(200f, 56f) };
        Sts2ModalPanel.StyleInput(_steamIdInput);
        row.AddChild(_steamIdInput);

        _nicknameInput = new LineEdit { PlaceholderText = Loc.Get("닉네임 (선택)"), CustomMinimumSize = new Vector2(140f, 56f) };
        Sts2ModalPanel.StyleInput(_nicknameInput);
        row.AddChild(_nicknameInput);

        _reasonInput = new LineEdit { PlaceholderText = Loc.Get("사유 (선택)"), CustomMinimumSize = new Vector2(140f, 56f) };
        Sts2ModalPanel.StyleInput(_reasonInput);
        row.AddChild(_reasonInput);

        // Explicit green, not the default teal - matches AddPopupYesButton's own fallback green (Sts2ModalPanel's
        // "3C7A2E"), the only other green already established in this mod, so a positive/constructive action
        // (adding someone) reads consistently with the game's own green "confirm" button elsewhere.
        Button addButton = Sts2ModalPanel.BuildTextActionButton(Loc.Get("추가"), 50f, explicitColor: new Color("3C7A2E"));
        addButton.Pressed += OnAddBySteamIdPressed;
        row.AddChild(addButton);

        AddChild(row);

        _addStatusLabel = Sts2ModalPanel.StyleBodyLabel(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart });
        AddChild(_addStatusLabel);
    }

    private void OnAddBySteamIdPressed()
    {
        if (!BanListStore.TryParseSteamId64(_steamIdInput.Text, out ulong steamId))
        {
            _addStatusLabel.Text = Loc.Get("올바른 SteamID64를 입력하세요.");
            return;
        }

        string nickname = _nicknameInput.Text.Trim();
        if (string.IsNullOrEmpty(nickname))
        {
            nickname = BanListStore.ResolveNickname(steamId);
        }
        string reason = _reasonInput.Text.Trim();
        BanListStore.Add(steamId, nickname, reason);
        _steamIdInput.Text = string.Empty;
        _nicknameInput.Text = string.Empty;
        _reasonInput.Text = string.Empty;
        _addStatusLabel.Text = Loc.Get("추가되었습니다.");
        RefreshList();
    }

    /// <summary>Text-file bulk import/export via Windows' own native Open/Save dialogs (FileDialog.UseNativeDialog,
    /// confirmed available - Godot 4.5's DisplayServer exposes FileDialogShow/HasFeature(NativeDialogFile) and
    /// FileDialog itself has a UseNativeDialog property, verified against the actual installed GodotSharp.dll
    /// rather than assumed from general Godot docs) instead of the earlier fixed-folder-plus-fixed-filename
    /// version of this feature - the player picks whatever file/location they want, same as any other Windows
    /// app's Open/Save. The format itself isn't explained here at all: ExportToFile writes a commented header with
    /// a worked example into the file it produces, so the one file a player needs to open to learn the format IS
    /// the file they'd import - no separate in-game description to keep in sync with it.</summary>
    private void BuildImportSection()
    {
        AddChild(Sts2ModalPanel.StyleBodyLabel(new Label { Text = Loc.Get("텍스트 파일로 가져오기/내보내기") }));

        Label helpLabel = Sts2ModalPanel.StyleBodyLabel(new Label
        {
            Text = Loc.Get("내보내기한 파일을 열어보면 가져오기 양식과 예시를 확인할 수 있습니다."),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        AddChild(helpLabel);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        // Same explicit green as the "추가" button above (see its own comment) - export/import are also additive/
        // constructive actions on the ban list, not destructive ones like "밴 해제"'s red.
        Button exportButton = Sts2ModalPanel.BuildTextActionButton(Loc.Get("내보내기"), 50f, explicitColor: new Color("3C7A2E"));
        exportButton.Pressed += OnExportPressed;
        row.AddChild(exportButton);

        Button importButton = Sts2ModalPanel.BuildTextActionButton(Loc.Get("가져오기"), 50f, explicitColor: new Color("3C7A2E"));
        importButton.Pressed += OnImportPressed;
        row.AddChild(importButton);

        AddChild(row);

        _importStatusLabel = Sts2ModalPanel.StyleBodyLabel(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart });
        AddChild(_importStatusLabel);
    }

    private void OnExportPressed()
    {
        FileDialog dialog = BuildFileDialog(FileDialog.FileModeEnum.SaveFile, "sts2_matchmaker_banlist.txt");
        dialog.FileSelected += path =>
        {
            try
            {
                BanListStore.ExportToFile(path);
                _importStatusLabel.Text = Loc.Get("내보내기가 완료되었습니다.");
            }
            catch (Exception ex)
            {
                Log.Error($"[sts2_matchmaker] Failed to export ban list to {path}: {ex}");
                _importStatusLabel.Text = Loc.Get("내보내기에 실패했습니다.");
            }
            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(720, 480));
    }

    private void OnImportPressed()
    {
        FileDialog dialog = BuildFileDialog(FileDialog.FileModeEnum.OpenFile, string.Empty);
        dialog.FileSelected += path =>
        {
            try
            {
                BanListStore.ImportResult result = BanListStore.ImportFromFile(path);
                _importStatusLabel.Text = BuildImportResultMessage(result);
                RefreshList();
            }
            catch (Exception ex)
            {
                Log.Error($"[sts2_matchmaker] Failed to import ban list from {path}: {ex}");
                _importStatusLabel.Text = Loc.Get("가져오기에 실패했습니다.");
            }
            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(720, 480));
    }

    /// <summary>Lists each skipped line's raw id/nickname/reason (whatever text sat in each comma-separated
    /// position, even though the id half failed to parse) below the added/skipped counts, so a player whose file
    /// has a typo can actually find and fix it instead of just knowing "something" was skipped.</summary>
    private static string BuildImportResultMessage(BanListStore.ImportResult result)
    {
        string summary = string.Format(Loc.Get("{0}개 추가됨, {1}개는 SteamID 형식이 올바르지 않아 건너뜀."), result.Added, result.Skipped.Count);
        if (result.Skipped.Count == 0)
        {
            return summary;
        }

        var lines = new List<string> { summary };
        foreach (BanListStore.SkippedLine skip in result.Skipped)
        {
            string idPart = string.IsNullOrEmpty(skip.RawId) ? Loc.Get("(빈 값)") : skip.RawId;
            string namePart = string.IsNullOrEmpty(skip.Nickname) ? idPart : $"{idPart} ({skip.Nickname})";
            lines.Add(string.IsNullOrEmpty(skip.Reason) ? $"- {namePart}" : $"- {namePart} - {skip.Reason}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Defaults CurrentDir to the OS's own Downloads folder - leaving it unset made the native dialog
    /// open inside the game's own install folder instead (FileDialog's CurrentDir, even with UseNativeDialog=true,
    /// still gets passed through as DisplayServer.FileDialogShow's currentDirectory and overrides whatever the OS
    /// dialog would otherwise remember on its own), which isn't where a player keeps files they'd want to
    /// import/export from. Downloads is the same starting point Windows' own Open/Save dialogs default to.</summary>
    private static FileDialog BuildFileDialog(FileDialog.FileModeEnum mode, string defaultFileName)
    {
        var dialog = new FileDialog
        {
            FileMode = mode,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Filters = new[] { "*.txt ; Text Files" },
            CurrentDir = OS.GetSystemDir(OS.SystemDir.Downloads, false),
        };
        if (!string.IsNullOrEmpty(defaultFileName))
        {
            dialog.CurrentFile = defaultFileName;
        }
        return dialog;
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
            _listContainer.AddChild(Sts2ModalPanel.StyleBodyLabel(new Label { Text = Loc.Get("밴 목록이 비어있습니다.") }));
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
            string namePart = string.IsNullOrEmpty(entry1.Nickname) ? steamId.ToString() : $"{steamId} ({entry1.Nickname})";
            string label = string.IsNullOrEmpty(entry1.Reason) ? namePart : $"{namePart} - {entry1.Reason}";
            row.AddChild(Sts2ModalPanel.StyleBodyLabel(new Label { Text = label, CustomMinimumSize = new Vector2(280, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart }));

            // Same look as the other two "밴 해제" buttons (RemoteLobbyPlayerKickPatch's in-lobby toggle,
            // RunHistoryPlayerBanPatch's) - explicit red BuildTextActionButton, not a generic StyleAsSettingsButton.
            Button removeButton = Sts2ModalPanel.BuildTextActionButton(Loc.Get("밴 해제"), 50f, explicitColor: new Color("991816"));
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
