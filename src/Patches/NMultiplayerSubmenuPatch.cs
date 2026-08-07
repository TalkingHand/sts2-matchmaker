using System;
using System.IO;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.addons.mega_text;
using Sts2Matchmaker.Localization;
using Sts2Matchmaker.UI;

namespace Sts2Matchmaker.Patches;

/// <summary>
/// Injects a "매칭" button into the vanilla multiplayer submenu (alongside Host/Load/Join/Abandon), built from the
/// same submenu_button.tscn the real buttons use (icon + title + description + hover glow), instead of a bare
/// Godot Button, so it actually looks native. No [ModInitializer] is declared for this assembly, so the mod loader
/// auto-creates a Harmony instance and calls PatchAll on us - this patch is wired up automatically.
///
/// The vanilla "세이브로 호스트"/LoadButton action (re-hosting a broken multiplayer run from save) is left
/// completely untouched here - it gets its search-tag treatment from the load screens it pushes instead (see
/// VanillaRehostTagPatch), not from anything added to this menu.
/// </summary>
[HarmonyPatch(typeof(NMultiplayerSubmenu), "_Ready")]
public static class NMultiplayerSubmenuPatch
{
    private const string ButtonScenePath = "res://scenes/ui/submenu_button.tscn";
    private static Texture2D? _iconTexture;

    [HarmonyPostfix]
    public static void Postfix(NMultiplayerSubmenu __instance)
    {
        try
        {
            Node? container = __instance.GetNodeOrNull("ButtonContainer");
            if (container == null || container.GetNodeOrNull("Sts2MatchmakerButton") != null)
            {
                return;
            }

            NButton? templateButton = container.GetNodeOrNull<NButton>("JoinButton");
            var button = PreloadManager.Cache.GetScene(ButtonScenePath).Instantiate<NButton>(PackedScene.GenEditState.Disabled);
            button.Name = "Sts2MatchmakerButton";
            if (templateButton != null)
            {
                button.CustomMinimumSize = templateButton.CustomMinimumSize;
                button.SizeFlagsHorizontal = templateButton.SizeFlagsHorizontal;
                button.SizeFlagsVertical = templateButton.SizeFlagsVertical;
            }
            container.AddChild(button);

            // The button's own children (Icon/%Title/%Description) aren't ready until it's had a frame in the
            // tree, so finish setup deferred - same reason _stack was still null when we read it too early before.
            Callable.From(() => FinalizeButton(button, __instance, container)).CallDeferred();
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to inject matchmaking button: {ex}");
        }
    }

    private static void FinalizeButton(NButton button, NMultiplayerSubmenu submenu, Node container)
    {
        if (!GodotObject.IsInstanceValid(button))
        {
            return;
        }

        try
        {
            TextureRect? icon = button.GetNodeOrNull<TextureRect>("Icon");
            Texture2D? iconTexture = GetIconTexture();
            if (icon != null && iconTexture != null)
            {
                icon.Texture = iconTexture;
                ApplyHoverOverlay(button, icon);
            }

            button.GetNodeOrNull<MegaLabel>("%Title")?.SetTextAutoSize(Loc.Get("매칭"));
            MegaRichTextLabel? description = button.GetNodeOrNull<MegaRichTextLabel>("%Description");
            if (description != null)
            {
                description.Text = Loc.Get("조건에 맞는 상대와 매칭합니다.");
            }

            button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ =>
            {
                NSubmenuStack? stack = Traverse.Create(submenu).Field("_stack").GetValue<NSubmenuStack>();
                if (stack == null)
                {
                    Log.Error("[sts2_matchmaker] _stack is still null when matching button was pressed");
                    return;
                }
                MatchmakingWindow.ShowFor(submenu, stack);
            }));

            RecenterButtons(container);
            Log.Info("[sts2_matchmaker] Injected matchmaking button into NMultiplayerSubmenu");
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to finalize matchmaking button: {ex}");
        }
    }

    private static void ApplyHoverOverlay(NButton button, TextureRect icon)
    {
        var overlay = (TextureRect)icon.Duplicate();
        overlay.Name = "Sts2MatchmakerHoverOverlay";
        overlay.Material = null;
        overlay.MouseFilter = Control.MouseFilterEnum.Ignore;
        overlay.Modulate = new Color(1f, 1f, 1f, 0f);
        button.AddChild(overlay);
        button.MoveChild(overlay, icon.GetIndex() + 1);

        button.Connect(NClickableControl.SignalName.Focused, Callable.From<NClickableControl>(_ =>
        {
            if (GodotObject.IsInstanceValid(overlay))
            {
                overlay.Modulate = new Color(1f, 1f, 1f, 0.18f);
            }
        }));
        button.Connect(NClickableControl.SignalName.Unfocused, Callable.From<NClickableControl>(_ =>
        {
            if (GodotObject.IsInstanceValid(overlay))
            {
                overlay.Modulate = new Color(1f, 1f, 1f, 0f);
            }
        }));
    }

    /// <summary>Keeps the button row centered by recomputing the container's offsets, same idea as other mods that
    /// add buttons to this row - it isn't a Godot-native auto-centering HBoxContainer.</summary>
    private static void RecenterButtons(Node containerNode)
    {
        if (containerNode is not Control container || !GodotObject.IsInstanceValid(container))
        {
            return;
        }

        int visibleCount = 0;
        float totalWidth = 0f;
        foreach (Node child in container.GetChildren())
        {
            if (child is Control control && control.Visible)
            {
                visibleCount++;
                totalWidth += Mathf.Max(330f, control.CustomMinimumSize.X);
            }
        }

        if (visibleCount == 0)
        {
            return;
        }

        float separation = container.GetThemeConstant("separation");
        float halfWidth = (totalWidth + Mathf.Max(0, visibleCount - 1) * separation) * 0.5f;
        container.OffsetLeft = -halfWidth;
        container.OffsetRight = halfWidth;
    }

    private static Texture2D? GetIconTexture()
    {
        if (_iconTexture != null)
        {
            return _iconTexture;
        }

        string? modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(modDirectory))
        {
            return null;
        }

        string iconPath = Path.Combine(modDirectory, "assets", "submenu_matching.png");
        if (!File.Exists(iconPath))
        {
            Log.Warn($"[sts2_matchmaker] Button icon missing: {iconPath}");
            return null;
        }

        Image image = Image.LoadFromFile(iconPath);
        if (!image.HasMipmaps())
        {
            image.GenerateMipmaps();
        }
        _iconTexture = ImageTexture.CreateFromImage(image);
        return _iconTexture;
    }
}
