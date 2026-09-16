using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Sound;

internal static class SoundSampleCatalog
{
    private const string DownpourModId = "moreslugcats";
    private const string WatcherModId = "watcher";

    private enum BuildPhase
    {
        Idle = 0,
        ModAmbient = 1,
        Samples = 2,
        Finalize = 3
    }

    private static Dictionary<string, EditorSoundSampleSnapshot> samples =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> looseAmbientFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, ModManager.Mod> modAmbientOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, ModManager.Mod> officialDlcAmbientOwners =
        new(StringComparer.OrdinalIgnoreCase);

    private static SoundPage publishedPage;
    private static string[] publishedNames;
    private static EditorSoundSampleSnapshot[] sortedCache = Array.Empty<EditorSoundSampleSnapshot>();
    private static bool sortedDirty;

    private static SoundPage requestedPage;
    private static string[] requestedNames;
    private static Dictionary<string, EditorSoundSampleSnapshot> buildingSamples;
    private static Dictionary<string, string> buildingLooseAmbientFiles;
    private static Dictionary<string, ModManager.Mod> buildingModAmbientOwners;
    private static Dictionary<string, ModManager.Mod> buildingOfficialDlcAmbientOwners;
    private static BuildPhase buildPhase;
    private static int buildSampleIndex;
    private static int buildModIndex;
    private static ModManager.Mod buildMod;
    private static string[] buildModDirectories = Array.Empty<string>();
    private static int buildModDirectoryIndex;

    internal static int ProcessedSampleCount => buildPhase == BuildPhase.Idle
        ? publishedNames?.Length ?? 0
        : Math.Min(buildSampleIndex, requestedNames?.Length ?? 0);

    internal static int TotalSampleCount => requestedNames?.Length ?? publishedNames?.Length ?? 0;

    internal static float Progress
    {
        get
        {
            if (buildPhase == BuildPhase.Idle)
                return publishedNames != null ? 1f : 0f;

            return buildPhase switch
            {
                BuildPhase.ModAmbient => 0.03f + 0.25f * ModProgress(),
                BuildPhase.Samples => 0.28f + 0.69f * SampleProgress(),
                BuildPhase.Finalize => 0.99f,
                _ => 0f
            };
        }
    }

    internal static EditorSoundSampleSnapshot[] Refresh(SoundPage page)
    {
        if (page == null)
        {
            ResetRuntimeState();
            return Array.Empty<EditorSoundSampleSnapshot>();
        }

        BeginRefresh(page);
        if (buildPhase == BuildPhase.Idle && sortedDirty)
            RebuildSortedCache();
        return sortedCache;
    }

    internal static void BeginRefresh(SoundPage page)
    {
        if (page == null || !SoundFileNameCatalog.IsReady)
            return;

        string[] names = page.fileNames ?? Array.Empty<string>();
        if (buildPhase == BuildPhase.Idle && ReferenceEquals(publishedNames, names))
        {
            publishedPage = page;
            return;
        }

        if (buildPhase != BuildPhase.Idle &&
            ReferenceEquals(requestedPage, page) &&
            ReferenceEquals(requestedNames, names))
            return;

        requestedPage = page;
        requestedNames = names;
        buildingSamples = new Dictionary<string, EditorSoundSampleSnapshot>(StringComparer.OrdinalIgnoreCase);
        buildingLooseAmbientFiles = SoundFileNameCatalog.CurrentLooseAmbientFiles;
        buildingModAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        buildingOfficialDlcAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        buildSampleIndex = 0;
        buildModIndex = ModManager.ActiveMods.Count - 1;
        buildMod = null;
        buildModDirectories = Array.Empty<string>();
        buildModDirectoryIndex = 0;
        buildPhase = BuildPhase.ModAmbient;
    }

    internal static bool IsReadyFor(SoundPage page)
    {
        if (page == null || buildPhase != BuildPhase.Idle)
            return false;
        return ReferenceEquals(publishedNames, page.fileNames ?? Array.Empty<string>());
    }

    internal static bool StepRefresh(double budgetMilliseconds)
    {
        if (buildPhase == BuildPhase.Idle)
            return true;
        if (budgetMilliseconds <= 0d)
            return false;

        long started = Stopwatch.GetTimestamp();
        do
        {
            switch (buildPhase)
            {
                case BuildPhase.ModAmbient:
                    StepModAmbientIndex();
                    break;
                case BuildPhase.Samples:
                    StepSampleResolution();
                    break;
                case BuildPhase.Finalize:
                    PublishBuild();
                    break;
            }

            if (buildPhase == BuildPhase.Idle)
                return true;
        }
        while (ElapsedMilliseconds(started) < budgetMilliseconds);

        return false;
    }

