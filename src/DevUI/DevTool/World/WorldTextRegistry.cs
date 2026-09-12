using System;
using System.IO;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Owns the editable lossless world.txt document for the region currently being authored.
/// Unknown sections and mod directives remain untouched because WorldDocument only regenerates
/// room lines that were actually changed by the editor.
/// </summary>
internal static class WorldTextRegistry
{
    private static WorldDocument document;

    internal static string LoadedRegion { get; private set; } = string.Empty;
    internal static string LoadedPath { get; private set; } = string.Empty;
    internal static string LoadError { get; private set; }
    internal static bool Dirty => document?.Dirty == true;

    internal static bool EnsureLoaded(string region)
    {
        string normalized = NormalizeRegion(region);
        if (normalized.Length == 0) return false;

        if (document != null &&
            string.Equals(LoadedRegion, normalized, StringComparison.OrdinalIgnoreCase))
            return true;

        if (Dirty)
        {
            LoadError = "Cannot switch world.txt documents while the current document has unsaved changes.";
            return false;
        }

        return Reload(normalized);
    }

    internal static bool Reload(string region)
    {
        if (Dirty) return false;

        string normalized = NormalizeRegion(region);
        LoadedRegion = normalized;
        LoadedPath = string.Empty;
        LoadError = null;
        document = null;

        if (normalized.Length == 0)
        {
            LoadError = "Region id is empty.";
            return false;
        }

        try
        {
            string path = AssetManager.ResolveFilePath(
                "World" + Path.DirectorySeparatorChar +
                normalized + Path.DirectorySeparatorChar +
                "world_" + normalized + ".txt");

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                LoadError = "world_" + normalized + ".txt could not be resolved.";
                return false;
            }

            LoadedPath = path;
            document = WorldDocument.Parse(File.ReadAllText(path), path);
            return true;
        }
        catch (Exception error)
        {
            LoadError = error.Message;
            document = null;
            global::DryCycle.Plugin.Logger?.LogWarning("WorldText load failed: " + error);
            return false;
        }
    }

    internal static bool HasRoom(string region, string roomName)
    {
        return EnsureLoaded(region) &&
               document != null &&
               document.TryGetRoom(roomName, out _);
    }

    internal static bool TryGetConnection(
        string region,
        string roomName,
        int exitIndex,
        out string destination)
    {
        destination = string.Empty;
        if (!EnsureLoaded(region) || document == null) return false;
        return document.TryGetConnection(roomName, exitIndex, out destination);
    }

    internal static bool TrySetConnection(
        string region,
        string roomName,
        int exitIndex,
        string destinationRoom,
        out string error)
    {
        error = null;
        if (!EnsureLoaded(region) || document == null)
        {
            error = LoadError ?? "world.txt is unavailable.";
            return false;
        }

        if (!document.TryGetRoom(roomName, out _))
        {
            error = "Room '" + roomName + "' does not exist in the editable ROOMS section.";
            return false;
        }

        document.TrySetConnection(roomName, exitIndex, destinationRoom);
        return true;
    }

    internal static bool Save()
    {
        if (document == null || string.IsNullOrWhiteSpace(LoadedPath)) return false;
        if (!document.Dirty) return true;

        string temp = LoadedPath + ".tmp";
        try
        {
            File.WriteAllText(temp, document.Serialize());
            if (File.Exists(LoadedPath))
            {
                File.Copy(LoadedPath, LoadedPath + ".bak", overwrite: true);
                File.Delete(LoadedPath);
            }
            File.Move(temp, LoadedPath);
            document.MarkSaved(LoadedPath);
            LoadError = null;
            return true;
        }
        catch (Exception error)
        {
            LoadError = error.Message;
            global::DryCycle.Plugin.Logger?.LogError("WorldText save failed: " + error);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }

    internal static void Clear()
    {
        if (Dirty) return;
        document = null;
        LoadedRegion = string.Empty;
        LoadedPath = string.Empty;
        LoadError = null;
    }

    private static string NormalizeRegion(string region) =>
        string.IsNullOrWhiteSpace(region) ? string.Empty : region.Trim().ToUpperInvariant();
}
