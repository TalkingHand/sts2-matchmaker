using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Shared "매칭 성사" sound effect - a plain Godot AudioStream, not an FMOD event like every other sound this mod
/// plays via Sts2ModalPanel.PlayClickSfx (lives in the game's own res://debug_audio/ folder alongside
/// heavy_attack.mp3/hiss.mp3/etc, confirmed by grepping SlayTheSpire2.pck directly, so AudioStreamPlayer is the
/// right way to play it rather than SfxCmd.Play). Originally only played on the host side when a lobby auto-closed
/// from matching search after filling up (RecruitToggleInjector) - reused here for the searcher/guest side too, so
/// actually entering a lobby through matching (own-mod search, DC-crawled guest fallback, or rehost-wait) gets the
/// same audible confirmation instead of the popup just silently closing.
/// </summary>
public static class MatchSfx
{
    private const string MatchFoundSfxPath = "res://debug_audio/hey.mp3";

    /// <summary>One-shot playback - builds a throwaway AudioStreamPlayer under liveParent (must already be in the
    /// live tree) and frees it once playback finishes, so nothing lingers after the sound is done.</summary>
    public static void PlayMatchFoundSfx(Node liveParent)
    {
        AudioStream? stream = GD.Load<AudioStream>(MatchFoundSfxPath);
        if (stream == null)
        {
            Log.Error($"[sts2_matchmaker] Could not load {MatchFoundSfxPath}");
            return;
        }
        var player = new AudioStreamPlayer { Stream = stream };
        liveParent.AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
    }
}
