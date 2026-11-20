using System;
using System.Reflection;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using Sts2Matchmaker.Localization;
using Sts2Matchmaker.Matchmaking;

namespace Sts2Matchmaker.Helpers;

/// <summary>
/// Bridges the game's GameMode/custom-run representation, which differs between builds, to MatchMode.
///
/// Older builds (general, and public-beta before v0.113.0): GameMode = None/Standard/Daily/Custom, no custom flag.
/// v0.113.0+: GameMode = None/Standard/Daily/Dread/Blitz, and custom is StartRunLobby.IsCustom /
/// SerializableRun.IsCustomGame on top of any of those. Never compare against a GameMode constant directly (it's
/// baked in as a number at compile time and silently means a different mode on the other build) - the enum value's
/// NAME is stable per mode, so it's classified by ToString() here, and the custom flag is read by reflection since
/// older builds don't have it at all.
///
/// Blitz and Dread only exist on the new build, so their screen types, unlock epochs and Blitz's ascension progress
/// are likewise looked up by name - this class's metadata must stay loadable on a build without any of them.
/// </summary>
public static class GameModeCompat
{
    private static readonly Assembly GameAssembly = typeof(StartRunLobby).Assembly;

    private static readonly PropertyInfo? LobbyIsCustomProp = typeof(StartRunLobby).GetProperty("IsCustom");
    private static readonly PropertyInfo? RunIsCustomProp = typeof(SerializableRun).GetProperty("IsCustomGame");

    public static readonly Type? BlitzScreenType = GameAssembly.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Blitz.NBlitzScreen");
    private static readonly Type? BlitzEpochType = GameAssembly.GetType("MegaCrit.Sts2.Core.Timeline.Epochs.BlitzEpoch");

    public static readonly Type? DreadScreenType = GameAssembly.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Dread.NDreadScreen");
    private static readonly Type? DreadEpochType = GameAssembly.GetType("MegaCrit.Sts2.Core.Timeline.Epochs.DreadEpoch");

    public static MatchMode FromLobby(StartRunLobby lobby)
    {
        bool isCustom = LobbyIsCustomProp != null && (bool)LobbyIsCustomProp.GetValue(lobby)!;
        return Classify(lobby.GameMode.ToString(), isCustom);
    }

    public static MatchMode FromRun(SerializableRun run)
    {
        bool isCustom = RunIsCustomProp != null && (bool)RunIsCustomProp.GetValue(run)!;
        return Classify(run.GameMode.ToString(), isCustom);
    }

    private static MatchMode Classify(string gameModeName, bool isCustom)
    {
        if (isCustom)
        {
            return MatchMode.Custom;
        }
        return gameModeName switch
        {
            "Daily" => MatchMode.Daily,
            "Custom" => MatchMode.Custom, // older builds only
            "Blitz" => MatchMode.Blitz,
            "Dread" => MatchMode.Dread,
            _ => MatchMode.Standard,
        };
    }

    /// <summary>Whether the running build has Blitz at all AND this player can host/join it - same check vanilla's
    /// own NMultiplayerHostSubmenu uses to enable its 블리츠 button (SaveManager.IsEpochRevealed&lt;BlitzEpoch&gt;).
    /// A host's StartRunLobby also rejects a joining player without that epoch, so offering Blitz matching to
    /// someone who hasn't unlocked it would only produce failed joins.</summary>
    public static bool IsBlitzAvailable() => IsModeAvailable(BlitzScreenType, BlitzEpochType);

    /// <summary>Same as IsBlitzAvailable, for Dread - vanilla's NMultiplayerHostSubmenu gates its 드레드 button on
    /// SaveManager.IsEpochRevealed&lt;DreadEpoch&gt;, and StartRunLobby disconnects a joining player without that
    /// epoch (NetError.DreadNotUnlocked).</summary>
    public static bool IsDreadAvailable() => IsModeAvailable(DreadScreenType, DreadEpochType);

