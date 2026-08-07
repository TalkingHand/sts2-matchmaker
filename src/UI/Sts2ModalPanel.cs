using System;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Base for our popups, styled like the game's own NVerticalPopup (see vertical_popup.tscn: a center-anchored
/// TextureRect using images/atlases/ui_atlas.sprites/popup_vertical.tres with the same hsv tint shader) instead of
/// a raw Godot Window - the game itself never uses Window for its UI (everything is a Control shown through
/// NModalContainer, which handles the darkened backdrop), so a real Window has generic engine chrome that looks
/// out of place. Subclasses build their own controls into ContentContainer.
/// </summary>
public abstract class Sts2ModalPanel : Control, IScreenContext
{
    private const string BackgroundTexturePath = "res://images/atlases/ui_atlas.sprites/popup_vertical.tres";
    private const string HsvShaderPath = "res://shaders/hsv.gdshader";
    private const string TitleFontPath = "res://themes/kreon_bold_glyph_space_two.tres";
    private const string BackButtonScenePath = "res://scenes/ui/back_button.tscn";
    private const string ConfirmButtonScenePath = "res://scenes/ui/confirm_button.tscn";
    // The game has no separate "cancel" scene - NCharacterSelectScreen's own "UnreadyButton" (shown in place of the
    // back button while waiting on other players to ready up) is literally a second back_button.tscn instance with
    // just its Image/Icon child texture swapped to this "X" variant (see reference/screens/CharacterSelect/
    // NCharacterSelectScreen.cs and the "UnreadyButton" node override in character_select_screen.tscn) - same scene,
    // same slide-in/out-from-the-left positioning, same script (NBackButton), different icon.
    private const string CancelIconTexturePath = "res://images/atlases/compressed.sprites/back_button_x.tres";
    private const string PopupYesButtonScenePath = "res://scenes/ui/abandon_run_yes_button.tscn";
    private const string BodyFontPath = "res://themes/kreon_regular_shared.tres";
    private const string PillButtonTexturePath = "res://images/ui/reward_screen/reward_item_button.png";
    private const string PillButtonRegularFontPath = "res://themes/kreon_regular_glyph_space_one.tres";
    private const string PillButtonBoldFontPath = "res://themes/kreon_bold_glyph_space_one.tres";
    private const string SettingsActionButtonTexturePath = "res://images/ui/reward_screen/reward_skip_button.png";
    private const string SettingsActionButtonFontPath = "res://themes/kreon_bold_glyph_space_two.tres";
    private const string CheckboxTickedTexturePath = "res://images/atlases/ui_atlas.sprites/checkbox_ticked.tres";
    private const string CheckboxUntickedTexturePath = "res://images/atlases/ui_atlas.sprites/checkbox_unticked.tres";
    private const string SettingsTabScenePath = "res://scenes/screens/settings_tab.tscn";

    // Same FMOD event NButton.ClickedSfx plays from its own OnPress() (see reference/GodotExtensions/NButton.cs) -
    // every native control we reuse via PreloadManager (NBackButton, NConfirmButton, NPopupYesNoButton,
    // NSettingsTab, NDropdownItem, all NButton subclasses) already gets this for free the moment it's pressed.
    // Plain Godot Buttons we build by hand (BuildTextActionButton, StyleAsSettingsButton) don't inherit any of
    // that, so they need this wired in explicitly - see PlayClickSfx, hooked into both of those.
    private const string ButtonClickSfxEvent = "event:/sfx/ui/clicks/ui_click";

    /// <summary>Plays the same click sound every native button already gets from NButton.OnPress() - see
    /// ButtonClickSfxEvent's own doc for why our own hand-built Buttons need this wired in manually instead of
    /// getting it automatically.</summary>
    private static void PlayClickSfx() => SfxCmd.Play(ButtonClickSfxEvent);

    /// <summary>
    /// Every "font" load in this file used to be a bare GD.Load&lt;Font&gt; on one of the Kreon FontVariation paths
    /// above - which is exactly what the native settings screen's OWN code does NOT do. Its labels are MegaLabel/
    /// MegaRichTextLabel (addons/mega_text), and both call FontControlUtils.ApplyLocaleFontSubstitution in
    /// RefreshFont(), which checks FontManager.NeedsFontSubstitution(current language) and swaps in a completely
    /// different font resource for languages Kreon doesn't cover glyphs for - confirmed via KorFontPathSet: ALL
    /// THREE of Regular/Bold/Italic map to the same res://themes/fonts/kor/gyeonggi_cheonnyeon_batang_bold_shared.tres
    /// for Korean, not any Kreon variant at all. Kreon is a Latin-only display font; without this substitution,
    /// Korean text falls back to whatever generic glyph Godot's engine substitutes internally for missing glyphs -
    /// which is what every one of this session's "font looks different/wrong size" reports against a Korean
    /// screenshot actually was, not a sizing or theme-property bug. FontManager/FontType are public in the game's
    /// own assembly, so we call them directly instead of reimplementing locale detection ourselves.
    /// </summary>
    private static Font? ResolveFont(string fallbackPath, FontType fontType)
    {
        string language = LocManager.Instance?.Language ?? string.Empty;
        Font? substitute = FontManager.GetSubstituteFont(language, fontType);
        return substitute ?? GD.Load<Font>(fallbackPath);
    }

    // The settings screen's own line-item text (checkboxes/dropdowns/descriptions) uses this exact font at 17px
    // (settings_screen.tscn) - matching it here instead of leaving every Label/CheckBox/Button on Godot's fallback
    // default font, which is what made our popups look visibly "un-native".
    private static Theme? _sharedBodyTheme;

    internal static Theme BodyTheme
    {
        get
        {
            if (_sharedBodyTheme == null)
            {
                _sharedBodyTheme = new Theme();
                Font? bodyFont = ResolveFont(BodyFontPath, FontType.Regular);
                if (bodyFont != null)
                {
                    _sharedBodyTheme.DefaultFont = bodyFont;
                }
                _sharedBodyTheme.DefaultFontSize = 18;
            }
            return _sharedBodyTheme;
        }
    }

    public Control? DefaultFocusedControl => null;

    protected VBoxContainer ContentContainer { get; private set; } = null!;

    /// <summary>The painted popup box itself (the "Background" TextureRect) - exposed so callers that need a fixed
    /// element outside the scrollable ContentContainer (e.g. BanConfirmPanel's Cancel/Confirm row, which must stay
    /// reachable regardless of how far the body text scrolls) have somewhere to attach it other than ContentContainer.</summary>
    protected Control? PopupBackground { get; private set; }

    /// <summary>Builds the panel and registers it with NModalContainer (darkens the backdrop, centers us).</summary>
    protected void ShowAsModal(string title, Vector2 size)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Theme = BodyTheme;

