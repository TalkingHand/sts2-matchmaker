using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2Matchmaker;

/// <summary>
/// Explicit mod entry point. Without this, the mod loader falls back to a single bare
/// <c>Harmony.PatchAll(assembly)</c> call (see ModManager's mod-loading code) - and PatchAll processes patch
/// classes one at a time with no per-class error isolation, so if MegaCrit renames/removes a single method we hook
/// (e.g. in a future update), the whole assembly-wide PatchAll throws on that one class and every other patch in
/// this mod silently fails to apply along with it, including unrelated features and the "매칭" menu button itself.
///
/// Here we replicate PatchAll's own type discovery (all types in this assembly carrying a HarmonyAttribute-derived
/// class attribute, i.e. every [HarmonyPatch(...)] patch class under Patches/) but patch each type in its own
/// try/catch, so a single broken patch only disables the one feature it belongs to - everything else keeps working.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    private static void Initialize()
    {
        var harmony = new Harmony("remesto.sts2_matchmaker");
        Type[] patchTypes = GetLoadableTypes(Assembly.GetExecutingAssembly())
            .Where(type => type.GetCustomAttributes(typeof(HarmonyAttribute), inherit: true).Length > 0)
            .ToArray();

        foreach (Type type in patchTypes)
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                Log.Error($"[sts2_matchmaker] Failed to apply patch class {type.Name} - the feature it belongs to will be disabled, but the rest of the mod keeps working:\n{ex}");
            }
        }
    }

    /// <summary>
    /// Assembly.GetTypes() throws ReflectionTypeLoadException - for the whole assembly, before returning anything -
    /// the moment a single type in it fails to load (e.g. a patch class whose Prefix/Postfix signature references a
    /// game type/member MegaCrit renamed or removed). Left uncaught, that one broken class would take down every
    /// other patch along with it, defeating the per-class isolation this file exists for. Recover by returning
    /// whichever types DID load successfully (ex.Types contains null entries for the failures) and logging the rest.
    /// </summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (Exception? loaderException in ex.LoaderExceptions)
            {
                Log.Error($"[sts2_matchmaker] A type failed to load, its patch class (if any) will be disabled, but the rest of the mod keeps working:\n{loaderException}");
            }
            return ex.Types.Where(type => type != null)!;
        }
    }
}
