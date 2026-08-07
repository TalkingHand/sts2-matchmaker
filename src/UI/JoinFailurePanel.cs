using Godot;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Fallback failure popup for MatchmakingWindow.JoinAsync. NModalContainer only ever holds one modal at a time, so
/// JoinAsync closes our own matching window before attempting the join - that way, if the join fails and the game
/// tries to show its own native error dialog (NErrorPopup), the slot is free and it actually displays instead of
/// silently no-opping ("There's another modal already open."). If nothing else ends up telling the player what
/// happened (e.g. the failure didn't come from a path that raises a vanilla popup), this is shown instead, so a
/// join failure is never silently invisible.
/// </summary>
public class JoinFailurePanel : Sts2ModalPanel
{
    public static void Show(string message, string title = "접속 실패")
    {
        var panel = new JoinFailurePanel();
        panel.ShowAsModal(title, new Vector2(480f, 280f));
        panel.ContentContainer.AddChild(new Label
        {
            Text = message,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
    }
}
