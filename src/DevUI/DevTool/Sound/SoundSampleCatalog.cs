using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Sound;

public static class SoundSampleCatalog
{
    private static readonly Dictionary<string, EditorSoundSampleSnapshot> samples =
        new(StringComparer.OrdinalIgnoreCase);
    private static SoundPage observedPage;

    internal static EditorSoundSampleSnapshot[] Refresh(SoundPage page)
    {
        if (page == null)
        {
            observedPage = null;
            samples.Clear();
            return Array.Empty<EditorSoundSampleSnapshot>();
        }

        if (!ReferenceEquals(observedPage, page))
        {
            observedPage = page;
            samples.Clear();
            string[] names = page.fileNames ?? Array.Empty<string>();
            for (int i = 0; i < names.Length; i++)
            {
                string sample = names[i];
                if (string.IsNullOrWhiteSpace(sample) || samples.ContainsKey(sample)) continue;
                samples[sample] = ResolveCore(sample, knownAvailable: true);
            }
        }

        EditorSoundSampleSnapshot[] result = samples.Values.ToArray();
        Array.Sort(result, CompareSamples);
        return result;
    }

    public static EditorSoundSampleSnapshot Resolve(string sample)
    {
        if (string.IsNullOrWhiteSpace(sample))
        {
            return new EditorSoundSampleSnapshot
            {
                Sample = sample ?? string.Empty,
                SourceKind = EditorSoundSourceKind.Missing,
                SourceName = "Missing",
                Available = false
            };
        }

        if (samples.TryGetValue(sample, out EditorSoundSampleSnapshot known))
            return known;

        EditorSoundSampleSnapshot resolved = ResolveCore(sample, knownAvailable: false);
        samples[sample] = resolved;
        return resolved;
    }

    private static EditorSoundSampleSnapshot ResolveCore(string sample, bool knownAvailable)
    {
        string resolved = TryResolveAmbientFile(sample);
        if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
        {
            if (TryIdentifyMod(resolved, out ModManager.Mod mod))
            {
                bool dlc = ModManager.PrePackagedModIDs.Contains(mod.id);
                return new EditorSoundSampleSnapshot
                {
                    Sample = sample,
                    SourceKind = dlc ? EditorSoundSourceKind.Dlc : EditorSoundSourceKind.Mod,
                    SourceName = SafeModName(mod),
                    SourceId = mod.id ?? string.Empty,
                    Available = true
                };
            }

            return new EditorSoundSampleSnapshot
            {
                Sample = sample,
                SourceKind = EditorSoundSourceKind.Vanilla,
                SourceName = "Vanilla",
                Available = true
            };
        }

        if (knownAvailable)
        {
            // SoundPage also exposes clips backed by the game's bundled ambient AudioClip archive.
            return new EditorSoundSampleSnapshot
            {
                Sample = sample,
                SourceKind = EditorSoundSourceKind.Vanilla,
                SourceName = "Vanilla",
                Available = true
            };
        }

        return new EditorSoundSampleSnapshot
        {
            Sample = sample,
            SourceKind = EditorSoundSourceKind.Missing,
            SourceName = "Missing",
            Available = false
        };
    }

    private static string TryResolveAmbientFile(string sample)
    {
        try
        {
            string loadedPath = AssetManager.ResolveFilePath(
                Path.Combine("LoadedSoundEffects", "Ambient", sample));
            if (File.Exists(loadedPath))
                return loadedPath;

            string[] loose = AssetManager.ListDirectory("soundeffects/ambient");
            for (int i = 0; i < loose.Length; i++)
            {
                if (string.Equals(Path.GetFileName(loose[i]), sample, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(loose[i]))
                    return loose[i];
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool sound source lookup failed for " + sample + ": " + error.Message);
        }

        return string.Empty;
    }

    private static bool TryIdentifyMod(string filePath, out ModManager.Mod owner)
    {
        owner = null;
        if (string.IsNullOrEmpty(filePath)) return false;

        for (int i = ModManager.ActiveMods.Count - 1; i >= 0; i--)
        {
            ModManager.Mod mod = ModManager.ActiveMods[i];
            if (mod == null) continue;
            if (IsUnder(filePath, mod.TargetedPath) ||
                IsUnder(filePath, mod.NewestPath) ||
                IsUnder(filePath, mod.path))
            {
                owner = mod;
                return true;
            }
        }

        // MergedMods can be the effective path. Recover the owning mod from the original roots
        // using Rain World's same high-to-low active-mod priority.
        string file = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(file)) return false;
        for (int i = ModManager.ActiveMods.Count - 1; i >= 0; i--)
        {
            ModManager.Mod mod = ModManager.ActiveMods[i];
            if (mod == null) continue;
            if (ModContainsAmbient(mod, file))
            {
                owner = mod;
                return true;
            }
        }

        return false;
    }

    private static bool ModContainsAmbient(ModManager.Mod mod, string sample)
    {
        string[] roots = { mod.TargetedPath, mod.NewestPath, mod.path };
        for (int i = 0; i < roots.Length; i++)
        {
            string root = roots[i];
            if (string.IsNullOrEmpty(root)) continue;
            if (File.Exists(Path.Combine(root, "loadedsoundeffects", "ambient", sample)) ||
                File.Exists(Path.Combine(root, "soundeffects", "ambient", sample)))
                return true;
        }
        return false;
    }

    private static bool IsUnder(string filePath, string root)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            string file = Path.GetFullPath(filePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string dir = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            return file.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string SafeModName(ModManager.Mod mod)
    {
        if (mod == null) return "Mod";
        try
        {
            string localized = mod.LocalizedName;
            if (!string.IsNullOrWhiteSpace(localized)) return localized;
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(mod.name)) return mod.name;
        if (!string.IsNullOrWhiteSpace(mod.id)) return mod.id;
        return "Mod";
    }

    private static int CompareSamples(EditorSoundSampleSnapshot a, EditorSoundSampleSnapshot b)
    {
        int kind = a.SourceKind.CompareTo(b.SourceKind);
        if (kind != 0) return kind;
        int source = string.Compare(a.SourceName, b.SourceName, StringComparison.OrdinalIgnoreCase);
        return source != 0 ? source : string.Compare(a.Sample, b.Sample, StringComparison.OrdinalIgnoreCase);
    }
}
