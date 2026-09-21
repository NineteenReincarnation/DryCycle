using System;
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
            .Replace('\', Path.DirectorySeparatorChar)
            .ToLowerInvariant();

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

        // Base-game/console files are not redirected. This preserves existing behavior when there is
        // no active mod source, while preventing writes to mergedmods whenever a real source exists.
        return AssetManager.ResolveFilePath(relativePath);
    }

    internal static string ResolveReadPath(string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : AssetManager.ResolveFilePath(relativePath);
}
