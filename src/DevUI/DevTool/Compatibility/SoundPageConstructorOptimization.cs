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
/// Removes synchronous ambient-directory discovery and hidden file-button materialization from the
/// vanilla SoundPage constructor while rebuilt presentation owns the workspace. One constructor IL
/// boundary replaces both jobs; no separate RefreshFilesPage On-hook is required anymore.
///
/// Explicit Vanilla/Legacy construction bypasses the optimization and executes the complete original
/// behaviour. Existing SoundPage instances can also materialize their file buttons later through the
/// compatibility helper when the user exposes legacy presentation.
/// </summary>
internal static class SoundPageConstructorOptimization
{
    private static bool enabled;
    private static bool constructorPatchInstalled;
    private static int legacyConstructionBypassDepth;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        try
        {
            IL.DevInterface.SoundPage.ctor += PatchConstructor;
            constructorPatchInstalled = true;
        }
        catch (Exception error)
        {
            if (constructorPatchInstalled)
            {
                try { IL.DevInterface.SoundPage.ctor -= PatchConstructor; }
                catch { }
                constructorPatchInstalled = false;
            }
            enabled = false;
            Plugin.Logger?.LogWarning("DevTool SoundPage constructor optimization unavailable: " + error);
        }
    }

    internal static void Disable()
    {
        enabled = false;

        if (constructorPatchInstalled)
        {
            try { IL.DevInterface.SoundPage.ctor -= PatchConstructor; }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool SoundPage constructor optimization could not be removed cleanly: " + error.Message);
            }
            constructorPatchInstalled = false;
        }

        legacyConstructionBypassDepth = 0;
    }

    private static void PatchConstructor(ILContext il)
    {
        bool replacedLoadedFiles = false;
        bool replacedAssetList = false;
        bool replacedToArray = false;
        bool replacedInitialFileButtons = false;
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

        cursor.Index = 0;
        if (cursor.TryGotoNext(MoveType.Before, IsRefreshFilesPageCall))
        {
            // The call already has `this` on the evaluation stack. Replace it with one conditional
            // delegate so native construction skips button allocation while explicit legacy
            // construction still executes the ordinary method without another runtime hook.
            cursor.Remove();
            cursor.EmitDelegate<Action<DevInterface.SoundPage>>(RefreshFilesPageDuringConstruction);
            replacedInitialFileButtons = true;
        }

        if (!replacedLoadedFiles || !replacedAssetList || !replacedToArray || !replacedInitialFileButtons)
        {
            throw new InvalidOperationException(
                "DevTool: SoundPage constructor layout changed; ambient/file-button optimization " +
                "refused to run against an unknown method body.");
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

    private static bool IsRefreshFilesPageCall(Instruction instruction)
    {
        if (instruction == null ||
            (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) ||
            instruction.Operand is not MethodReference method)
            return false;

        return method.DeclaringType?.FullName == typeof(DevInterface.SoundPage).FullName &&
               method.Name == nameof(DevInterface.SoundPage.RefreshFilesPage) &&
               method.Parameters.Count == 0;
    }

    private static FileInfo[] GetLoadedAmbientFiles(DirectoryInfo directory)
    {
        if (!UseOptimizedConstructor())
        {
            try { return directory?.GetFiles() ?? Array.Empty<FileInfo>(); }
            catch { return Array.Empty<FileInfo>(); }
        }

        // The complete filename generation belongs to the headless native catalogue. Keep the
        // constructor's first source empty and let the patched ToArray stage receive only the small
        // bootstrap view required by compatibility state.
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

    private static void RefreshFilesPageDuringConstruction(DevInterface.SoundPage page)
    {
        if (page == null || UseOptimizedConstructor())
            return;

        page.RefreshFilesPage();
    }

    internal readonly struct LegacyConstructionScope : IDisposable
    {
        internal LegacyConstructionScope(bool enter)
        {
            if (enter) legacyConstructionBypassDepth++;
        }

        public void Dispose()
        {
            if (legacyConstructionBypassDepth > 0)
                legacyConstructionBypassDepth--;
        }
    }

    internal static LegacyConstructionScope EnterLegacyConstruction() => new(true);

    internal static void MaterializeLegacyFileButtons(DevInterface.SoundPage page)
    {
        if (page == null) return;
        page.RefreshFilesPage();
    }

    private static bool UseOptimizedConstructor()
    {
        EditorSession session = DevToolRuntime.ActiveSession;
        return enabled &&
               legacyConstructionBypassDepth == 0 &&
               EditorInputRouter.FrontendAttached &&
               !EditorUiModeState.UseVanilla &&
               session != null &&
               session.LegacyUiVisible == false;
    }
}
