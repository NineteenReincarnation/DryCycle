using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Sound;

internal static class SoundSampleCatalog
{
    private const string DownpourModId = "moreslugcats";
    private const string WatcherModId = "watcher";

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

    internal static EditorSoundSampleSnapshot Resolve(string sample)
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
                return FromMod(sample, mod);

            // Official DLC assets may be served by Rain World's combined consolefiles directory,
            // so the effective path is not always physically beneath the DLC mod root. Recover
            // provenance from the official package roots without doing this for arbitrary mods.
            if (TryIdentifyOfficialDlcBySample(sample, out ModManager.Mod officialDlc))
                return FromMod(sample, officialDlc);

            return Vanilla(sample);
        }

        if (knownAvailable)
        {
            // SoundPage can also expose clips backed by a bundled ambient AudioClip archive. The
            // old implementation treated all of these as Vanilla, which merged Downpour and
            // Watcher into the base-game section. Official-package probing keeps those DLCs
            // distinct while avoiding false attribution to arbitrary third-party mods.
            if (TryIdentifyOfficialDlcBySample(sample, out ModManager.Mod officialDlc))
                return FromMod(sample, officialDlc);

            return Vanilla(sample);
        }

        return new EditorSoundSampleSnapshot
        {
            Sample = sample,
            SourceKind = EditorSoundSourceKind.Missing,
            SourceName = "Missing",
            Available = false
        };
    }

    private static EditorSoundSampleSnapshot Vanilla(string sample) => new()
    {
        Sample = sample,
        SourceKind = EditorSoundSourceKind.Vanilla,
        SourceName = "Vanilla",
        SourceId = string.Empty,
        Available = true
    };

    private static EditorSoundSampleSnapshot FromMod(string sample, ModManager.Mod mod)
    {
        string id = mod?.id ?? string.Empty;
        EditorSoundSourceKind kind = ClassifyMod(id);
        string name = kind switch
        {
            EditorSoundSourceKind.Downpour => "Downpour",
            EditorSoundSourceKind.Watcher => "Watcher",
            _ => SafeModName(mod)
        };

        return new EditorSoundSampleSnapshot
        {
            Sample = sample,
            SourceKind = kind,
            SourceName = name,
            SourceId = id,
            Available = true
        };
    }

    private static EditorSoundSourceKind ClassifyMod(string id)
    {
        if (string.Equals(id, DownpourModId, StringComparison.OrdinalIgnoreCase))
            return EditorSoundSourceKind.Downpour;
        if (string.Equals(id, WatcherModId, StringComparison.OrdinalIgnoreCase))
            return EditorSoundSourceKind.Watcher;

        // "PrePackagedModIDs" also contains DevTools/MMF/Jolly/Expedition, so it must not be
        // treated as a synonym for paid DLC provenance.
        return EditorSoundSourceKind.Mod;
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

        // AssetManager may return mergedmods as the effective file. Only in that case do we
        // recover provenance from the original mod roots. Do not use this fallback for a base-game
        // file: a lower-priority loose mod file with the same name does not override the bundled
        // ambient asset, and attributing it to that mod would be incorrect.
        if (!IsMergedModsPath(filePath)) return false;

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

    private static bool TryIdentifyOfficialDlcBySample(string sample, out ModManager.Mod owner)
    {
        owner = null;
        if (string.IsNullOrWhiteSpace(sample)) return false;

        // Match AssetManager's active-mod priority direction. Restrict this fallback to the two
        // official DLC ids so a same-named file in a lower-priority third-party mod never steals
        // ownership from a base-game/bundled clip.
        for (int i = ModManager.ActiveMods.Count - 1; i >= 0; i--)
        {
            ModManager.Mod mod = ModManager.ActiveMods[i];
            if (mod == null || !IsOfficialDlc(mod.id)) continue;
            if (!ModContainsAmbient(mod, sample)) continue;
            owner = mod;
            return true;
        }

        return false;
    }

    private static bool IsOfficialDlc(string id) =>
        string.Equals(id, DownpourModId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, WatcherModId, StringComparison.OrdinalIgnoreCase);

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

    private static bool IsMergedModsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string marker = Path.DirectorySeparatorChar + "mergedmods" + Path.DirectorySeparatorChar;
        return normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
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
