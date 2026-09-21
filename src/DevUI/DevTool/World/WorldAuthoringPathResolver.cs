using System;
using System.Collections.Generic;
using System.IO;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Resolves editable Rain World world-data files back to their real mod source instead of the
/// generated mergedmods cache returned first by AssetManager.ResolveFilePath.
///
/// Read resolution may still use AssetManager. Authoring must prefer the highest-priority active
/// mod source that actually owns the file so edits survive Remix merge-cache rebuilds.
/// </summary>
internal static class WorldAuthoringPathResolver
{
    internal static string ResolveExistingSource(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        string normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        try
        {
            for (int i = ModManager.ActiveMods.Count - 1; i >= 0; i--)
            {
                ModManager.Mod mod = ModManager.ActiveMods[i];
                if (mod == null) continue;

                if (mod.hasTargetedVersionFolder)
                {
                    string candidate = Path.Combine(mod.TargetedPath, normalized);
                    if (File.Exists(candidate)) return candidate;
                }

                if (mod.hasNewestFolder)
                {
                    string candidate = Path.Combine(mod.NewestPath, normalized);
                    if (File.Exists(candidate)) return candidate;
                }

                if (!string.IsNullOrWhiteSpace(mod.path))
                {
                    string candidate = Path.Combine(mod.path, normalized);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "World authoring source resolution failed for '" + relativePath + "': " + error.Message);
        }

        string resolved = AssetManager.ResolveFilePath(relativePath);
        if (string.IsNullOrWhiteSpace(resolved))
            return string.Empty;

        // Never return the generated merge cache as an authoring target. A file that exists only in
        // mergedmods may have been synthesized from modify/ patches or multiple mods and therefore
        // has no lossless single-source destination.
        if (IsMergedCachePath(resolved))
            return string.Empty;

        return resolved;
    }

    internal static bool TryResolveMapConfigSource(
        string regionName,
        string resolvedMapPath,
        out string sourcePath,
        out string error)
    {
        sourcePath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(regionName) || string.IsNullOrWhiteSpace(resolvedMapPath))
        {
            error = "Map config region or resolved path is unavailable.";
            return false;
        }

        string fileName = Path.GetFileName(resolvedMapPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            error = "Map config file name could not be resolved.";
            return false;
        }

        string relativePath =
            "World" + Path.DirectorySeparatorChar +
            regionName + Path.DirectorySeparatorChar +
            fileName;
        sourcePath = ResolveExistingSource(relativePath);
        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
            return true;

        string readPath = ResolveReadPath(relativePath);
        error = IsMergedCachePath(readPath)
            ? "Map config resolves only to generated mergedmods data; no lossless writable mod source was found."
            : "Map config could not be resolved to a writable source file.";
        sourcePath = string.Empty;
        return false;
    }

    internal static string ResolveReadPath(string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : AssetManager.ResolveFilePath(relativePath);

    internal static void AtomicWriteAllText(string target, string content)
    {
        AtomicWrite(target, temp => File.WriteAllText(temp, content ?? string.Empty));
    }

    internal static void AtomicWriteAllLines(string target, IEnumerable<string> lines)
    {
        AtomicWrite(target, temp => File.WriteAllLines(temp, lines ?? Array.Empty<string>()));
    }

    private static void AtomicWrite(string target, Action<string> writeTemp)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Authoring target path is empty.", nameof(target));
        if (writeTemp == null)
            throw new ArgumentNullException(nameof(writeTemp));

        string directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temp = target + ".tmp";
        string backup = target + ".bak";

        try
        {
            if (File.Exists(temp))
                File.Delete(temp);

            writeTemp(temp);

            bool hadOriginal = File.Exists(target);
            if (hadOriginal)
            {
                File.Copy(target, backup, overwrite: true);
                File.Delete(target);
            }

            try
            {
                File.Move(temp, target);
            }
            catch
            {
                // Restore the exact pre-save file if commit fails after the original was removed.
                // Keep the .bak copy as an additional recovery artifact.
                if (hadOriginal && File.Exists(backup))
                {
                    try
                    {
                        if (File.Exists(target))
                            File.Delete(target);
                        File.Copy(backup, target, overwrite: true);
                    }
                    catch (Exception restoreError)
                    {
                        Plugin.Logger?.LogError(
                            "World authoring rollback could not restore '" + target +
                            "' from backup: " + restoreError);
                    }
                }
                throw;
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch { }
        }
    }

    internal static bool IsMergedCachePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string root = Path.GetFullPath(global::RWCustom.Custom.RootFolderDirectory())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string merged = Path.GetFullPath(Path.Combine(root, "mergedmods"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path);
            return candidate.StartsWith(merged, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
