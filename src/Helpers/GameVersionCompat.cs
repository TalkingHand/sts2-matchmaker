using System;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer;

namespace Sts2Matchmaker.Helpers;

/// <summary>
/// Wraps two spots where our code depends on PeerVersionInfo (MegaCrit.Sts2.Core.Multiplayer), a peer-handshake
/// struct that public-beta has but general (as of the version this was checked, v0.107.1) doesn't - general
/// predates any version-string API entirely, not just this struct, so there's no equivalent value to substitute
/// on that branch. NetHostGameService's constructor is a hard split too: beta only has the 1-arg
/// (PeerVersionInfo) overload, general only has the 0-arg one - so PeerVersionInfo can never appear in this
/// class's own metadata (a plain reference would throw ReflectionTypeLoadException on whichever branch lacks
/// it); both members are resolved through Type.GetType at runtime instead.
/// </summary>
public static class GameVersionCompat
{
    private static readonly Type? PeerVersionInfoType = typeof(NetHostGameService).Assembly.GetType("MegaCrit.Sts2.Core.Multiplayer.PeerVersionInfo");

    /// <summary>
    /// Constructs a NetHostGameService via whichever constructor the running game branch actually exposes.
    /// </summary>
    public static NetHostGameService CreateNetHostGameService()
    {
        // Not "new NetHostGameService(...)" for either branch: beta only has the 1-arg overload, general only has
        // the 0-arg one, so writing either call directly would fail to compile against whichever branch's dll
        // this assembly happens to be built against. Both go through reflection instead.
        ConstructorInfo? versionedCtor = PeerVersionInfoType == null
            ? null
            : typeof(NetHostGameService).GetConstructor(new[] { PeerVersionInfoType });
        if (versionedCtor != null)
        {
            object versionInfo = PeerVersionInfoType!.GetMethod("LocalDefault", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
            return (NetHostGameService)versionedCtor.Invoke(new[] { versionInfo });
        }
        ConstructorInfo parameterlessCtor = typeof(NetHostGameService).GetConstructor(Type.EmptyTypes)!;
        return (NetHostGameService)parameterlessCtor.Invoke(null);
    }

    /// <summary>
    /// This client's game version string (PeerVersionInfo.version), or a fixed placeholder on branches that
    /// don't have PeerVersionInfo at all. The placeholder can never collide with a real version string, so
    /// players on that branch simply match each other (this tag's whole purpose - see MatchTags.CurrentVersionTag)
    /// while still never cross-matching a branch that reports an actual version.
    /// </summary>
    public static string GetCurrentVersionTag()
    {
        if (PeerVersionInfoType == null)
        {
            return "unversioned";
        }
        object versionInfo = PeerVersionInfoType.GetMethod("LocalDefault", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        return (string)PeerVersionInfoType.GetField("version")!.GetValue(versionInfo)!;
    }
}
