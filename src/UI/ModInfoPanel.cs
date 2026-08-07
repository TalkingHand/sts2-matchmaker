using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Tab content listing the client's gameplay-relevant mods - the exact same list ComputeGameplayModHash() hashes
/// for the "모드 목록이 일치하는 로비만" filter, so this shows precisely what's being compared (not the full mod
/// list, and not cosmetic/QoL mods) instead of leaving that filter's behavior a mystery. Was previously a
/// show/hide toggle button buried at the bottom of the matching-conditions form ("내 모드 목록 보기"); pulled out
/// into its own always-visible tab since a whole tab is a clearer, more discoverable place for it than a toggle.
/// </summary>
public class ModInfoPanel : VBoxContainer
{
    public void Build()
    {
        var hintLabel = Sts2ModalPanel.StyleBodyLabel(new Label
        {
            Text = "매칭 시 상대방과 비교되는, 게임플레이에 영향을 주는 모드만 표시됩니다.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        AddChild(hintLabel);
        AddChild(Sts2ModalPanel.BuildSettingsDivider());

        var listLabel = Sts2ModalPanel.StyleBodyLabel(new Label
        {
            Text = BuildGameplayModListText(),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        AddChild(listLabel);
    }

    private static string BuildGameplayModListText()
    {
        List<string>? mods = ModManager.GetGameplayRelevantModNameList();
        if (mods == null || mods.Count == 0)
        {
            return "게임플레이에 영향을 주는 모드가 설치되어 있지 않습니다 (언모드 상태).";
        }
        return string.Join("\n", mods.OrderBy(m => m));
    }
}
