using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace Sts2Matchmaker.UI;

/// <summary>
/// The real native settings dropdown (res://scenes/screens/settings_dropdown.tscn - the exact scene
/// NLanguageDropdown/NResolutionDropdown/NDisplayDropdown/NAspectRatioDropdown all instance, confirmed via
/// reference/screens/settings_screen.tscn's own ext_resource table), for a plain list of arbitrary string options
/// rather than one of the game's own domain-specific dropdowns.
///
/// The base scene's root has NO script of its own - every concrete dropdown attaches its OWN script (e.g.
/// NLanguageDropdown.cs) directly at its embedding site inside settings_screen.tscn, not baked into the shared
/// scene. We can't do the equivalent (there's no res:// path Godot could resolve to a class living in our own mod
/// DLL, unlike the game's own scripts), so instead of subclassing NDropdown, this hand-wires the same open/close/
/// select behavior NDropdown.cs itself implements (see reference/GodotExtensions/NDropdown.cs) directly in plain
/// C#, on top of the instantiated scene's real children - which already carry their own native scripts
/// (NDropdownContainer, NDropdownScrollbar, NButton for the Dismisser) at THAT level within the .tscn, so
/// scrolling/hover/drag physics all work exactly as in the real settings screen. Only the outer click-to-open/
/// close and item population/selection below is ours.
///
/// Each option reuses res://scenes/ui/language_dropdown_item.tscn (NLanguageDropdownItem) as a generic
/// NDropdownItem instead of building a bare item by hand, sidestepping not knowing whether
/// NButton._Ready()/ConnectSignals() requires supporting child nodes we'd have to reproduce by hand (e.g. a hotkey
/// icon) - the real scene already has whatever it needs, since it's what the actual language dropdown uses. We
/// never call NLanguageDropdownItem's own Init(languageCode) (language-specific, not applicable here). Text is set
/// via the item's own "RichLabel" child (a plain RichTextLabel) directly, NOT via the base NDropdownItem.Text
/// property - that property reads/writes a "Label" child (protected field _label, a MegaLabel) which this specific
/// item scene doesn't have (confirmed via its .tscn: only "Highlight" and "RichLabel", no "Label"), so _label is
/// permanently null here and Text would throw. NLanguageDropdownItem.Init() itself confirms this - it sets text via
/// _richLabel.SetTextAutoSize(...), never through the inherited Text property.
/// </summary>
internal static class Sts2NativeDropdown
{
    private const string DropdownScenePath = "res://scenes/screens/settings_dropdown.tscn";
    private const string ItemScenePath = "res://scenes/ui/language_dropdown_item.tscn";

    /// <summary>Closes whichever dropdown built by TryBuild is currently open, if any - shared across every
    /// instance so opening one always closes any other still-open one first (see SetOpen's own doc).</summary>
    private static Action? s_openDropdownCloser;

    /// <summary>Every root Control built by TryBuild that's still alive, so opening one can block mouse input on
    /// all the others for as long as it's open (see SetOpen's own doc for why z_index elevation alone isn't
    /// trusted to be enough here). Entries remove themselves on TreeExiting.</summary>
    private static readonly List<Control> s_allDropdownRoots = new();

    /// <summary>
    /// Live controls for a dropdown built by TryBuild - callers that need to change the selection or enabled state
    /// after construction (e.g. MatchConditionsPanel.LoadFrom restoring a saved value, or disabling "인원 수" while
    /// "런 종류" is Daily) use this instead of poking at Control directly, since the actual selection state lives in
    /// TryBuild's own closures, not on any Godot node property a caller could reach from outside.
    /// </summary>
    public sealed class Handle
    {
        public Control Control { get; }
        private readonly Action<string> _setValue;
        private readonly Action<bool> _setDisabled;

        internal Handle(Control control, Action<string> setValue, Action<bool> setDisabled)
        {
            Control = control;
            _setValue = setValue;
            _setDisabled = setDisabled;
        }

        /// <summary>Updates the displayed value only - does NOT invoke the onSelected callback TryBuild was given,
        /// since callers of this (e.g. LoadFrom) already know the value they're setting and are usually about to
        /// assign it to their own backing field directly; re-invoking onSelected would just be a redundant echo.</summary>
        public void SetValue(string value) => _setValue(value);

        /// <summary>Dims the control and blocks opening it while true (closing it first if it's currently open) -
        /// there's no native "disabled" state on this hand-wired scene to fall back on, unlike Godot's own
        /// Button.Disabled.</summary>
        public bool Disabled
        {
            set => _setDisabled(value);
        }
    }