    internal static EditorSoundSampleSnapshot Resolve(string sample)
    {
        if (string.IsNullOrWhiteSpace(sample))
            return Missing(sample);

        if (samples.TryGetValue(sample, out EditorSoundSampleSnapshot known))
            return known;
        if (buildingSamples != null && buildingSamples.TryGetValue(sample, out known))
            return known;

        if (!SoundFileNameCatalog.IsReady || publishedNames == null || buildPhase != BuildPhase.Idle)
            return Missing(sample);

        EditorSoundSampleSnapshot resolved = ResolveCore(
            sample,
            knownAvailable: false,
            looseAmbientFiles,
            modAmbientOwners,
            officialDlcAmbientOwners);
        samples[sample] = resolved;
        sortedDirty = true;
        return resolved;
    }

    internal static void ResetRuntimeState()
    {
        samples = new Dictionary<string, EditorSoundSampleSnapshot>(StringComparer.OrdinalIgnoreCase);
        looseAmbientFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        modAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        officialDlcAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        publishedPage = null;
        publishedNames = null;
        sortedCache = Array.Empty<EditorSoundSampleSnapshot>();
        sortedDirty = false;

        requestedPage = null;
        requestedNames = null;
        buildingSamples = null;
        buildingLooseAmbientFiles = null;
        buildingModAmbientOwners = null;
        buildingOfficialDlcAmbientOwners = null;
        buildPhase = BuildPhase.Idle;
        buildSampleIndex = 0;
        buildModIndex = -1;
        buildMod = null;
        buildModDirectories = Array.Empty<string>();
        buildModDirectoryIndex = 0;
    }

    private static void StepModAmbientIndex()
    {
        if (buildModDirectoryIndex < buildModDirectories.Length)
        {
            IndexAmbientDirectory(
                buildMod,
                buildModDirectories[buildModDirectoryIndex++],
                buildingModAmbientOwners,
                buildingOfficialDlcAmbientOwners);
            return;
        }

        while (buildModIndex >= 0)
        {
            ModManager.Mod mod = ModManager.ActiveMods[buildModIndex--];
            if (mod == null) continue;

            buildMod = mod;
            buildModDirectories = BuildAmbientDirectories(mod);
            buildModDirectoryIndex = 0;
            if (buildModDirectories.Length == 0)
                continue;
            return;
        }

        buildMod = null;
        buildModDirectories = Array.Empty<string>();
        buildModDirectoryIndex = 0;
        buildPhase = BuildPhase.Samples;
    }

    private static string[] BuildAmbientDirectories(ModManager.Mod mod)
    {
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        AddRoot(roots, mod.TargetedPath);
        AddRoot(roots, mod.NewestPath);
        AddRoot(roots, mod.path);

        if (roots.Count == 0)
            return Array.Empty<string>();

        string[] directories = new string[roots.Count * 2];
        int index = 0;
        foreach (string root in roots)
        {
            directories[index++] = Path.Combine(root, "loadedsoundeffects", "ambient");
            directories[index++] = Path.Combine(root, "soundeffects", "ambient");
        }
        return directories;
    }

    private static void AddRoot(HashSet<string> roots, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        try { roots.Add(Path.GetFullPath(root)); }
        catch { roots.Add(root); }
    }

