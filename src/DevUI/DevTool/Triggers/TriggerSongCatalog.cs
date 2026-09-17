using System;
using System.Collections.Generic;
using System.IO;

namespace DryCycle.DevUI.DevTool.Triggers;

/// <summary>
/// Headless song-name catalogue for Trigger authoring. Vanilla TriggersPage used to synchronously
/// discover this data in its constructor, coupling metadata availability to a screen-space page.
/// The rebuilt editor owns one cached catalogue instead; compatibility may project it back into a
/// legacy page only when Vanilla/Legacy presentation is explicitly active.
/// </summary>
internal static class TriggerSongCatalog
{
    private static string[] currentNames = Array.Empty<string>();
    private static bool loaded;
    private static string loadError = string.Empty;
    private static long revision = 1L;

    internal static string[] CurrentNames => currentNames;
    internal static bool IsLoaded => loaded;
    internal static string LoadError => loadError;
    internal static long Revision => revision;

    internal static bool EnsureLoaded()
    {
        if (loaded) return true;
        return Reload();
    }

    internal static bool Reload()
    {
        try
        {
            string directory = Directory.Exists("./Assets/Resources/Music/Songs")
                ? "./Assets/Resources/Music/Songs"
                : AssetManager.ResolveDirectory("Music" + Path.DirectorySeparatorChar + "Songs");

            FileInfo[] files = new DirectoryInfo(directory).GetFiles();
            HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
            List<string> names = new(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = files[i]?.Name;
                if (string.IsNullOrWhiteSpace(fileName) || fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    continue;

                string song = Path.GetFileNameWithoutExtension(fileName);
                if (!string.IsNullOrWhiteSpace(song) && unique.Add(song))
                    names.Add(song);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            currentNames = names.ToArray();
            loaded = true;
            loadError = string.Empty;
            revision = revision >= long.MaxValue ? 1L : revision + 1L;
            return true;
        }
        catch (Exception error)
        {
            currentNames = Array.Empty<string>();
            loaded = false;
            loadError = error.Message;
            Plugin.Logger?.LogWarning("DevTool Trigger song catalogue failed: " + error.Message);
            return false;
        }
    }

    internal static void ResetRuntimeState()
    {
        currentNames = Array.Empty<string>();
        loaded = false;
        loadError = string.Empty;
        revision = revision >= long.MaxValue ? 1L : revision + 1L;
    }
}