        var background = new TextureRect
        {
            Name = "Background",
            Texture = GD.Load<Texture2D>(BackgroundTexturePath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            // KeepAspectCentered previously let the art draw narrower than the Control's own box whenever the
            // texture's native aspect ratio didn't match `size` exactly, letterboxing it - our content area
            // (below) always fills the full box, so that mismatch made long lines visually spill past the drawn
            // tablet's edges even though they were still within the Control's actual bounds. Scale guarantees the
            // art always exactly fills whatever box we size it to.
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Stop,
        };
        background.SetAnchorsPreset(LayoutPreset.Center);
        background.OffsetLeft = -size.X / 2f;
        background.OffsetTop = -size.Y / 2f;
        background.OffsetRight = size.X / 2f;
        background.OffsetBottom = size.Y / 2f;
        Shader? shader = GD.Load<Shader>(HsvShaderPath);
        if (shader != null)
        {
            var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter("h", 0.505f);
            material.SetShaderParameter("s", 1.0f);
            material.SetShaderParameter("v", 0.75f);
            background.Material = material;
        }
        // Click-away-to-unfocus, same reasoning/mechanism as ShowAsTabbedSettingsScreen's own contentRoot handler
        // (see its doc) - LineEdit doesn't release focus on its own just because something else was clicked.
        background.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                GetViewport()?.GuiReleaseFocus();
            }
        };
        AddChild(background);
        PopupBackground = background;

        var titleLabel = new Label
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        titleLabel.SetAnchorsPreset(LayoutPreset.TopWide);
        titleLabel.OffsetTop = 40f;
        titleLabel.OffsetBottom = 90f;
        titleLabel.AddThemeColorOverride("font_color", new Color(0.937255f, 0.784314f, 0.317647f, 1f));
        titleLabel.AddThemeColorOverride("font_outline_color", new Color(0.33f, 0.2475f, 0f, 1f));
        titleLabel.AddThemeConstantOverride("outline_size", 12);
        titleLabel.AddThemeFontSizeOverride("font_size", 30);
        Font? titleFont = ResolveFont(TitleFontPath, FontType.Bold);
        if (titleFont != null)
        {
            titleLabel.AddThemeFontOverride("font", titleFont);
        }
        background.AddChild(titleLabel);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        scroll.OffsetLeft = 40f;
        scroll.OffsetTop = 110f;
        scroll.OffsetRight = -40f;
        scroll.OffsetBottom = -100f;
        background.AddChild(scroll);

        ContentContainer = new VBoxContainer();
        ContentContainer.SetHSizeFlags(SizeFlags.ExpandFill);
        scroll.AddChild(ContentContainer);

        NModalContainer.Instance?.Add(this);
        AddCloseButton();
    }

    private const string SettingsTabBannerTexturePath = "res://images/packed/common_ui/settings_tab_selected.png";
    private const string SettingsTabStrokeTexturePath = "res://images/packed/common_ui/settings_tab_stroke.png";
    private const string SettingsTabAdditiveMaterialPath = "res://themes/canvas_item_material_additive_shared.tres";
    private const string DropdownArrowTexturePath = "res://images/packed/common_ui/settings_tiny_left_arrow.png";

    // Glow modulate color copied from settings_tab.tscn's "Outline" node - a cyan glow drawn (additive blend) over
    // the selected tab's own base image, not a different-colored texture. See NSettingsTab.Select()/Deselect():
    // every tab (selected or not) renders the exact same settings_tab_selected.png at the exact same hue - only
    // this glow's visibility and the label's brightness change between selected/unselected.
    private static readonly Color SettingsTabOutlineModulate = new(0.3648f, 0.9104f, 0.96f, 0.752941f);

    /// <summary>
    /// Same registration/scroll/close-button plumbing as <see cref="ShowAsModal"/>, but WITHOUT the painted
    /// popup_vertical.tres box - settings_screen.tscn (the game's actual multi-tab settings screen) has no popup
    /// art at all, just a tab-strip header and a bare row list sitting directly on whatever's behind it (in our
    /// case, NModalContainer's own darkening backstop, which exists independently of anything we draw - confirmed
    /// via NModalContainer.cs's own _backstop ColorRect, so dropping our box doesn't risk an unreadable/transparent
    /// popup).
    ///
    /// Builds a real tab strip (one <see cref="TryBuildNativeSettingsTab"/> per label) instead of a single static
    /// title banner - modeled on the game's own NSettingsTabManager (src/Core/Nodes/Screens/Settings/), which does
    /// exactly this: N NSettingsTab buttons up top, each paired with an NSettingsPanel that gets shown/hidden on
    /// selection. We reproduce that pairing with a plain array of VBoxContainer pages returned to the caller -
    /// callers build their own content into pages[i], same as they used to build directly into ContentContainer.
    /// Only one page is ever attached to the ScrollContainer at a time (added/removed on switch, not
    /// Visible-toggled) - this mirrors NSettingsTabManager.SwitchTabTo's own NScrollableContainer.SetContent(...)
    /// swap, and sidesteps any question of whether a hidden sibling would still affect the ScrollContainer's
    /// measured content height. Row/tab dimensions below are copied directly from settings_screen.tscn and its
    /// settings_tab.tscn/settings_dropdown.tscn sub-scenes (see reference/screens/ if re-verifying), not eyeballed
    /// off a screenshot like earlier versions of this method.
    ///
    /// No fixed size parameter (unlike ShowAsModal) - the real settings screen fills nearly the whole viewport, not
    /// a small centered box.
    ///
    /// contentRoot uses a FIXED, centered pixel width (1012px) rather than a percentage of the viewport - copied
    /// exactly from settings_screen.tscn's own GeneralSettings/VBoxContainer node (custom_minimum_size.x = 1012,
    /// centered via anchor_left = anchor_right = 0.5 with offset_left/right = -506/506). Earlier versions of this
    /// method used percentage-of-viewport anchors (first 88%, then 62%) tuned by eyeballing screenshots, and each
    /// one drifted from the real thing in a different way. The reason a FIXED pixel width is actually correct here
    /// (not just "another guess"): the game runs its whole UI at a fixed design resolution (1920x1080 - confirmed
    /// by NSettingsScreen.settingTipsOffset using Vector2(1012f, -60f) relative to a Vector2(960, 540) screen
    /// center) and lets Godot's canvas stretch transform scale that uniformly to whatever the real window size is.
    /// Since our popup is just another Control in that same live scene tree, it's scaled by the exact same
    /// transform - so reusing the native scene's own design-pixel numbers (not a percentage we invented) is what
    /// actually reproduces its proportions at any resolution, not an approximation of it.
    /// </summary>
    /// <summary>Full-width semi-transparent scrim over the current tab's content area (same rect as the scroll
    /// region, not the tab strip - switching tabs stays possible while this is up). Hidden by default; callers
    /// toggle Visible themselves for whatever "options are frozen right now" state they need (e.g.
    /// MatchConditionsWindow while a matching search is active) - there's no native equivalent to copy this from
    /// (NCharacterSelectScreen disables its character buttons individually instead), so this is a plain custom
    /// ColorRect + MouseFilter.Stop rather than a reproduction of a specific game asset.</summary>
    protected ColorRect? ContentBlocker { get; private set; }

    protected VBoxContainer[] ShowAsTabbedSettingsScreen(string[] tabLabels)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Theme = BodyTheme;

        // Added to the live tree BEFORE building anything else below (not at the end, like this method used to) -
        // the tab loop instantiates real native NSettingsTab scenes, whose _Ready() (populating fields like _label)
        // only fires once a node is actually live, which for a freshly-AddChild'd node means its PARENT must
        // already be live too. Moving this here means every AddChild for the rest of this method (contentRoot,
        // tabBar, each tab) is already happening on a live tree, so each one's own _Ready() fires synchronously
        // right there - no deferred-callback juggling needed, the same reasoning already established for every
        // other native-scene reuse in this file (see AddPopupYesButton's own doc, the first place this exact
        // ordering hazard showed up).
        NModalContainer.Instance?.Add(this);

        var contentRoot = new Control { Name = "ContentRoot", MouseFilter = MouseFilterEnum.Stop };
        contentRoot.AnchorLeft = 0.5f;
        contentRoot.AnchorRight = 0.5f;
        contentRoot.OffsetLeft = -506f;
        contentRoot.OffsetRight = 506f;
        contentRoot.AnchorTop = 0f;
        contentRoot.AnchorBottom = 1f;
        // Godot's LineEdit (e.g. MatchConditionsPanel's "커뮤니티명" field) does NOT release focus just because the
        // player clicks somewhere else on screen - only losing to another focusable control, Tab, or Escape does
        // that natively. A click that isn't already consumed by some more specific interactive child (a row's
        // dropdown, a button, the input field itself) bubbles up to here, so releasing whatever currently has focus
        // on any such click is the simplest way to get the "click away to unfocus" behavior players expect. Safe
        // to call even when nothing is focused - GuiReleaseFocus() is a no-op then.
        contentRoot.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                GetViewport()?.GuiReleaseFocus();
            }
        };
        AddChild(contentRoot);

        // SettingsTabManager in settings_screen.tscn sits at offset_top/bottom = 72/162 (a 90px-tall band, matching
        // settings_tab.tscn's own 90px tab height) and uses the engine's default ~4px HBoxContainer separation - we
        // don't have native's left/right controller-hint icons to help fill the row, but centering our (fewer) tabs
        // in the same band still lines the tab strip up with the row content below it.
        var tabBar = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        tabBar.AddThemeConstantOverride("separation", 4);
        tabBar.SetAnchorsPreset(LayoutPreset.TopWide);
        tabBar.OffsetTop = 72f;
        tabBar.OffsetBottom = 162f;
        contentRoot.AddChild(tabBar);

        // ScrollMode.Auto (the default) hides the bar entirely whenever content happens to fit without overflowing -
        // that made it look "missing" once. ShowAlways matches the real settings screen, where the gold diamond
        // handle is always present regardless of content length.
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowAlways,
        };
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        // 211 = Clipper's own offset_top (187) + GeneralSettings' offset_top (24) in settings_screen.tscn - the gap
        // between the bottom of the tab strip (162) and the first row.
        scroll.OffsetTop = 211f;
        scroll.OffsetBottom = -50f;
        StyleScrollbar(scroll);
        contentRoot.AddChild(scroll);

        // Hidden by default - see ContentBlocker doc. Same rect as scroll (not tabBar), added after it so it paints
        // on top: covers whatever tab is currently showing without blocking tab-switching itself.
        var contentBlocker = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.6f),
            Visible = false,
            MouseFilter = MouseFilterEnum.Stop,
        };
        contentBlocker.SetAnchorsPreset(LayoutPreset.FullRect);
        contentBlocker.OffsetTop = 211f;
        contentBlocker.OffsetBottom = -50f;
        contentRoot.AddChild(contentBlocker);
        ContentBlocker = contentBlocker;

        var pages = new VBoxContainer[tabLabels.Length];
        for (int i = 0; i < tabLabels.Length; i++)
        {
            pages[i] = new VBoxContainer();
            pages[i].SetHSizeFlags(SizeFlags.ExpandFill);
            // Matches GeneralSettings/VBoxContainer's theme_override_constants/separation in settings_screen.tscn -
            // the gap between each row (and its preceding divider).
            pages[i].AddThemeConstantOverride("separation", 8);
        }

        // One entry per tab regardless of which path built it (real NSettingsTab vs the plain-Button fallback) -
        // setSelected hides the visual difference between the two from SelectTab below, since NSettingsTab owns
        // its own Select()/Deselect() visuals internally while the fallback needs them driven by hand.
        var setTabSelected = new Action<bool>[tabLabels.Length];

        void SelectTab(int index)
        {
            for (int i = 0; i < setTabSelected.Length; i++)
            {
                setTabSelected[i](i == index);
            }
            if (scroll.GetChildCount() > 0)
            {
                scroll.RemoveChild(scroll.GetChild(0));
            }
            scroll.AddChild(pages[index]);
        }

        for (int i = 0; i < tabLabels.Length; i++)
        {
            int capturedIndex = i;
            NSettingsTab? nativeTab = TryBuildNativeSettingsTab(tabBar, tabLabels[i]);
            if (nativeTab != null)
            {
                setTabSelected[i] = selected =>
                {
                    if (selected)
                    {
                        nativeTab.Select();
                    }
                    else
                    {
                        nativeTab.Deselect();
                    }
                };
                nativeTab.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => SelectTab(capturedIndex)));
            }
            else
            {
                Control tabControl = BuildSettingsTabButtonFallback(tabLabels[i], out Button tabButton, out TextureRect outline, out Label tabLabel);
                tabBar.AddChild(tabControl);
                setTabSelected[i] = selected =>
                {
                    outline.Visible = selected;
                    tabLabel.AddThemeColorOverride("font_color", selected ? StsColors.cream : StsColors.halfTransparentCream);
                };
                tabButton.Pressed += () => SelectTab(capturedIndex);
            }
        }

        SelectTab(0);

        AddCloseButton();
        return pages;
    }

    /// <summary>
    /// Reuses the real settings_tab.tscn/NSettingsTab scene directly instead of BuildSettingsTabButtonFallback's
    /// hand-built approximation (which explicitly skips NSettingsTab's own hover scale-bump + HSV brightness pulse
    /// tween, per that method's own doc) - gets that native hover/focus interaction, and the label's real
    /// MegaLabel auto-sizing (18-32px), for free, since it's the actual scene/script the settings screen itself
    /// uses. tabBar must already be live (see ShowAsTabbedSettingsScreen's own doc on why NModalContainer.Add
    /// moved to the top of that method) - AddChild happens before SetLabel/Enable, both of which touch fields
    /// NSettingsTab's own _Ready() populates, same AddChild-before-touch ordering every other native-scene reuse
    /// in this file follows. Returns null (falling back to the approximation) if the scene fails to load.
    /// </summary>
    private static NSettingsTab? TryBuildNativeSettingsTab(Control tabBar, string text)
    {
        try
        {
            PackedScene? scene = PreloadManager.Cache.GetScene(SettingsTabScenePath);
            if (scene == null)
            {
                throw new InvalidOperationException($"GetScene returned null for {SettingsTabScenePath}");
            }
            var tab = scene.Instantiate<NSettingsTab>(PackedScene.GenEditState.Disabled);
            tabBar.AddChild(tab);
            tab.SetLabel(text);
            tab.Enable();
            return tab;
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to build native settings tab, falling back to approximation: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Fallback only (see TryBuildNativeSettingsTab, which every caller actually uses unless the real scene fails
    /// to load) - a hand-built approximation of settings_tab.tscn/NSettingsTab: the SAME settings_tab_selected.png
    /// (h=1/s=1/v=0.9, the native unshifted teal) for every tab regardless of selection state, with a separate
    /// additive-blended glow (settings_tab_stroke.png, tinted per NSettingsTab's own "Outline" modulate) shown only
    /// when selected - the vanilla tab doesn't recolor itself on selection, it overlays a glow on top. Label dims
    /// to halfTransparentCream when unselected, brightens to cream when selected (also copied from
    /// NSettingsTab.Select/Deselect). Skips NSettingsTab's hover scale-bump/tween polish (OnFocus/OnUnfocus) - not
    /// worth reproducing for a path that should rarely if ever actually run.
    /// Size (256x90) is settings_tab.tscn's own OptionsTab root Control's exact custom_minimum_size - fixed, not
    /// ExpandFill, matching how native tabs are centered as a group rather than stretched to fill their row.
    /// </summary>
    private static Control BuildSettingsTabButtonFallback(string text, out Button button, out TextureRect outline, out Label label)
    {
        button = new Button { Flat = true, CustomMinimumSize = new Vector2(256f, 90f) };
        button.Pressed += PlayClickSfx;
        var empty = new StyleBoxEmpty();
        foreach (string slot in new[] { "normal", "hover", "pressed", "focus" })
        {
            button.AddThemeStyleboxOverride(slot, empty);
        }

        var image = new TextureRect
        {
            Texture = GD.Load<Texture2D>(SettingsTabBannerTexturePath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        image.SetAnchorsPreset(LayoutPreset.FullRect);
        Shader? tabShader = GD.Load<Shader>(HsvShaderPath);
        if (tabShader != null)
        {
            var tabMaterial = new ShaderMaterial { Shader = tabShader };
            tabMaterial.SetShaderParameter("h", 1f);
            tabMaterial.SetShaderParameter("s", 1f);
            tabMaterial.SetShaderParameter("v", 0.9f);
            image.Material = tabMaterial;
        }
        button.AddChild(image);

        outline = new TextureRect
        {
            Texture = GD.Load<Texture2D>(SettingsTabStrokeTexturePath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            Modulate = SettingsTabOutlineModulate,
            Visible = false,
            Material = GD.Load<Material>(SettingsTabAdditiveMaterialPath),
        };
        outline.SetAnchorsPreset(LayoutPreset.FullRect);
        button.AddChild(outline);

        label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        label.AddThemeColorOverride("font_color", StsColors.halfTransparentCream);
        // settings_tab.tscn's own Label is 32px, but that's paired with MegaLabel's auto-shrink (MinFontSize 18,
        // MaxFontSize 32) for when a tab's text runs long - we don't have that safety net, so this sits a bit under
        // native's max as a fixed size instead of risking overflow on our (longer, Korean) tab labels.
        label.AddThemeFontSizeOverride("font_size", 28);
        Font? titleFont = ResolveFont(TitleFontPath, FontType.Bold);
        if (titleFont != null)
        {
            label.AddThemeFontOverride("font", titleFont);
        }
        button.AddChild(label);

        return button;
    }

    /// <summary>Godot's default ScrollContainer scrollbar is a thin, barely-visible gray sliver - practically
    /// invisible against our dark backgrounds. The real settings screen has an unmissable gold diamond-handled
    /// scrollbar (scenes/ui/scrollbar.tscn); reusing that exact custom asset felt like more risk than it's worth
    /// (another multi-piece nine-patch-shaped scene, the same category of thing that broke buttons earlier this
    /// session), so this just recolors Godot's own scrollbar to be legible instead: gold grabber, dark track.</summary>
    private static void StyleScrollbar(ScrollContainer scroll)
    {
        VScrollBar vBar = scroll.GetVScrollBar();
        var grabber = new StyleBoxFlat { BgColor = StsColors.gold };
        grabber.SetCornerRadiusAll(4);
        var track = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.35f) };
        track.SetCornerRadiusAll(4);
        vBar.AddThemeStyleboxOverride("grabber", grabber);
        vBar.AddThemeStyleboxOverride("grabber_highlight", grabber);
        vBar.AddThemeStyleboxOverride("grabber_pressed", grabber);
        vBar.AddThemeStyleboxOverride("scroll", track);
        vBar.AddThemeStyleboxOverride("scroll_focus", track);
    }

    /// <summary>
    /// Reuses the game's own bottom-left "back" button (arrow banner shape) instead of a plain close button.
    /// NBackButton does NOT position itself via anchors after _Ready() - it reads GetWindow().ContentScaleSize
    /// (the whole window's size) and manually tweens its own Position to sit flush at the window's bottom-left,
    /// so it must be a direct child of a control that spans the full window at origin (us - "this" is FullRect),
    /// not the small popup-sized background, or its self-computed position lands way outside that container.
    /// It also starts hidden (parked off-screen) until Enable() is called, which must happen after _Ready() has
    /// already run (i.e. after we're already inside the live scene tree via NModalContainer.Add) so its internal
    /// node references are populated - calling it too early throws inside OnEnable(). We keep its scene-authored
    /// offsets untouched since those are what the show/hide tween math is built around.
    /// Falls back to a plain guaranteed-visible button if anything about instantiating the scene goes wrong, so a
    /// popup can never end up with no way to close it again.
    /// </summary>
    /// <summary>The NBackButton created by AddCloseButton, if the native scene loaded successfully (null on the
    /// plain-button fallback path). Exposed so subclasses that need to temporarily hide/reshow the close button
    /// (e.g. while a "confirm in progress" state has its own cancel control - see AddCancelButton) can call
    /// Enable()/Disable() on the exact same instance instead of tracking a second reference.</summary>
    protected NBackButton? CloseButton { get; private set; }

    private void AddCloseButton()
    {
        CloseButton = BuildCloseButton(this, OnCloseRequested);
    }

    /// <summary>
    /// AddCloseButton's own body, extracted as a static helper so non-Sts2ModalPanel callers - e.g.
    /// KickBanPromptPatch augmenting a native NErrorPopup, which is also a FullRect Control at origin (see
    /// error_popup.tscn's root, anchors_preset=15) but isn't one of our own panels - can attach the exact same
    /// native close control instead of hand-rolling a second copy of this fallback/positioning logic.
    /// fullRectParent must span the full window at origin for the same reason AddCloseButton's own doc requires
    /// it of "this": NBackButton computes its own on-screen position from the window's size, not its parent's.
    /// </summary>
    internal static NBackButton? BuildCloseButton(Control fullRectParent, Action onClose)
    {
        try
        {
            PackedScene? scene = PreloadManager.Cache.GetScene(BackButtonScenePath);
            if (scene == null)
            {
                throw new InvalidOperationException($"GetScene returned null for {BackButtonScenePath}");
            }
            var backButton = scene.Instantiate<NBackButton>(PackedScene.GenEditState.Disabled);
            backButton.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => onClose()));
            fullRectParent.AddChild(backButton);
            backButton.Enable();
            return backButton;
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to build NBackButton close control, falling back to a plain button: {ex}");
            var fallback = new Button { Text = "닫기" };
            fallback.Pressed += PlayClickSfx;
            fallback.SetAnchorsPreset(LayoutPreset.BottomWide);
            fallback.OffsetTop = -50f;
            fallback.OffsetBottom = -14f;
            fallback.OffsetLeft = 60f;
            fallback.OffsetRight = -60f;
            fallback.AddThemeColorOverride("font_color", StsColors.cream);
            fallback.Pressed += onClose;
            fullRectParent.AddChild(fallback);
            return null;
        }
    }

    /// <summary>
    /// Same bottom-left back_button.tscn slot as AddCloseButton, but with the icon swapped to the "X"/cancel
    /// variant the game itself uses for exactly this purpose (see the CancelIconTexturePath doc above) - meant to
    /// stand in for AddCloseButton's NBackButton while some in-progress action (that the close button can't safely
    /// interrupt) is running, e.g. NCharacterSelectScreen's UnreadyButton standing in for BackButton while waiting
    /// on other players. Starts disabled/off-screen (unlike AddCloseButton, which enables itself immediately) -
    /// callers Enable() it only once whatever "in progress" state they're guarding actually begins, and Disable()
    /// it again (re-enabling the real close button) once that state ends.
    /// </summary>
    protected NBackButton? AddCancelButton(string tooltip, Action onCancel)
    {
        try
        {
            PackedScene? scene = PreloadManager.Cache.GetScene(BackButtonScenePath);
            if (scene == null)
            {
                throw new InvalidOperationException($"GetScene returned null for {BackButtonScenePath}");
            }
            var cancelButton = scene.Instantiate<NBackButton>(PackedScene.GenEditState.Disabled);
            Texture2D? xIcon = GD.Load<Texture2D>(CancelIconTexturePath);
            if (xIcon != null)
            {
                cancelButton.GetNode<TextureRect>("Image/Icon").Texture = xIcon;
            }
            cancelButton.TooltipText = tooltip;
            cancelButton.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => onCancel()));
            AddChild(cancelButton);
            return cancelButton;
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to build NBackButton cancel control: {ex}");
            return null;
        }
    }

    /// <summary>
    /// The game's own generic dialog "Yes" button (green ribbon, e.g. "확인" on a plain popup or "버그 제보" on
    /// NErrorPopup - same button, just a different label) - see reference/CommonUi/NVerticalPopup.cs
    /// (InitYesButton), NPopupYesNoButton.cs, and reference/ui/abandon_run_yes_button.tscn. Unlike
    /// NConfirmButton/NBackButton this doesn't self-position via NGame.Instance.Size math (no OnWindowChange
    /// override in NPopupYesNoButton), so it's a perfectly ordinary Control as far as anchors/offsets go - callers
    /// position it themselves after AddChild, same as any other Control. Fixed 180x72 native size (not
    /// auto-sized to text, unlike BuildTextActionButton) - its own texture/outline art is designed for that exact
    /// box, per abandon_run_yes_button.tscn.
    /// Parented to PopupBackground (the painted popup_vertical.tres box itself), NOT "this" the way
    /// AddCloseButton/AddConfirmButton/AddCancelButton are - those three deliberately need "this" (the full-window
    /// FullRect Control) because NBackButton/NConfirmButton compute their own on-screen position from
    /// NGame.Instance.Size, i.e. genuinely screen-relative regardless of what parents them. This button has no such
    /// math - in the native scene it's a plain child of the popup box itself (vertical_popup.tscn's own YesButton
    /// node), so anchoring it to "this" instead would resolve BottomRight against the whole screen, not the popup
    /// box, landing it in the actual corner of the window rather than near the popup. Falls back to "this" only if
    /// ShowAsModal/ShowAsTabbedSettingsScreen was never called (PopupBackground still null), so this never throws
    /// for a caller that skips ShowAsModal.
    /// Falls back to a plain BuildTextActionButton (in the same green-ish hue, approximated) if the scene fails to
    /// load, so a dialog's confirm action never silently disappears.
    /// </summary>
    protected Control AddPopupYesButton(string text, Action onConfirmed)
    {
        Control parent = PopupBackground ?? this;
        try
        {
            PackedScene? scene = PreloadManager.Cache.GetScene(PopupYesButtonScenePath);
            if (scene == null)
            {
                throw new InvalidOperationException($"GetScene returned null for {PopupYesButtonScenePath}");
            }
            var button = scene.Instantiate<NPopupYesNoButton>(PackedScene.GenEditState.Disabled);
            // AddChild BEFORE touching IsYes/SetText - both read/write internal fields (e.g. _label) that
            // NPopupYesNoButton's own _Ready() populates, which only runs once the node actually enters the live
            // scene tree. parent (PopupBackground/this) is already live at this call site (ShowAsModal already
            // added the whole panel by now), so this AddChild fires _Ready() synchronously before we return.
            parent.AddChild(button);
            button.IsYes = true;
            button.SetText(text);
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => onConfirmed()));
            return button;
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to build NPopupYesNoButton, falling back to a plain button: {ex}");
            // Explicit green hex, not a hue/sat/value shader triple - ApproximateShaderColor's h/s/v math is
            // calibrated to reward_item_button.png's own native hue (see PillButtonNativeHueDegrees), a totally
            // different source texture than abandon_run_yes_button.tscn's popup_confirm_button.tres, so it can't
            // reproduce this green from the same numbers.
            var fallback = BuildTextActionButton(text, 72f, explicitColor: new Color("3C7A2E"));
            fallback.Pressed += onConfirmed;
            parent.AddChild(fallback);
            return fallback;
        }
    }

    /// <summary>
    /// Shared confirm/cancel/content-blocker flip for any window built around an AddConfirmButton + AddCancelButton
    /// pair guarding an "operation in progress" state - mirrors NCharacterSelectScreen's own OnEmbarkPressed/
    /// OnUnreadyPressed dance (see AddCancelButton's doc). Callers just need one bool for "is that state active
    /// right now" (MatchConditionsWindow: already open to matching; MatchmakingWindow: an auto-match/rehost-wait is
    /// pending) - null-safe throughout so the AddConfirmButton/AddCancelButton scene-load-failure fallback path
    /// degrades quietly instead of throwing.
    /// </summary>
    protected void ApplyConfirmCancelState(bool inProgress, NConfirmButton? confirmButton, NBackButton? cancelButton)
    {
        if (inProgress)
        {
            confirmButton?.Disable();
            CloseButton?.Disable();
            cancelButton?.Enable();
        }
        else
        {
            cancelButton?.Disable();
            CloseButton?.Enable();
            confirmButton?.Enable();
        }
        if (ContentBlocker != null)
        {
            ContentBlocker.Visible = inProgress;
        }
    }

    /// <summary>Called when the close button is pressed. Override to persist state before closing; call base last.</summary>
    protected virtual void OnCloseRequested()
    {
        NModalContainer.Instance?.Clear();
    }

    /// <summary>
    /// Reuses the game's own bottom-right checkmark "confirm" button (scenes/ui/confirm_button.tscn -> NConfirmButton
    /// - see reference/ui/confirm_button.tscn and reference/CommonUi/NConfirmButton.cs) instead of a plain text
    /// button, for an action that reads as "confirm this choice" rather than a labeled command. Same positioning
    /// requirement as AddCloseButton's NBackButton: NConfirmButton computes its own absolute on-screen position
    /// from NGame.Instance.Size minus its scene-authored offsets rather than using anchors, so it must be a direct
    /// child of a control that spans the full window at origin ("this"), not the small popup-sized background -
    /// and it starts hidden until Enable() is called, same as the back button.
    /// No text label exists on the native scene (just Shadow/Outline/Image/tick-Icon textures), so callers convey
    /// what "confirm" means here via TooltipText instead. Returns null (rather than a Control standing in for the
    /// fallback button) on the rare scene-load-failure path below - callers that need to Enable()/Disable() this
    /// alongside a matching AddCancelButton (e.g. to slide it off-screen while some in-progress state is guarded by
    /// a cancel button instead) should null-check rather than assume the native control exists.
    /// Falls back to a plain guaranteed-visible button if anything about instantiating the scene goes wrong.
    /// </summary>
    protected NConfirmButton? AddConfirmButton(string tooltip, Action onConfirmed)
    {
        try
        {
            PackedScene? scene = PreloadManager.Cache.GetScene(ConfirmButtonScenePath);
            if (scene == null)
            {
                throw new InvalidOperationException($"GetScene returned null for {ConfirmButtonScenePath}");
            }
            var confirmButton = scene.Instantiate<NConfirmButton>(PackedScene.GenEditState.Disabled);
            confirmButton.TooltipText = tooltip;
            confirmButton.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => onConfirmed()));
            AddChild(confirmButton);
            confirmButton.Enable();
            return confirmButton;
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to build NConfirmButton, falling back to a plain button: {ex}");
            var fallback = new Button { Text = tooltip, TooltipText = tooltip };
            StyleAsSettingsButton(fallback);
            fallback.SetAnchorsPreset(LayoutPreset.BottomRight);
            fallback.OffsetTop = -64f;
            fallback.OffsetBottom = -14f;
            fallback.OffsetLeft = -160f;
            fallback.OffsetRight = -14f;
            fallback.Pressed += onConfirmed;
            AddChild(fallback);
            return null;
        }
    }

    /// <summary>Hue (in the shader's own [0,1] "rotation amount" space, not a standard color wheel) of the reward
    /// button texture's own native color, measured from its pixels (RGB (34,225,235) -> ~183° cyan-teal). Needed to
    /// translate a shader h/s/v triple into an approximate standalone Color for deriving an outline tint below -
    /// the shader rotates the TEXTURE's existing hue by (1-h)*360°, it doesn't set an absolute target hue, so h=1
    /// alone doesn't tell us what color actually comes out.</summary>
    private const float PillButtonNativeHueDegrees = 183f;

    /// <summary>
    /// Builds a pill-shaped button matching the vanilla invite button's look (remote_player_container.tscn):
    /// same background texture + HSV tint shader (given a distinct hue per caller so different actions don't all
    /// look identical) and the same fonts on its label, but backed by a plain Button so it carries none of the
    /// original scene's baked-in click behavior. Defaults (h=1, s=1, v=0.9) reproduce the exact "no shift" native
    /// teal used by Invite/reward/join-friend buttons throughout the game, rather than an arbitrary custom hue.
    /// Originally written for RecruitToggleInjector's in-lobby toggle; shared here so popup content (dialog
    /// buttons, etc.) gets the same game-native look instead of raw Godot Button chrome.
    ///
    /// Background is a plain stretched TextureRect, NOT NinePatchRect - tried NinePatchRect once (to fix the
    /// gear button's corner distortion at 50x50) and it broke every pill button in-game. Root cause: NinePatchRect
    /// reports GetMinimumSize() = (patch_margin_left+right, patch_margin_top+bottom) and Godot's layout system
    /// won't let a Control render smaller than its own minimum size even when a parent's anchors say "fill this
    /// smaller box" - with a margin large enough to actually capture reward_item_button.png's corner curve
    /// (~48px), that minimum size (96x96) is bigger than every button we build (all 50px tall), so the background
    /// forced itself larger than the button and spilled outward instead of clipping to it. The corner asset's
    /// curve genuinely doesn't fit nine-slicing at this UI scale - see tools/UiPreview for how this was diagnosed
    /// offline (renders the exact composite without needing the game running).
    /// </summary>
    internal static Button BuildPillButton(Vector2 size, out RichTextLabel label, float hue = 1f, float saturation = 1f, float value = 0.9f, Color? outlineColor = null)
    {
        var button = new Button { CustomMinimumSize = size, Flat = true };
        button.Pressed += PlayClickSfx;
        var empty = new StyleBoxEmpty();
        button.AddThemeStyleboxOverride("normal", empty);
        button.AddThemeStyleboxOverride("hover", empty);
        button.AddThemeStyleboxOverride("pressed", empty);
        button.AddThemeStyleboxOverride("focus", empty);

        var background = new TextureRect
        {
            Texture = GD.Load<Texture2D>(PillButtonTexturePath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        Shader? shader = GD.Load<Shader>(HsvShaderPath);
        if (shader != null)
        {
            var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter("h", hue);
            material.SetShaderParameter("s", saturation);
            material.SetShaderParameter("v", value);
            background.Material = material;
        }
        button.AddChild(background);

        label = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = false,
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        label.AddThemeColorOverride("default_color", StsColors.cream);
        label.AddThemeColorOverride("font_outline_color", outlineColor ?? DeriveOutlineColor(hue, saturation, value));
        label.AddThemeConstantOverride("outline_size", 12);
        // 24 matches remote_player_container.tscn's own invite-button Label exactly (normal/bold_font_size = 24) -
        // was 22, a guess from before we had the actual scene to check against.
        label.AddThemeFontSizeOverride("normal_font_size", 24);
        label.AddThemeFontSizeOverride("bold_font_size", 24);
        Font? regularFont = ResolveFont(PillButtonRegularFontPath, FontType.Regular);
        if (regularFont != null)
        {
            label.AddThemeFontOverride("normal_font", regularFont);
        }
        Font? boldFont = ResolveFont(PillButtonBoldFontPath, FontType.Bold);
        if (boldFont != null)
        {
            label.AddThemeFontOverride("bold_font", boldFont);
        }
        button.AddChild(label);
        return button;
    }

    /// <summary>
    /// Builds a button matching settings_screen.tscn's own "Send Feedback"/"Credits"/"Reset Tutorials" row
    /// buttons exactly - same reward_skip_button.png background + HSV tint shader, same label styling/font/
    /// outline - a second native template distinct from BuildPillButton's invite-button style (different source
    /// texture: reward_skip_button.png, not reward_item_button.png). h/s/v defaults (0.82/1.4/0.8) are copied
    /// as-is from "Send Feedback"'s own ShaderMaterial_prf3m rather than re-derived, since this is meant to
    /// reproduce that ONE specific native button precisely, not support an arbitrary caller-chosen hue the way
    /// BuildPillButton does. Doesn't reproduce the native SelectionReticle hover glow (a separately-instanced
    /// scene whose internal wiring requirements weren't verified) - callers wanting hover feedback should use
    /// WireHoverBrightness instead, same as BuildPillButton's own callers do.
    /// </summary>
    internal static Button BuildSettingsActionButton(string text, Vector2 size, float hue = 0.82f, float saturation = 1.4f, float value = 0.8f)
    {
        var button = new Button { CustomMinimumSize = size, Flat = true };
        button.Pressed += PlayClickSfx;
        var empty = new StyleBoxEmpty();
        button.AddThemeStyleboxOverride("normal", empty);
        button.AddThemeStyleboxOverride("hover", empty);
        button.AddThemeStyleboxOverride("pressed", empty);
        button.AddThemeStyleboxOverride("focus", empty);

        var background = new TextureRect
        {
            Texture = GD.Load<Texture2D>(SettingsActionButtonTexturePath),
            // IgnoreSize + KeepAspectCentered - settings_screen.tscn's own FeedbackButton/Image uses
            // expand_mode=1/stretch_mode=5 exactly (NOT Scale, which is BuildPillButton's own choice for a
            // different texture/button - reusing it here stretched reward_skip_button.png off its native aspect
            // ratio, which is what actually looked "off").
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        Shader? shader = GD.Load<Shader>(HsvShaderPath);
        if (shader != null)
        {
            var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter("h", hue);
            material.SetShaderParameter("s", saturation);
            material.SetShaderParameter("v", value);
            background.Material = material;
        }
        button.AddChild(background);

        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        // Exact values copied from settings_screen.tscn's own FeedbackButton/Label, not approximated.
        label.AddThemeColorOverride("font_color", new Color(0.91f, 0.86359f, 0.7462f, 1f));
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.25098f));
        label.AddThemeColorOverride("font_outline_color", new Color(0.1274f, 0.26f, 0.14066f, 1f));
        label.AddThemeConstantOverride("shadow_offset_x", 4);
        label.AddThemeConstantOverride("shadow_offset_y", 3);
        label.AddThemeConstantOverride("outline_size", 12);
        label.AddThemeFontSizeOverride("font_size", 28);
        Font? font = ResolveFont(SettingsActionButtonFontPath, FontType.Bold);
        if (font != null)
        {
            label.AddThemeFontOverride("font", font);
        }
        button.AddChild(label);

        return button;
    }

    /// <summary>
    /// Subtle brightness+saturation pop on hover (s/v: 1 -> 1.1, reverting on exit) via BuildPillButton's own HSV
    /// shader material - matches the vanilla "초대" button's hover feel exactly (NInvitePlayersButton.OnFocus/
    /// OnUnfocus, see reference/Multiplayer/NInvitePlayersButton.cs). Shared by every plain pill button that wants
    /// SOME hover feedback but not a bespoke animation.
    /// </summary>
    internal static void WireHoverBrightness(Button button)
    {
        // BuildPillButton always adds the background TextureRect (with the shared HSV shader) as child 0.
        var hsv = button.GetChildOrNull<TextureRect>(0)?.Material as ShaderMaterial;
        if (hsv == null)
        {
            return;
        }
        button.MouseEntered += () =>
        {
            hsv.SetShaderParameter("s", 1.1f);
            hsv.SetShaderParameter("v", 1.1f);
        };
        button.MouseExited += () =>
        {
            hsv.SetShaderParameter("s", 1f);
            hsv.SetShaderParameter("v", 1f);
        };
    }

    /// <summary>
    /// Approximates the actual on-screen color a given shader h/s/v triple paints reward_item_button.png with -
    /// only an approximation of the shader's true YIQ rotation (assumes shader (s,v) map onto standard-HSV
    /// saturation/value closely enough for a background chrome detail), not the literal pixel result. Shared by
    /// DeriveOutlineColor (a darkened version, for text outlines), BuildIconButton (used as-is, for a flat
    /// background fill), and subclasses (protected, not private) that need to reproduce a specific BuildPillButton
    /// h/s/v look (e.g. its muted cyan) as an explicitColor on a BuildIconButton/BuildTextActionButton instead -
    /// BuildPillButton's shader can't be asked for an arbitrary hex directly, but it CAN be asked "what color does
    /// this h/s/v triple actually paint", which is exactly what this already computes.
    /// </summary>
    protected static Color ApproximateShaderColor(float hue, float saturation, float value)
    {
        float wheelHue = Mathf.PosMod(PillButtonNativeHueDegrees / 360f + (1f - hue), 1f);
        return Color.FromHsv(wheelHue, Mathf.Clamp(saturation, 0f, 1f), Mathf.Clamp(value, 0f, 1f));
    }

    /// <summary>
    /// A darker shade of whatever color the given shader h/s/v triple actually paints the button - not a fixed
    /// constant. The invite button's own outline works (per direct comparison) because it's a darkened version of
    /// its OWN fill color, not a color borrowed from somewhere else; our previous buttons all reused invite's exact
    /// teal outline regardless of their own tint, which looked wrong on anything shifted away from teal. Callers
    /// with a known-correct native pairing (e.g. an exact vanilla button's own outline) should pass outlineColor
    /// explicitly instead of relying on this.
    /// </summary>
    private static Color DeriveOutlineColor(float hue, float saturation, float value)
    {
        return ApproximateShaderColor(hue, saturation, value).Darkened(0.6f);
    }

    /// <summary>
    /// Red "danger/moderation" text-pill button (StyleBoxFlat rounded box + drop shadow, no border) sized to its
    /// own text instead of a fixed square - short words (킥/밴/밴 등록) read faster than the emoji glyphs these
    /// replaced, and a fixed-width square button can't host that without either clipping longer labels or wasting
    /// space around short ones.
    /// Height is fixed (a row of these needs consistent height to line up); width is computed from the actual
    /// rendered text size (Font.GetStringSize) plus the StyleBoxFlat's own ContentMargin padding below, so longer
    /// text (a different UI language, "밴 등록" vs "밴") still grows the button instead of overflowing/clipping -
    /// just computed by us instead of left to Godot's own Button auto-sizing (see next paragraph for why).
    /// Renders through a child Label, NOT Button's own Text property (an earlier version of
    /// this used Text directly, relying on it for free auto-sizing) - turns out Button's theme schema has no
    /// font_shadow_color/shadow_offset_x/shadow_offset_y keys at all (those are Label-only), so a shadow set via
    /// AddThemeColorOverride/AddThemeConstantOverride on the Button itself is silently a no-op: stored under a key
    /// its own draw code never reads, not an error, just invisible. Only outline_size/font_outline_color actually
    /// exist on Button's schema, which is why the outline showed but the shadow never did.
    /// </summary>
    internal static Button BuildTextActionButton(string text, float height, float hue = 1f, float saturation = 1f, float value = 0.9f, Color? explicitColor = null)
    {
        Color baseColor = explicitColor ?? ApproximateShaderColor(hue, saturation, value);
        Color hoverColor = Color.FromHsv(baseColor.H, Mathf.Min(1f, baseColor.S * 1.1f), Mathf.Min(1f, baseColor.V * 1.1f), baseColor.A);
        Color pressedColor = Color.FromHsv(baseColor.H, baseColor.S, Mathf.Max(0f, baseColor.V * 0.85f), baseColor.A);
        int cornerRadius = Mathf.RoundToInt(height * 0.2f);
        int shadowSize = Mathf.Max(2, Mathf.RoundToInt(height * 0.12f));
        var shadowOffset = new Vector2(0f, Mathf.Max(1, Mathf.RoundToInt(height * 0.06f)));
        // Left/right specifically tuned so a single-glyph label ("킥"/"밴") renders as a square button
        // (height == width) rather than a noticeably wide pill - an estimate based on typical CJK glyph advance
        // width at this font size, not a measured value, so it may need another pass once seen live.
        float marginLeftRight = height * 0.3f;
        float marginTopBottom = height * 0.08f;

        var normal = new StyleBoxFlat { BgColor = baseColor };
        var hover = new StyleBoxFlat { BgColor = hoverColor };
        var pressed = new StyleBoxFlat { BgColor = pressedColor };
        foreach (StyleBoxFlat box in new[] { normal, hover, pressed })
        {
            box.SetCornerRadiusAll(cornerRadius);
            box.ShadowColor = new Color(0f, 0f, 0f, 0.5f);
            box.ShadowSize = shadowSize;
            box.ShadowOffset = shadowOffset;
            box.ContentMarginLeft = marginLeftRight;
            box.ContentMarginRight = marginLeftRight;
            box.ContentMarginTop = marginTopBottom;
            box.ContentMarginBottom = marginTopBottom;
        }

        // 0.4, not the original 0.32 - matches BuildIconButton's own glyph-size ratio (size.Y * 0.4f) now that
        // padding shrank to make room, instead of a smaller custom ratio just for this button.
        int fontSize = Mathf.Max(8, Mathf.RoundToInt(height * 0.4f));
        int outlineSize = Mathf.Max(2, Mathf.RoundToInt(height * 0.12f));
        // Was 0.04/alpha 0.35 - barely registered next to the button box's own much more visible drop shadow
        // (ShadowSize scaled at 0.12, ShadowColor alpha 0.5) - but 0.08 offset with no softening read as a harsh
        // duplicate copy of the text, not a shadow. Small offset + shadowOutlineSize (Label's shadow-only "blur"
        // knob - thickens/softens just the shadow copy's silhouette, independent of the real outline_size above)
        // together read much closer to the box's own soft drop shadow: short distance, wide spread.
        int glyphShadowOffset = Mathf.Max(1, Mathf.RoundToInt(height * 0.04f));
        int shadowBlur = Mathf.Max(3, Mathf.RoundToInt(height * 0.1f));

        // Same font-substitution requirement as every other text element (ResolveFont swaps in a per-language
        // fallback, e.g. Korean gets a non-Kreon font) - PillButtonBoldFontPath/Bold to match every other short
        // action-button label (StyleAsSettingsButton, BuildSettingsTabButtonFallback) at this size. Resolved before the
        // width measurement below since that needs a concrete Font to measure text against.
        Font? boldFont = ResolveFont(PillButtonBoldFontPath, FontType.Bold);
        Font measureFont = boldFont ?? ThemeDB.FallbackFont;
        float textWidth = measureFont.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;

        var button = new Button
        {
            CustomMinimumSize = new Vector2(textWidth + marginLeftRight * 2f, height),
            Flat = false,
        };
        button.Pressed += PlayClickSfx;
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("focus", hover);

        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        label.AddThemeColorOverride("font_color", StsColors.cream);
        label.AddThemeColorOverride("font_outline_color", explicitColor?.Darkened(0.6f) ?? DeriveOutlineColor(hue, saturation, value));
        label.AddThemeConstantOverride("outline_size", outlineSize);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.6f));
        label.AddThemeConstantOverride("shadow_offset_x", glyphShadowOffset);
        label.AddThemeConstantOverride("shadow_offset_y", glyphShadowOffset);
        label.AddThemeConstantOverride("shadow_outline_size", shadowBlur);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        if (boldFont != null)
        {
            label.AddThemeFontOverride("font", boldFont);
        }
        button.AddChild(label);
        return button;
    }

    /// <summary>
    /// Updates the text on a button built by BuildTextActionButton (e.g. toggling "밴 등록"/"밴 해제" in place) -
    /// button.Text itself must NOT be touched for this (see BuildTextActionButton's own doc: it's left at its
    /// default "", since the visible text is a separate child Label). Setting button.Text anyway doesn't silently
    /// no-op the way the shadow keys did - Button DOES render its own native Text using the engine's default
    /// theme, so doing that turns on a second, un-styled text layer sitting right on top of (and never in sync
    /// with) our own child Label, which is what "겹쳐 보인다" (overlapping text) turned out to be. Also
    /// recomputes CustomMinimumSize.X the same way BuildTextActionButton originally did, so a differently-sized
    /// replacement label (e.g. "밴 등록" vs "밴 해제") doesn't clip or leave stray padding.
    /// </summary>
    internal static void UpdateTextActionButtonText(Button button, string text)
    {
        var label = button.GetChild<Label>(0);
        label.Text = text;

        int fontSize = label.GetThemeFontSize("font_size");
        Font font = label.GetThemeFont("font") ?? ThemeDB.FallbackFont;
        float height = button.CustomMinimumSize.Y;
        float marginLeftRight = height * 0.3f;
        float textWidth = font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;
        button.CustomMinimumSize = new Vector2(textWidth + marginLeftRight * 2f, height);
    }

    /// <summary>
    /// Cream-colored, dark-outlined label text - the same treatment ShowAsModal uses for the title (just a smaller
    /// outline), so body copy reads clearly against the popup's painted background regardless of the HSV tint
    /// applied to it, instead of falling back to Godot's default (often low-contrast) label color.
    /// </summary>
    internal static Label StyleBodyLabel(Label label, bool emphasize = false)
    {
        label.AddThemeColorOverride("font_color", emphasize ? StsColors.gold : StsColors.cream);
        label.AddThemeColorOverride("font_outline_color", new Color(0.134729f, 0.315898f, 0.341731f, 1f));
        label.AddThemeConstantOverride("outline_size", 6);
        return label;
    }

    /// <summary>
    /// Flat-colored input chrome (dark translucent fill, gold border) for LineEdit/OptionButton, standing in for
    /// Godot's default gray engine boxes which otherwise look out of place on the painted popup background - the
    /// game doesn't expose a plain reusable text-field skin we can instantiate, so this approximates one instead of
    /// leaving inputs unstyled like MatchConditionsPanel's controls currently are.
    /// </summary>
    protected static StyleBoxFlat BuildInputStyleBox()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.45f),
            BorderColor = StsColors.gold,
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        style.ContentMarginLeft = 10f;
        style.ContentMarginRight = 10f;
        style.ContentMarginTop = 6f;
        style.ContentMarginBottom = 6f;
        return style;
    }

    /// <summary>Gold border only while actually focused - BuildInputStyleBox's own border is the "focus" look, so
    /// "normal" here is the same box with the border stripped (0-width, transparent) instead of reusing the exact
    /// same StyleBoxFlat for both slots the way an earlier version of this did. That earlier version meant the
    /// gold border was permanently visible regardless of focus state, making focused vs unfocused indistinguishable
    /// - not a deliberate "always show the border" style choice, just an oversight (this input has no vanilla
    /// equivalent to source a focus-only treatment from, unlike the native-scene selects elsewhere in this file).</summary>
    internal static void StyleInput(LineEdit lineEdit)
    {
        StyleBoxFlat normal = BuildInputStyleBox();
        normal.BorderColor = new Color(0f, 0f, 0f, 0f);
        normal.SetBorderWidthAll(0);
        StyleBoxFlat focus = BuildInputStyleBox();
        lineEdit.AddThemeStyleboxOverride("normal", normal);
        lineEdit.AddThemeStyleboxOverride("focus", focus);
        lineEdit.AddThemeStyleboxOverride("read_only", normal);
        lineEdit.AddThemeColorOverride("font_color", StsColors.cream);
        lineEdit.AddThemeColorOverride("font_placeholder_color", StsColors.halfTransparentCream);
        lineEdit.AddThemeColorOverride("caret_color", StsColors.gold);
    }

    /// <summary>
    /// Same navy/gold theming as StyleAsSettingsDropdown, applied to plain action Buttons (매칭 시작, 밴 목록
    /// 관리, etc) instead of leaving them on Godot's default gray engine chrome - those buttons don't have a
    /// direct settings-dropdown equivalent to source exact values from, so this reuses our own already-established
    /// navy palette (SettingsDropdownUnfocused/Focused) rather than inventing a third color scheme.
    /// </summary>
    internal static void StyleAsSettingsButton(Button button)
    {
        button.Pressed += PlayClickSfx;
        var normal = new StyleBoxFlat { BgColor = SettingsDropdownUnfocused };
        var hover = new StyleBoxFlat { BgColor = SettingsDropdownFocused };
        foreach (StyleBoxFlat box in new[] { normal, hover })
        {
            box.ContentMarginLeft = 14f;
            box.ContentMarginRight = 14f;
            box.ContentMarginTop = 8f;
            box.ContentMarginBottom = 8f;
        }
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", hover);
        button.AddThemeStyleboxOverride("focus", hover);
        button.AddThemeStyleboxOverride("disabled", normal);
        button.AddThemeColorOverride("font_color", StsColors.gold);
        button.AddThemeColorOverride("font_hover_color", StsColors.gold);
        button.AddThemeColorOverride("font_pressed_color", StsColors.gold);
        button.AddThemeColorOverride("font_disabled_color", StsColors.halfTransparentCream);
        Font? boldFont = ResolveFont(PillButtonBoldFontPath, FontType.Bold);
        if (boldFont != null)
        {
            button.AddThemeFontOverride("font", boldFont);
        }
        button.AddThemeFontSizeOverride("font_size", 18);
    }

    /// <summary>
    /// The exact checkbox icons the settings screen itself uses (scenes/ui/tickbox.tscn: checkbox_ticked.tres /
    /// checkbox_unticked.tres, a dark navy squircle with a gold checkmark when ticked, h=1/s=1/v=1 native color -
    /// no shader math needed, just the raw icons). Godot's CheckBox already supports icon-only theming via the
    /// checked/unchecked/radio_checked/radio_unchecked icon slots, so this needs no custom drawing - just point it
    /// at the real assets instead of leaving the default engine checkbox glyph.
    /// </summary>
    internal static void StyleAsSettingsCheckbox(CheckBox checkBox)
    {
        Texture2D? ticked = GD.Load<Texture2D>(CheckboxTickedTexturePath);
        Texture2D? unticked = GD.Load<Texture2D>(CheckboxUntickedTexturePath);
        if (ticked != null)
        {
            checkBox.AddThemeIconOverride("checked", ticked);
            checkBox.AddThemeIconOverride("checked_disabled", ticked);
        }
        if (unticked != null)
        {
            checkBox.AddThemeIconOverride("unchecked", unticked);
            checkBox.AddThemeIconOverride("unchecked_disabled", unticked);
        }
        checkBox.AddThemeColorOverride("font_color", StsColors.cream);
        checkBox.AddThemeColorOverride("font_hover_color", StsColors.gold);
        checkBox.AddThemeConstantOverride("h_separation", 12);
    }

    // Colors lifted directly from the settings screen's own dropdown (settings_dropdown.tscn +
    // NSettingsDropdown.OnFocus/OnUnfocus) - the "CurrentOption" row is a flat ColorRect (no rounded corners at
    // all) that darkens/brightens between these two exact values on focus, with the selected value shown in bold
    // gold text, and the opened option list sits on this near-black navy panel. Reproduced here (rather than our
    // own invented gold-bordered box from BuildInputStyleBox) for the selects the vanilla settings screen actually
    // has a direct equivalent for - game mode, player count, ascension level, language.
    protected static readonly Color SettingsDropdownUnfocused = new("2C434F");
    protected static readonly Color SettingsDropdownFocused = new("3C5B6B");
    protected static readonly Color SettingsDropdownPanel = new(0.0705882f, 0.129412f, 0.160784f, 1f);

    private static Texture2D? _cachedDropdownArrowIcon;
    private static bool _dropdownArrowIconLoadAttempted;
    private static Texture2D? _cachedTransparentPixel;

    /// <summary>
    /// settings_tiny_left_arrow.png points left natively; settings_dropdown.tscn rotates its Arrow node -90°
    /// (Godot Control.Rotation, clockwise-positive in screen space) to make it point down. Theme icon overrides on
    /// OptionButton draw the icon as-is with no rotation control, so the pixels themselves need to be rotated once
    /// up front instead - cached because every settings-styled dropdown calls this.
    /// </summary>
    private static Texture2D? BuildDropdownArrowIcon()
    {
        if (_dropdownArrowIconLoadAttempted)
        {
            return _cachedDropdownArrowIcon;
        }
        _dropdownArrowIconLoadAttempted = true;
        Texture2D? source = GD.Load<Texture2D>(DropdownArrowTexturePath);
        Image? image = source?.GetImage();
        if (image == null)
        {
            return null;
        }
        image.Rotate90(ClockDirection.Counterclockwise);
        // Native asset is 128x128 - settings_dropdown.tscn's own Arrow node is exactly 26x26 (offset_right(-13) -
        // offset_left(-39)). OptionButton draws theme icons at their native pixel size with no auto-scaling, so
        // left at 128px this would overflow our dropdown box entirely.
        image.Resize(26, 26, Image.Interpolation.Lanczos);
        _cachedDropdownArrowIcon = ImageTexture.CreateFromImage(image);
        return _cachedDropdownArrowIcon;
    }

    /// <summary>1x1 fully-transparent pixel, used to neutralize OptionButton's built-in icon slot (see
    /// StyleAsSettingsDropdown) without leaving a visible gap where a real icon would normally sit.</summary>
    private static Texture2D BuildTransparentPixelTexture()
    {
        if (_cachedTransparentPixel != null)
        {
            return _cachedTransparentPixel;
        }
        var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        image.SetPixel(0, 0, new Color(0, 0, 0, 0));
        _cachedTransparentPixel = ImageTexture.CreateFromImage(image);
        return _cachedTransparentPixel;
    }

    /// <summary>Native settings-dropdown look for a single-select OptionButton: flat (unrounded) navy box, bold
    /// gold current-value text with a drop shadow, near-black navy popup list. Size (320x64) and font size (28) are
    /// settings_dropdown.tscn's own exact values.
    ///
    /// Returns a wrapper Control instead of styling optionButton in place - callers should add the WRAPPER to
    /// their row/layout (e.g. via BuildSettingsRow), not the OptionButton directly, but keep using their own
    /// OptionButton reference for reading/setting selection (Select, Selected, GetItemId, ItemSelected, etc.),
    /// which all still work identically since the object itself is unchanged, just reparented.
    ///
    /// The wrapper does two things Godot's stock OptionButton can't do on its own, both confirmed by direct
    /// screenshot comparison against the real settings dropdown:
    /// 1. Centering: OptionButton lays out its icon and text as ONE combined block, reserving the icon's width from
    ///    the right before centering the remaining text in what's left - so even with Alignment=Center the text
    ///    visibly sits left of the box's true center. Fixed by giving OptionButton a 1x1 transparent "icon" (so its
    ///    reserved icon space collapses to ~nothing) and drawing the real arrow ourselves as a separate overlay.
    /// 2. Shadow: native's current-value text isn't actually an OptionButton's own text at all - settings_dropdown.
    ///    tscn's "Label" is a plain Label node with font_shadow_color/shadow_offset set. Godot's Button (and so
    ///    OptionButton) has no shadow theme slot whatsoever - only font_outline_color/outline_size - so no
    ///    combination of theme overrides on the OptionButton itself can produce a shadow. Fixed the same way as
    ///    native does it: make OptionButton's own text fully transparent and overlay a real Label (which does
    ///    support shadow) on top for the visible text. The overlay has to be kept in sync with the selection by
    ///    hand, since OptionButton.Select() (used for every *programmatic* selection in this codebase - initial
    ///    defaults, LoadFrom() restoring saved settings) does NOT fire ItemSelected (that signal only fires for
    ///    user-driven clicks) - so an ItemSelected hook alone would miss those. A lightweight polling Timer catches
    ///    every case uniformly instead of auditing/patching every call site that selects an item.
    /// </summary>
    internal static Control StyleAsSettingsDropdown(OptionButton optionButton)
    {
        var normal = new StyleBoxFlat { BgColor = SettingsDropdownUnfocused };
        var hover = new StyleBoxFlat { BgColor = SettingsDropdownFocused };
        foreach (StyleBoxFlat box in new[] { normal, hover })
        {
            box.ContentMarginLeft = 14f;
            box.ContentMarginRight = 14f;
            box.ContentMarginTop = 6f;
            box.ContentMarginBottom = 6f;
        }
        optionButton.CustomMinimumSize = new Vector2(320f, 64f);
        optionButton.SetAnchorsPreset(LayoutPreset.FullRect);
        optionButton.Alignment = HorizontalAlignment.Center;
        optionButton.AddThemeStyleboxOverride("normal", normal);
        optionButton.AddThemeStyleboxOverride("hover", hover);
        optionButton.AddThemeStyleboxOverride("pressed", hover);
        optionButton.AddThemeStyleboxOverride("focus", hover);
        optionButton.AddThemeStyleboxOverride("disabled", normal);
        // OptionButton's OWN text is made fully transparent (not blank) - it still needs to occupy the same layout
        // space and still needs to actually BE the clickable/focusable text for accessibility, we just don't want
        // it visibly double-drawn under our shadowed overlay Label.
        var transparent = new Color(0f, 0f, 0f, 0f);
        optionButton.AddThemeColorOverride("font_color", transparent);
        optionButton.AddThemeColorOverride("font_hover_color", transparent);
        optionButton.AddThemeColorOverride("font_pressed_color", transparent);
        optionButton.AddThemeColorOverride("font_focus_color", transparent);
        optionButton.AddThemeColorOverride("font_disabled_color", transparent);
        Font? boldFont = ResolveFont(PillButtonBoldFontPath, FontType.Bold);
        if (boldFont != null)
        {
            optionButton.AddThemeFontOverride("font", boldFont);
        }
        optionButton.AddThemeFontSizeOverride("font_size", 28);
        optionButton.AddThemeIconOverride("arrow", BuildTransparentPixelTexture());
        optionButton.AddThemeConstantOverride("icon_max_width", 1);

        PopupMenu popup = optionButton.GetPopup();
        popup.Theme = BodyTheme;
        popup.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = SettingsDropdownPanel });
        popup.AddThemeColorOverride("font_color", StsColors.cream);
        popup.AddThemeColorOverride("font_hover_color", StsColors.gold);
        popup.AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = SettingsDropdownFocused });

        var wrapper = new Control { CustomMinimumSize = new Vector2(320f, 64f) };
        wrapper.AddChild(optionButton);

        // Overlay label - same font/size as optionButton's own (now-invisible) text, plus the shadow treatment
        // native's settings_dropdown.tscn Label uses (font_shadow_color=(0,0,0,0.251), offset (3,2)) that
        // OptionButton itself has no theme slot for at all.
        var valueLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        valueLabel.SetAnchorsPreset(LayoutPreset.FullRect);
        valueLabel.AddThemeColorOverride("font_color", StsColors.gold);
        valueLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.25098f));
        valueLabel.AddThemeConstantOverride("shadow_offset_x", 3);
        valueLabel.AddThemeConstantOverride("shadow_offset_y", 2);
        valueLabel.AddThemeFontSizeOverride("font_size", 28);
        if (boldFont != null)
        {
            valueLabel.AddThemeFontOverride("font", boldFont);
        }
        wrapper.AddChild(valueLabel);

        void SyncValueLabel()
        {
            valueLabel.Text = optionButton.Selected >= 0 ? optionButton.GetItemText(optionButton.Selected) : string.Empty;
        }
        SyncValueLabel();
        optionButton.ItemSelected += _ => SyncValueLabel();
        var syncTimer = new Godot.Timer { WaitTime = 0.1, Autostart = true };
        syncTimer.Timeout += SyncValueLabel;
        wrapper.AddChild(syncTimer);

        Texture2D? arrowIcon = BuildDropdownArrowIcon();
        if (arrowIcon != null)
        {
            var arrow = new TextureRect
            {
                Texture = arrowIcon,
                MouseFilter = MouseFilterEnum.Ignore,
                StretchMode = TextureRect.StretchModeEnum.Scale,
            };
            // Matches settings_dropdown.tscn's own Arrow node: anchored to the vertical center of the right edge,
            // 26x26, sitting 13px in from the right edge.
            arrow.SetAnchorsPreset(LayoutPreset.CenterRight);
            arrow.OffsetLeft = -39f;
            arrow.OffsetRight = -13f;
            arrow.OffsetTop = -13f;
            arrow.OffsetBottom = 13f;
            wrapper.AddChild(arrow);
        }

        return wrapper;
    }

    /// <summary>Cream text + drop shadow (NOT outline) - the same color/shadow treatment settings_screen.tscn's own
    /// row labels use (themes/settings_screen_line_header.tres: default_color=(1,0.9647,0.8863),
    /// font_shadow_color=(0,0,0,0.502), shadow offset (3,2)). Row titles ("Language", "Screenshake", etc in the
    /// real settings screen) use this - distinct from StyleBodyLabel's outline treatment, which is for paragraph
    /// copy sitting directly on the popup's painted background rather than inside a themed settings row.
    /// No font_size override here - BuildSettingsRow always applies its own right after this returns (see there
    /// for why: the native row label isn't actually a static 17px, confirmed via reference/addons/mega_text/
    /// MegaRichTextLabel.cs).
    /// Font is REGULAR, not bold, despite this being the more "prominent" row-title text - the native row label is
    /// actually a RichTextLabel with theme_override_fonts/normal_font = kreon_regular_shared.tres AND bold_font =
    /// kreon_bold_shared.tres both set, but its text ("Language" etc.) carries no [b] bbcode tag, so it renders in
    /// normal_font. An earlier version of this method loaded the bold font unconditionally, which is why our row
    /// titles looked visibly heavier/different than native's side by side - this was a genuine bug, not a
    /// stylistic choice, caught by directly comparing screenshots of the two side by side.</summary>
    internal static Label StyleSettingsRowLabel(Label label)
    {
        label.AddThemeColorOverride("font_color", StsColors.cream);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.501961f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        Font? regularFont = ResolveFont(BodyFontPath, FontType.Regular);
        if (regularFont != null)
        {
            label.AddThemeFontOverride("font", regularFont);
        }
        return label;
    }

    /// <summary>
    /// One row of the "title on the left, control docked to the right" layout every line of the vanilla settings
    /// screen uses (settings_screen.tscn: MarginContainer, custom_minimum_size (0,64), 12px L/R margin - both now
    /// used as-is, not scaled down, now that the surrounding content width is also the real 1012px rather than a
    /// guessed popup width). Native achieves the left/right split by overlapping a full-width label with a
    /// right-shrunk control inside a bare MarginContainer; an HBoxContainer with the label set to ExpandFill and
    /// the control left at its own fixed size gets the identical visual result more simply.
    /// </summary>
    /// <summary>fontSize defaults to 28 - NOT settings_screen.tscn's literal "17" written on its own row label
    /// node. That node is a MegaRichTextLabel (MinFontSize=18/MaxFontSize=28), which overrides whatever static
    /// font_size the .tscn wrote the moment _Ready() runs (see reference/addons/mega_text/MegaRichTextLabel.cs -
    /// AdjustFontSize binary-searches the largest size up to MaxFontSize that still fits the label's rect, then
    /// calls AddThemeFontSizeOverride with THAT, not 17). Every native row title ("Language", "Upload Gameplay
    /// Data", etc) is short enough to fit at the full 28px within the row's actual available width, so that's what
    /// actually renders - the "17" only ever briefly exists as a pre-adjustment placeholder value. Our own row
    /// titles are comparably short Korean phrases at the same row width, so 28 (not a full binary-search
    /// reimplementation) is the correct match here too.</summary>
    internal static Control BuildSettingsRow(string title, Control control, int fontSize = 28)
    {
        var margin = new MarginContainer { CustomMinimumSize = new Vector2(0f, 64f) };
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        var row = new HBoxContainer();
        margin.AddChild(row);

        var label = StyleSettingsRowLabel(new Label { Text = title, VerticalAlignment = VerticalAlignment.Center });
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.SetHSizeFlags(SizeFlags.ExpandFill);
        row.AddChild(label);
        row.AddChild(control);
        return margin;
    }

    /// <summary>Thin divider between settings rows - settings_screen.tscn puts one of these between every single
    /// line (color/alpha copied exactly: cream at 25% opacity, 2px tall).</summary>
    internal static Control BuildSettingsDivider()
    {
        return new ColorRect
        {
            CustomMinimumSize = new Vector2(0f, 2f),
            Color = new Color(0.909804f, 0.862745f, 0.745098f, 0.25098f),
        };
    }
}
