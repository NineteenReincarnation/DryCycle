using System;
using System.IO;

namespace DryCycle.Creatures.LanceScavenger;

internal static class LanceScavengerAssets
{
    internal const string Prefix = "LanceScavengerMask";
    internal const string ColorPrefix = "LanceScavengerMaskColor";
    internal static string ActivePrefix { get; private set; } = Prefix;
    private static string _ownedAtlas;
    private static string _ownedColorAtlas;

    internal static bool EnsureLoaded()
    {
        if (Futile.atlasManager == null) return false;
        // The authored color atlas keeps the bone and gold regions separate.
        // Older installations can still use the original white atlas.
        if (TryLoad(ColorPrefix, ref _ownedColorAtlas, false))
        {
            ActivePrefix = ColorPrefix;
            return true;
        }
        ActivePrefix = Prefix;
        return TryLoad(Prefix, ref _ownedAtlas, true);
    }

    private static bool TryLoad(string prefix, ref string ownedAtlas, bool required)
    {
        try
        {
            if (!Futile.atlasManager.DoesContainElementWithName(prefix + "0"))
            {
                string file = AssetManager.ResolveFilePath("atlas/LanceScavenger/" + prefix + ".png");
                if (!File.Exists(file) || !File.Exists(Path.ChangeExtension(file, ".txt")))
                {
                    if (!required) return false;
                    throw new FileNotFoundException("Lance Scavenger requires its mask PNG and TXT.", file);
                }
                string path = file.Substring(0, file.Length - 4);
                Futile.atlasManager.LoadAtlas(path);
                ownedAtlas = path;
            }
            for (int i = 0; i < 9; i++)
                if (!Futile.atlasManager.DoesContainElementWithName(prefix + i))
                    throw new InvalidDataException("Missing atlas element: " + prefix + i);
            return true;
        }
        catch (Exception ex)
        { Plugin.Logger?.LogError("LanceScavenger mask atlas: " + ex.Message); return false; }
    }
    internal static void Unload()
    {
        if (_ownedColorAtlas != null && Futile.atlasManager != null) Futile.atlasManager.UnloadAtlas(_ownedColorAtlas);
        if (_ownedAtlas != null && Futile.atlasManager != null) Futile.atlasManager.UnloadAtlas(_ownedAtlas);
        _ownedColorAtlas = null;
        _ownedAtlas = null;
        ActivePrefix = Prefix;
    }
}
