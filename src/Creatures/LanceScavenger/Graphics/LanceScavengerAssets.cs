using System;
using System.IO;

namespace DryCycle.Creatures.LanceScavenger;

internal static class LanceScavengerAssets
{
    internal const string Prefix = "LanceScavengerMask";
    private static string _ownedAtlas;
    internal static bool EnsureLoaded()
    {
        if (Futile.atlasManager == null) return false;
        try
        {
            if (!Futile.atlasManager.DoesContainElementWithName(Prefix + "0"))
            {
                string file = AssetManager.ResolveFilePath("atlas/LanceScavenger/LanceScavengerMask.png");
                if (!File.Exists(file)) throw new FileNotFoundException("Lance Scavenger requires the user's existing mask atlas.", file);
                string path = file.Substring(0, file.Length - 4);
                Futile.atlasManager.LoadAtlas(path);
                _ownedAtlas = path;
            }
            for (int i = 0; i < 9; i++)
                if (!Futile.atlasManager.DoesContainElementWithName(Prefix + i))
                    throw new InvalidDataException("Missing atlas element: " + Prefix + i);
            return true;
        }
        catch (Exception ex)
        { Plugin.Logger?.LogError("LanceScavenger mask atlas: " + ex.Message); return false; }
    }
    internal static void Unload()
    {
        if (_ownedAtlas != null && Futile.atlasManager != null) Futile.atlasManager.UnloadAtlas(_ownedAtlas);
        _ownedAtlas = null;
    }
}