    private static void IndexAmbientDirectory(
        ModManager.Mod mod,
        string directory,
        Dictionary<string, ModManager.Mod> owners,
        Dictionary<string, ModManager.Mod> officialOwners)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            string[] files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                string key = SampleKey(files[i]);
                if (string.IsNullOrEmpty(key)) continue;
                if (!owners.ContainsKey(key))
                    owners[key] = mod;
                if (IsOfficialDlc(mod.id) && !officialOwners.ContainsKey(key))
                    officialOwners[key] = mod;
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool sound mod ambient index failed for " + SafeModName(mod) + ": " + error.Message);
        }
    }

    private static void StepSampleResolution()
    {
        string[] names = requestedNames ?? Array.Empty<string>();
        while (buildSampleIndex < names.Length)
        {
            string sample = names[buildSampleIndex++];
            if (string.IsNullOrWhiteSpace(sample) || buildingSamples.ContainsKey(sample))
                return;

            buildingSamples[sample] = ResolveCore(
                sample,
                knownAvailable: true,
                buildingLooseAmbientFiles,
                buildingModAmbientOwners,
                buildingOfficialDlcAmbientOwners);
            return;
        }

        buildPhase = BuildPhase.Finalize;
    }

    private static void PublishBuild()
    {
        samples = buildingSamples ?? new Dictionary<string, EditorSoundSampleSnapshot>(StringComparer.OrdinalIgnoreCase);
        looseAmbientFiles = buildingLooseAmbientFiles ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        modAmbientOwners = buildingModAmbientOwners ?? new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        officialDlcAmbientOwners = buildingOfficialDlcAmbientOwners ?? new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        publishedPage = requestedPage;
        publishedNames = requestedNames ?? Array.Empty<string>();
        sortedDirty = true;
        RebuildSortedCache();

        buildingSamples = null;
        buildingLooseAmbientFiles = null;
        buildingModAmbientOwners = null;
        buildingOfficialDlcAmbientOwners = null;
        buildMod = null;
        buildModDirectories = Array.Empty<string>();
        buildModDirectoryIndex = 0;
        buildPhase = BuildPhase.Idle;
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

    private static EditorSoundSampleSnapshot ResolveCore(
        string sample,
        bool knownAvailable,
        Dictionary<string, string> looseFiles,
        Dictionary<string, ModManager.Mod> owners,
        Dictionary<string, ModManager.Mod> officialOwners)
    {
        string resolved = TryResolveAmbientFile(sample, looseFiles);
        if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
        {
            if (TryIdentifyMod(resolved, owners, out ModManager.Mod mod))
                return FromMod(sample, mod);
            if (TryIdentifyOfficialDlcBySample(sample, officialOwners, out ModManager.Mod officialDlc))
                return FromMod(sample, officialDlc);
            return Vanilla(sample);
        }

        if (knownAvailable)
        {
            if (TryIdentifyOfficialDlcBySample(sample, officialOwners, out ModManager.Mod officialDlc))
                return FromMod(sample, officialDlc);
            return Vanilla(sample);
        }

        return Missing(sample);
    }

    private static EditorSoundSampleSnapshot Missing(string sample) => new()
    {
        Sample = sample ?? string.Empty,
        SourceKind = EditorSoundSourceKind.Missing,
        SourceName = "Missing",
        Available = false
    };

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

    private static string TryResolveAmbientFile(string sample, Dictionary<string, string> looseFiles)
    {
        try
        {
            string loadedPath = AssetManager.ResolveFilePath(
                Path.Combine("LoadedSoundEffects", "Ambient", sample));
            if (File.Exists(loadedPath))
                return loadedPath;

            string key = SampleKey(sample);
            if (!string.IsNullOrEmpty(key) &&
                looseFiles != null &&
                looseFiles.TryGetValue(key, out string loosePath) &&
                File.Exists(loosePath))
                return loosePath;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool sound source lookup failed for " + sample + ": " + error.Message);
        }

        return string.Empty;
    }

    private static bool TryIdentifyMod(
        string filePath,
        Dictionary<string, ModManager.Mod> owners,
        out ModManager.Mod owner)
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
        return !string.IsNullOrEmpty(key) && owners != null && owners.TryGetValue(key, out owner);
    }

    private static bool TryIdentifyOfficialDlcBySample(
        string sample,
        Dictionary<string, ModManager.Mod> officialOwners,
        out ModManager.Mod owner)
    {
        string key = SampleKey(sample);
        if (!string.IsNullOrEmpty(key) &&
            officialOwners != null &&
            officialOwners.TryGetValue(key, out owner))
            return true;
        owner = null;
        return false;
    }

    private static string SampleKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Path.GetFileName(value.Trim()) ?? string.Empty; }
        catch { return value.Trim(); }
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
        catch { return false; }
    }

    private static string SafeModName(ModManager.Mod mod)
    {
        if (mod == null) return "Mod";
        try
        {
            string localized = mod.LocalizedName;
            if (!string.IsNullOrWhiteSpace(localized)) return localized;
        }
        catch { }

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

    private static float ModProgress()
    {
        int total = ModManager.ActiveMods.Count;
        if (total <= 0) return 1f;
        int remaining = Math.Max(0, buildModIndex + 1);
        return Math.Max(0f, Math.Min(1f, (total - remaining) / (float)total));
    }

    private static float SampleProgress()
    {
        int total = requestedNames?.Length ?? 0;
        return total <= 0 ? 1f : Math.Max(0f, Math.Min(1f, buildSampleIndex / (float)total));
    }

    private static double ElapsedMilliseconds(long startedTimestamp) =>
        (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency;
}