    /// <summary>
    /// Builds the dropdown and returns a Handle wrapping its root Control (320x64, matching the native scene's own
    /// custom_minimum_size) to add into your own layout. onSelected fires once immediately with initialValue (or
    /// options[0] if initialValue isn't in the list) to let the caller sync its own state, then again every time
    /// the user picks a different option. Returns null if the native scene fails to load, so callers can fall back
    /// to Sts2ModalPanel.StyleAsSettingsDropdown instead.
    ///
    /// overlayParent: a Control that is NOT itself inside a ScrollContainer (e.g. the whole Sts2ModalPanel - "this"
    /// - from a caller using ShowAsModal/ShowAsTabbedSettingsScreen, both of which wrap their actual row content in
    /// a real ScrollContainer). ScrollContainer always clips to its own viewport rect regardless of z_index or
    /// mouse_filter - a long open list (e.g. ascension's up to 12 options, positioned as one of the LAST rows on
    /// the page) can extend well past that clipped viewport, silently cutting off every option beyond whatever
    /// still fits, with no way to reach the rest even though NDropdownContainer's own scrolling works fine (it's
    /// simply never given the chance to be needed - the clip happens first, further out). When given, the open
    /// DropdownContainer is reparented out to overlayParent (once, on first open) and repositioned via
    /// GlobalPosition to sit exactly where it would have anyway, escaping that clip entirely. Omit only for a
    /// caller confirmed to never need more vertical room than its own popup already has - safe to leave unset,
    /// since the whole dropdown still works identically, just clippable again if it ever grows a long enough list.
    /// </summary>
    public static Handle? TryBuild(IReadOnlyList<string> options, string initialValue, Action<string> onSelected, Control? overlayParent = null)
    {
        try
        {
            PackedScene? dropdownScene = PreloadManager.Cache.GetScene(DropdownScenePath);
            if (dropdownScene == null)
            {
                throw new InvalidOperationException($"GetScene returned null for {DropdownScenePath}");
            }
            PackedScene? itemScene = PreloadManager.Cache.GetScene(ItemScenePath);
            if (itemScene == null)
            {
                throw new InvalidOperationException($"GetScene returned null for {ItemScenePath}");
            }

            Control root = dropdownScene.Instantiate<Control>(PackedScene.GenEditState.Disabled);
            s_allDropdownRoots.Add(root);
            root.TreeExiting += () => s_allDropdownRoots.Remove(root);
            var dropdownContainer = root.GetNode<NDropdownContainer>("%DropdownContainer");
            // Left at the native .tscn's own 600px (offset_top=64/offset_bottom=664) - an earlier pass shrank this
            // to force even moderately long lists (e.g. ascension's up to 12 options) into NDropdownContainer's own
            // scroll path, but NDropdownScrollbar's train visuals are tuned for the native 600px track and read as
            // disconnected from the actual scroll position once that track is compressed. Reverted; overflow past
            // the actual screen edge is handled entirely by RepositionDropdownContainer's clamp below instead.
            VBoxContainer items = dropdownContainer.GetNode<VBoxContainer>("VBoxContainer");
            var currentLabel = root.GetNode<MegaLabel>("%Label");
            var dismisser = root.GetNode<NButton>("%Dismisser");

            bool isOpen = false;
            bool isDisabled = false;
            // Tracked separately from currentLabel's own displayed text so the deferred block below (which re-syncs
            // the label on the next idle frame) always re-applies whatever the MOST RECENT desired value is, not
            // whatever initialValue happened to resolve to when TryBuild was first called. Without this, a caller
            // that calls Handle.SetValue synchronously right after TryBuild returns (e.g. LoadFrom restoring a
            // saved value, in the same frame as Build()) would see its own update silently overwritten a moment
            // later when the still-pending deferred callback fires and reapplies the stale startValue.
            string currentValue = options.Contains(initialValue) ? initialValue : (options.Count > 0 ? options[0] : string.Empty);

            // Callers like MatchConditionsPanel now put several of these dropdowns on one screen at once (language/
            // game mode/player count/ascension) - without this, opening a second one while a first is still open
            // would show two overlapping DropdownContainer popovers (both z_index 100, same overlap class the
            // per-row z_index fix above was for), since nothing otherwise closes the first when a second opens.
            // Shared across every dropdown built by TryBuild (static, not per-instance) - only one can meaningfully
            // be open at a time regardless of which screen built them.
            Action closeCurrentlyOpen = null!;
            bool reparented = false;

            void ReparentDropdownContainerIfNeeded()
            {
                if (overlayParent == null || reparented)
                {
                    return;
                }
                reparented = true;
                Node originalParent = dropdownContainer.GetParent();
                Vector2 size = dropdownContainer.Size;
                originalParent.RemoveChild(dropdownContainer);
                overlayParent.AddChild(dropdownContainer);
                // Was anchored relative to originalParent (Container, inside root's own 320-wide box) - those
                // fractions mean nothing under a differently-sized overlayParent, so this switches to an explicit
                // top-left position/size instead, computed fresh below (and again on every subsequent open, since
                // root's own on-screen position can move between opens - e.g. the page being scrolled).
                dropdownContainer.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                dropdownContainer.Size = size;
                // No longer a descendant of root, so it no longer inherits root's z_index (root.ZIndex = 100 below
                // only affects root's OWN remaining subtree now) - needs its own, high enough to draw above
                // overlayParent's other children (tab strip, scroll content, close button, etc, all left at the
                // default 0).
                dropdownContainer.ZIndex = 100;
            }

            void RepositionDropdownContainer()
            {
                if (overlayParent == null)
                {
                    return;
                }
                // Escaping the ancestor ScrollContainer's clip (see ReparentDropdownContainerIfNeeded) traded one
                // clipping problem for another: nothing was keeping the list within the actual VIEWPORT either, so
                // a row sitting near the bottom of a tall page (e.g. 승천 단계, the last row) could still open a
                // list that runs off the bottom of the monitor - same end result (unreachable bottom options), just
                // clipped by the screen edge now instead of the ScrollContainer. Clamp so the list's bottom edge
                // never passes the viewport's own bottom edge (and its top never passes above the viewport's own
                // top, for a row near the very top instead) - reference/Settings/NDropdownPositioner.cs (the game's
                // own equivalent positioning helper) doesn't do any such clamping itself, presumably because the
                // real settings screen never has a dropdown positioned close enough to the bottom for this to come
                // up; nothing else in the native scenes does this job for us.
                Vector2 viewportSize = root.GetViewportRect().Size;
                float desiredTop = root.GlobalPosition.Y + root.Size.Y;
                float maxTop = viewportSize.Y - dropdownContainer.Size.Y;
                float clampedTop = Mathf.Clamp(desiredTop, 0f, Mathf.Max(0f, maxTop));
                float maxLeft = viewportSize.X - dropdownContainer.Size.X;
                float clampedLeft = Mathf.Clamp(root.GlobalPosition.X, 0f, Mathf.Max(0f, maxLeft));
                dropdownContainer.GlobalPosition = new Vector2(clampedLeft, clampedTop);
            }

            void SetOpen(bool open)
            {
                if (isDisabled && open)
                {
                    return;
                }
                if (open == isOpen)
                {
                    // No actual state change (e.g. SetDisabled(true) defensively calling SetOpen(false) on a
                    // dropdown that was never open, or a redundant call) - must NOT touch the shared mutual-
                    // exclusion/mouse-filter state below in this case, since doing so would incorrectly reset
                    // whichever OTHER dropdown might legitimately be open right now (its MouseFilter/z_index/
                    // s_openDropdownCloser claim would get stomped by a close call that was never really "closing"
                    // anything).
                    return;
                }
                if (open)
                {
                    if (s_openDropdownCloser != closeCurrentlyOpen)
                    {
                        s_openDropdownCloser?.Invoke();
                    }
                    s_openDropdownCloser = closeCurrentlyOpen;
                }
                else if (s_openDropdownCloser == closeCurrentlyOpen)
                {
                    s_openDropdownCloser = null;
                }
                isOpen = open;
                if (open)
                {
                    // Re-measures scrollbar need/bounds against the VBoxContainer's ACTUAL current child sizes -
                    // not redundant with the deferred RefreshLayout() call below despite looking like it: THAT one
                    // runs once, shortly after construction, and can race the container's own live-tree layout pass
                    // (its result depends on each child's real Size.Y, which isn't trustworthy until Godot has
                    // actually laid out the VBoxContainer - a live-tree timing dependency, same class of bug as the
                    // _label/_Ready() issues elsewhere in this file). By the time a user actually opens this
                    // (a real click, frames after construction), that layout is guaranteed to have long settled,
                    // so this call is the one that actually determines correct behavior in practice - without it,
                    // a dropdown whose deferred call happened to race the layout would show no scrollbar and
                    // ignore drag/wheel input (NDropdownContainer.UpdateScrollPosition no-ops entirely whenever
                    // its own _scrollbar.Visible is false), every time it's opened, not just the first. Runs BEFORE
                    // reparenting so ReparentDropdownContainerIfNeeded captures the real, settled Size (not
                    // whatever it happened to be at construction time).
                    dropdownContainer.RefreshLayout();
                    ReparentDropdownContainerIfNeeded();
                    RepositionDropdownContainer();
                }
                dropdownContainer.Visible = open;
                dismisser.Visible = open;
                // Belt-and-suspenders alongside the z_index elevation below: when several of these dropdowns sit
                // as sibling rows (MatchConditionsPanel), an OPEN one's DropdownContainer visually extends well
                // past its own row's bounds, over whatever rows happen to be below it - those rows' OWN root
                // Controls are separate, non-overlapping-when-closed rects that just happen to now occupy the same
                // screen area. z_index SHOULD already resolve hit-testing correctly in Godot's favor of the
                // higher-z control, but rather than trust that alone, every OTHER live dropdown's root is set to
                // MOUSE_FILTER_IGNORE while this one is open - a control with that filter is unconditionally
                // excluded from mouse hit-testing, removing any doubt about which one "wins" a click in the
                // overlapping region. Restored to the default MOUSE_FILTER_STOP once nothing is open. Safe from the
                // "some unrelated dropdown resets everyone" hazard the early-return above guards against, since we
                // only ever reach here on a REAL open<->closed transition for THIS dropdown, and mutual exclusion
                // above guarantees at most one dropdown is ever actually open at a time.
                foreach (Control other in s_allDropdownRoots)
                {
                    if (other != root && GodotObject.IsInstanceValid(other))
                    {
                        other.MouseFilter = open ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
                    }
                }
                // Elevated ONLY while open, not permanently - a caller (e.g. MatchConditionsPanel) may place several
                // of these dropdowns as sibling rows on one screen, and Godot breaks z_index ties by tree order
                // (later siblings paint over earlier ones). A permanently-high z_index on every row ties all of them
                // together, so a LATER row's own closed 320x64 box (still drawn - closed only hides its
                // DropdownContainer child, not the row itself) would win that tie and paint over an EARLIER row's
                // open popover extending down past its own bounds. Bumping z_index only for the one actually open
                // (mutual exclusion above already guarantees at most one at a time) keeps everything else at the
                // neutral default, so there's never a tie to break in the first place.
                root.ZIndex = open ? 100 : 0;
            }

            closeCurrentlyOpen = () => SetOpen(false);

            void SelectValue(string value)
            {
                currentValue = value;
                currentLabel.SetTextAutoSize(value);
                SetOpen(false);
                onSelected(value);
            }

            void SetDisplayValue(string value)
            {
                currentValue = value;
                currentLabel.SetTextAutoSize(value);
            }

            void SetDisabled(bool disabled)
            {
                isDisabled = disabled;
                root.Modulate = disabled ? new Color(1f, 1f, 1f, 0.5f) : Colors.White;
                if (disabled)
                {
                    SetOpen(false);
                }
            }

            foreach (string option in options)
            {
                var item = itemScene.Instantiate<NDropdownItem>(PackedScene.GenEditState.Disabled);
                // RichTextLabel.Text is a plain engine-native property (see class doc for why we use this instead
                // of NDropdownItem.Text) - safe to set immediately, no dependency on _Ready() or scene-tree
                // liveness, unlike NDropdownItem.Text/_label would have been.
                item.GetNode<RichTextLabel>("RichLabel").Text = option;
                items.AddChild(item);
                item.Connect(NDropdownItem.SignalName.Selected, Callable.From<NDropdownItem>(_ => SelectValue(option)));
            }

            // No script on "root" (see class doc) means no NClickableControl.Released to connect to for the
            // click-to-open toggle NDropdown.OnRelease provides natively - GuiInput on the plain Control is the
            // ordinary Godot way to detect a left-click release ourselves instead.
            root.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                {
                    SetOpen(!isOpen);
                }
            };
            dismisser.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => SetOpen(false)));

            // dropdownContainer.RefreshLayout() reads _scrollbar/_dropdownItems, fields NDropdownContainer's own
            // _Ready() populates - which only runs once the WHOLE subtree (root and everything under it) actually
            // enters the live SceneTree, not yet at this point in TryBuild (the caller adds "root" to a live
            // parent only after this method returns). CallDeferred pushes this to the next idle frame, by which
            // point that AddChild will already have happened (synchronously, in the caller, right after TryBuild
            // returns), so _Ready() will have already run. Same technique RecruitToggleInjector's KeepLastDeferred
            // already uses for the same "wait until the tree has settled" reason. currentLabel.SetTextAutoSize
            // doesn't actually need this (see class doc - MegaLabel's own _Ready() isn't involved), but is kept
            // alongside RefreshLayout so the dropdown's displayed value and its item list settle in the same frame.
            Callable.From(() =>
            {
                // Own try/catch - this runs as a deferred callback, outside TryBuild's own call stack, so the
                // surrounding try/catch below (which only wraps TryBuild's synchronous body) can't see exceptions
                // thrown in here.
                try
                {
                    dropdownContainer.RefreshLayout();
                    currentLabel.SetTextAutoSize(currentValue);
                    onSelected(currentValue);
                }
                catch (Exception ex)
                {
                    Log.Error($"[sts2_matchmaker] Failed to populate native settings dropdown (deferred): {ex}");
                }
            }).CallDeferred();

            return new Handle(root, SetDisplayValue, SetDisabled);
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to build native settings dropdown: {ex}");
            return null;
        }
    }
}
