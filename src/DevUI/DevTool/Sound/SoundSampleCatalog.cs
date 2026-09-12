using System;
using System.Collections.Generic;
using System.IO;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Sound;

internal static class SoundSampleCatalog
{
    private const string DownpourModId = "moreslugcats";
    private const string WatcherModId = "watcher";

    private static readonly Dictionary<string, EditorSoundSampleSnapshot> samples =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> looseAmbientFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ModManager.Mod> modAmbientOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ModManager.Mod> officialDlcAmbientOwners =
        new(StringComparer.OrdinalIgnoreCase);

    private static SoundPage observedPage;
    private static string[] observedNames;
    private static EditorSoundSampleSnapshot[] sortedCache = Array.Empty<EditorSoundSampleSnapshot>();
    private static bool sortedDirty;

    internal static EditorSoundSampleSnapshot[] Refresh(SoundPage page)
    {
        if (page == null)
        {
            Reset();
            return Array.Empty<EditorSoundSampleSnapshot>();
        }

        string[] names = page.fileNames ?? Array.Empty<string>();
        bool pageChanged = !ReferenceEquals(observedPage, page);
        bool namesChanged = !ReferenceEquals(observedNames, names);
        if (pageChanged || namesChanged)
        {
            observedPage = page;
            observedNames = names;
            samples.Clear();
            sortedCache = Array.Empty<EditorSoundSampleSnapshot>();
            sortedDirty = true;

            // The old implementation called AssetManager.ListDirectory and repeatedly probed every
            // active mod once per sample. A large ambient library therefore produced hundreds or
            // thousands of filesystem scans on the first Sound-page frame. Build those lookup
            // indexes once for the page instead, then resolve each sample with O(1) fallbacks.
            BuildAmbientIndexes();

            for (int i = 0; i < names.Length; i++)
            {
                string sample = names[i];
                if (string.IsNullOrWhiteSpace(sample) || samples.ContainsKey(sample)) continue;
                samples[sample] = ResolveCore(sample, knownAvailable: true);
            }
        }

        if (sortedDirty)
            RebuildSortedCache();
        return sortedCache;
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
        sortedDirty = true;
        return resolved;
    }

    private static void Reset()
    {
        observedPage = null;
        observedNames = null;
        samples.Clear();
        looseAmbientFiles.Clear();
        modAmbientOwners.Clear();
        officialDlcAmbientOwners.Clear();
        sortedCache = Array.Empty<EditorSoundSampleSnapshot>();
        sortedDirty = false;
    }

    private static void RebuildSortedCache()
    {
        sortedCache = new EditorSoundSampleSnapshot[samples.Count];
        int index = 0;
        foreach (EditorSoundSampleSnapshot value in samples.Values)
            sortedCache[index++] = value;
        Array.Sort(sortedCache, CompareSamples);
        sortedDirty = false;
    }

    private static void BuildAmbientIndexes()
    {
        looseAmbientFiles.Clear();
        modAmbientOwners.Clear();
        officialDlcAmbientOwners.Clear();

        try
        {
            string[] loose = AssetManager.ListDirectory("soundeffects/ambient") ?? Array.Empty<string>();
            for (int i = 0; i < loose.Length; i++)
            {
                string path = loose[i];
                string key = SampleKey(path);
                if (string.IsNullOrEmpty(key) || looseAmbientFiles.ContainsKey(key)) continue;
                looseAmbientFiles[key] = path;
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool sound ambient index failed: " + error.Message);
        }

        // Walk in the same priority direction used elsewhere in the catalog. The first owner wins,
        // so mergedmods provenance and official-DLC fallback become dictionary lookups instead of
        // repeated File.Exists calls for every sample and every active mod.
        for (int i = ModManager.ActiveMods.Count - 1; i >= 0; i--)
        {
            ModManager.Mod mod = ModManager.ActiveMods[i];
            if (mod == null) continue;
            IndexModAmbient(mod);
        }
    }

    private static void IndexModAmbient(ModManager.Mod mod)
    {
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        AddRoot(roots, mod.TargetedPath);
        AddRoot(roots, mod.NewestPath);
        AddRoot(roots, mod.path);

        foreach (string root in roots)
        {
            IndexAmbientDirectory(mod, Path.Combine(root, "loadedsoundeffects", "ambient"));
            IndexAmbientDirectory(mod, Path.Combine(root, "soundeffects", "ambient"));
        }
    }

    private static void AddRoot(HashSet<string> roots, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            roots.Add(Path.GetFullPath(root));
        }
        catch
        {
            roots.Add(root);
        }
    }

    private static void IndexAmbientDirectory(ModManager.Mod mod, string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            string[] files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                string key = SampleKey(files[i]);
                if (string.IsNullOrEmpty(key)) continue;
                if (!modAmbientOwners.ContainsKey(key))
                    modAmbientOwners[key] = mod;
                if (IsOfficialDlc(mod.id) && !officialDlcAmbientOwners.ContainsKey(key))
                    officialDlcAmbientOwners[key] = mod;
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool sound mod ambient index failed for " + SafeModName(mod) + ": " + error.Message);
        }
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
            // provenance from the pre-indexed official package roots.
            if (TryIdentifyOfficialDlcBySample(sample, out ModManager.Mod officialDlc))
                return FromMod(sample, officialDlc);

            return Vanilla(sample);
        }

        if (knownAvailable)
        {
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

            string key = SampleKey(sample);
            if (!string.IsNullOrEmpty(key) &&
                looseAmbientFiles.TryGetValue(key, out string loosePath) &&
                File.Exists(loosePath))
                return loosePath;
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

        if (!IsMergedModsPath(filePath)) return false;
        string key = SampleKey(filePath);
        return !string.IsNullOrEmpty(key) && modAmbientOwners.TryGetValue(key, out owner);
    }

    private static bool TryIdentifyOfficialDlcBySample(string sample, out ModManager.Mod owner)
    {
        string key = SampleKey(sample);
        if (!string.IsNullOrEmpty(key) && officialDlcAmbientOwners.TryGetValue(key, out owner))
            return true;
        owner = null;
        return false;
    }

    private static string SampleKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try
        {
            return Path.GetFileName(value.Trim()) ?? string.Empty;
        }
        catch
        {
            return value.Trim();
        }
    }

    private static bool IsOfficialDlc(string id) =>
        string.Equals(id, DownpourModId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, WatcherModId, StringComparison.OrdinalIgnoreCase);

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