    private static bool IsModeAvailable(Type? screenType, Type? epochType)
    {
        if (screenType == null || epochType == null)
        {
            return false;
        }
        try
        {
            MethodInfo? isRevealed = Array.Find(typeof(SaveManager).GetMethods(BindingFlags.Public | BindingFlags.Instance),
                m => m.Name == "IsEpochRevealed" && m.IsGenericMethodDefinition);
            return isRevealed != null && (bool)isRevealed.MakeGenericMethod(epochType).Invoke(SaveManager.Instance, null)!;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Dropdown label for Dread - the game's own host-menu button title (main_menu_ui DREAD_MP.title, what
    /// NSubmenuButton.SetLocalization("DREAD_MP") shows) so the name matches vanilla in every language, falling back
    /// to our own "드레드" entry if that key is ever missing. BBCode tags are stripped since the dropdown is plain
    /// text.</summary>
    public static string DreadDisplayName()
    {
        try
        {
            string? title = LocString.GetIfExists("main_menu_ui", "DREAD_MP.title")?.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(title))
            {
                return Regex.Replace(title, @"\[[^\]]*\]", string.Empty).Trim();
            }
        }
        catch (Exception)
        {
            // fall through to our own string
        }
        return Loc.Get("드레드");
    }

    /// <summary>Highest ascension this player has unlocked for the given mode - Blitz tracks its own
    /// (Progress.MaxBlitzAscension, matching StartRunLobby's own per-mode MaxAscension rule), everything else uses
    /// the multiplayer one.</summary>
    public static int LocalMaxAscension(MatchMode mode)
    {
        // Dread has no ascension choice at all - every Dread run is played at a fixed A10 (NDreadScreen's own
        // const _ascension = 10; NAscensionPanel hides its arrows in Dread). Reporting 0 hides our ascension row
        // for it, and every Dread room is tagged with the same 0 (see MatchLobbyTagging.ApplyTags), so matching
        // never splits Dread players over a number the game ignores.
        if (mode == MatchMode.Dread)
        {
            return 0;
        }
        object progress = SaveManager.Instance.Progress;
        if (mode == MatchMode.Blitz)
        {
            PropertyInfo? blitzProp = progress.GetType().GetProperty("MaxBlitzAscension");
            return blitzProp == null ? 0 : (int)blitzProp.GetValue(progress)!;
        }
        return SaveManager.Instance.Progress.MaxMultiplayerAscension;
    }

    /// <summary>NBlitzScreen fetched/initialized/pushed the same way vanilla's NMultiplayerHostSubmenu.StartHostAsync
    /// does for its Blitz case, but by reflection (the type doesn't exist on older builds).</summary>
    public static void PushBlitzHostScreen(NSubmenuStack stack, object netService, int maxPlayers) =>
        PushHostScreen(BlitzScreenType, "Blitz", stack, netService, maxPlayers);

    /// <summary>Dread counterpart of PushBlitzHostScreen - NDreadScreen has the same
    /// InitializeMultiplayerAsHost(INetGameService, int) shape (vanilla's StartHostAsync Dread case).</summary>
    public static void PushDreadHostScreen(NSubmenuStack stack, object netService, int maxPlayers) =>
        PushHostScreen(DreadScreenType, "Dread", stack, netService, maxPlayers);

    private static void PushHostScreen(Type? screenType, string modeName, NSubmenuStack stack, object netService, int maxPlayers)
    {
        if (screenType == null)
        {
            throw new InvalidOperationException($"{modeName} is not available on this game build");
        }
        NSubmenu screen = stack.GetSubmenuType(screenType);
        MethodInfo init = screenType.GetMethod("InitializeMultiplayerAsHost")
            ?? throw new MissingMethodException(screenType.Name, "InitializeMultiplayerAsHost");
        init.Invoke(screen, new[] { netService, maxPlayers });
        stack.Push(screen);
    }
}
