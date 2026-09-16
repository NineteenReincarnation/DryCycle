using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using DryCycle.DevUI.DevTool.Sound;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Removes the synchronous ambient-directory walk from the vanilla SoundPage constructor only while
/// the rebuilt frontend owns presentation. The rest of SoundPage construction is untouched, which
/// preserves world handles, third-party page assumptions, and vanilla fallback behaviour.
/// </summary>
internal static class SoundPageConstructorOptimization
{
    private static bool enabled;
    private static bool patchInstalled;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        try
        {
            IL.DevInterface.SoundPage.ctor += PatchConstructor;
            patchInstalled = true;
        }
        catch (Exception error)
        {
            patchInstalled = false;
            Plugin.Logger?.LogWarning("DevTool SoundPage constructor optimization unavailable: " + error);
        }
    }

    internal static void Disable()
    {
        enabled = false;
        if (!patchInstalled) return;
        try
        {
            IL.DevInterface.SoundPage.ctor -= PatchConstructor;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool SoundPage constructor optimization could not be removed cleanly: " + error.Message);
        }
        patchInstalled = false;
    }

    private static void PatchConstructor(ILContext il)
    {
        bool replacedLoadedFiles = false;
        bool replacedAssetList = false;
        bool replacedToArray = false;
        ILCursor cursor = new(il);

        if (cursor.TryGotoNext(MoveType.Before, IsDirectoryInfoGetFiles))
        {
            cursor.Remove();
            cursor.EmitDelegate<Func<DirectoryInfo, FileInfo[]>>(GetLoadedAmbientFiles);
            replacedLoadedFiles = true;
        }

        cursor.Index = 0;
        if (cursor.TryGotoNext(MoveType.Before, IsAssetManagerListDirectory))
        {
            cursor.Remove();
            cursor.EmitDelegate<Func<string, bool, bool, bool, string[]>>(ListAmbientDirectory);
            replacedAssetList = true;
        }

        cursor.Index = 0;
        if (cursor.TryGotoNext(MoveType.Before, IsStringListToArray))
        {
            cursor.Remove();
            cursor.EmitDelegate<Func<System.Collections.Generic.List<string>, string[]>>(FinalizeFileNames);
            replacedToArray = true;
        }

        if (!replacedLoadedFiles || !replacedAssetList || !replacedToArray)
        {
            throw new InvalidOperationException(
                "DevTool: SoundPage constructor layout changed; ambient enumeration patch refused " +
                "to run against an unknown method body.");
        }
    }

    private static bool IsDirectoryInfoGetFiles(Instruction instruction)
    {
        if (instruction?.OpCode != OpCodes.Callvirt || instruction.Operand is not MethodReference method)
            return false;
        return method.DeclaringType?.FullName == typeof(DirectoryInfo).FullName &&
               method.Name == nameof(DirectoryInfo.GetFiles) &&
               method.Parameters.Count == 0;
    }

    private static bool IsAssetManagerListDirectory(Instruction instruction)
    {
        if (instruction?.OpCode != OpCodes.Call || instruction.Operand is not MethodReference method)
            return false;
        return method.DeclaringType?.FullName == typeof(AssetManager).FullName &&
               method.Name == nameof(AssetManager.ListDirectory) &&
               method.Parameters.Count == 4;
    }

    private static bool IsStringListToArray(Instruction instruction)
    {
        if (instruction?.OpCode != OpCodes.Callvirt || instruction.Operand is not MethodReference method)
            return false;
        return method.DeclaringType?.FullName == "System.Collections.Generic.List`1<System.String>" &&
               method.Name == "ToArray" && method.Parameters.Count == 0;
    }

    private static FileInfo[] GetLoadedAmbientFiles(DirectoryInfo directory)
    {
        if (!UseOptimizedConstructor())
        {
            try { return directory?.GetFiles() ?? Array.Empty<FileInfo>(); }
            catch { return Array.Empty<FileInfo>(); }
        }

        SoundFileNameCatalog.EnsureStarted();
        return Array.Empty<FileInfo>();
    }

    private static string[] ListAmbientDirectory(
        string path,
        bool directories,
        bool includeAll,
        bool moddedOnly)
    {
        if (!UseOptimizedConstructor() ||
            directories || includeAll || moddedOnly ||
            !string.Equals(path, "soundeffects/ambient", StringComparison.OrdinalIgnoreCase))
        {
            return AssetManager.ListDirectory(path, directories, includeAll, moddedOnly) ?? Array.Empty<string>();
        }

        SoundFileNameCatalog.EnsureStarted();
        return Array.Empty<string>();
    }

    private static string[] FinalizeFileNames(System.Collections.Generic.List<string> vanillaNames)
    {
        if (!UseOptimizedConstructor())
            return vanillaNames?.ToArray() ?? Array.Empty<string>();

        return SoundFileNameCatalog.ConstructorNamesOrEmpty();
    }

    private static bool UseOptimizedConstructor() =>
        enabled && EditorInputRouter.FrontendAttached && !EditorUiModeState.UseVanilla;
}
