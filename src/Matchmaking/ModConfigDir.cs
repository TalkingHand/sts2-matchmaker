using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Makes sure user://mod_configs/ exists before a store writes its .cfg file there. ConfigFile.Save does not create
/// missing parent folders - it just fails with ERR_FILE_CANT_OPEN - and the game itself never creates this folder;
/// it only exists when some other mod (e.g. BaseLib) happened to create it first. Without this, players with no such
/// mod installed lost every saved setting, ban and recent community on restart, because every save silently failed.
/// </summary>
public static class ModConfigDir
{
    private const string DirPath = "user://mod_configs";

    public static void Ensure()
    {
        Error err = DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(DirPath));
        if (err != Error.Ok && err != Error.AlreadyExists)
        {
            Log.Error($"[sts2_matchmaker] Failed to create {DirPath}: {err}");
        }
    }
}
