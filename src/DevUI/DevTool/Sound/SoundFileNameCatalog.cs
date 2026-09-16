using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using RWCustom;

namespace DryCycle.DevUI.DevTool.Sound;

/// <summary>
/// Incremental replacement for the synchronous ambient-file enumeration performed by the vanilla
/// SoundPage constructor. The published arrays/dictionaries are immutable by convention: builders
/// are private and are swapped only after a complete pass.
/// </summary>
internal static class SoundFileNameCatalog
{
    private enum BuildPhase
    {
        Dormant = 0,
        ResolveLoadedDirectory = 1,
        ScanLoadedDirectory = 2,
        PrepareAssetRoots = 3,
        ScanAssetRoots = 4,
        Finalize = 5,
        Ready = 6
    }

    private sealed class AssetRoot
    {
        internal string Root = string.Empty;
        internal string RelativeDirectory = string.Empty;
        internal ModManager.Mod Owner;
        internal bool IncludeInNames;
    }

    private static readonly string[] EmptyNames = Array.Empty<string>();
    private static string[] currentNames = EmptyNames;
    private static Dictionary<string, string> currentLoadedAmbientFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> currentLooseAmbientFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, ModManager.Mod> currentModAmbientOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, ModManager.Mod> currentOfficialDlcAmbientOwners =
        new(StringComparer.OrdinalIgnoreCase);

    private static BuildPhase phase;
    private static readonly List<string> buildingNames = new();
    private static readonly HashSet<string> buildingNameSet = new(StringComparer.Ordinal);
    private static readonly HashSet<string> secondarySeenNames = new(StringComparer.Ordinal);
    private static readonly HashSet<string> rootKeys = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> buildingLoadedAmbientFiles;
    private static Dictionary<string, string> buildingLooseAmbientFiles;
    private static Dictionary<string, ModManager.Mod> buildingModAmbientOwners;
    private static Dictionary<string, ModManager.Mod> buildingOfficialDlcAmbientOwners;
    private static readonly List<AssetRoot> roots = new();
    private static int rootIndex;
    private static IEnumerator<string> enumerator;
    private static AssetRoot activeRoot;
    private static string loadedDirectory = string.Empty;
    private static int processedEntries;
    private static int processedRoots;
    private static double maxUnitMilliseconds;
    private static string maxUnit = string.Empty;

    internal static bool IsReady => phase == BuildPhase.Ready;
    internal static string[] CurrentNames => currentNames;
    internal static Dictionary<string, string> CurrentLoadedAmbientFiles => currentLoadedAmbientFiles;
    internal static Dictionary<string, string> CurrentLooseAmbientFiles => currentLooseAmbientFiles;
    internal static Dictionary<string, ModManager.Mod> CurrentModAmbientOwners => currentModAmbientOwners;
    internal static Dictionary<string, ModManager.Mod> CurrentOfficialDlcAmbientOwners => currentOfficialDlcAmbientOwners;
    internal static int ProcessedEntries => processedEntries;
    internal static int ProcessedRoots => processedRoots;
    internal static int TotalRoots => roots.Count;
    internal static double MaxUnitMilliseconds => maxUnitMilliseconds;
    internal static string MaxUnit => maxUnit;

    internal static float Progress => phase switch
    {
        BuildPhase.Dormant => 0f,
        BuildPhase.ResolveLoadedDirectory => 0.02f,
        BuildPhase.ScanLoadedDirectory => 0.18f,
        BuildPhase.PrepareAssetRoots => 0.22f,
        BuildPhase.ScanAssetRoots => 0.24f + 0.73f * RootProgress(),
        BuildPhase.Finalize => 0.99f,
        BuildPhase.Ready => 1f,
        _ => 0f
    };

    internal static void EnsureStarted()
    {
        if (phase != BuildPhase.Dormant)
            return;

        buildingNames.Clear();
        buildingNameSet.Clear();
        secondarySeenNames.Clear();
        rootKeys.Clear();
        buildingLoadedAmbientFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        buildingLooseAmbientFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        buildingModAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        buildingOfficialDlcAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        roots.Clear();
        rootIndex = 0;
        DisposeEnumerator();
        activeRoot = null;
        loadedDirectory = string.Empty;
        processedEntries = 0;
        processedRoots = 0;
        maxUnitMilliseconds = 0d;
        maxUnit = string.Empty;
        phase = BuildPhase.ResolveLoadedDirectory;
    }

    internal static bool Step(double budgetMilliseconds)
    {
        EnsureStarted();
        if (phase == BuildPhase.Ready)
            return true;
        if (budgetMilliseconds <= 0d)
            return false;

        long frameStart = Stopwatch.GetTimestamp();
        do
        {
            switch (phase)
            {
                case BuildPhase.ResolveLoadedDirectory:
                    MeasureUnit("resolve loaded ambient directory", ResolveLoadedDirectory);
                    phase = BuildPhase.ScanLoadedDirectory;
                    break;

                case BuildPhase.ScanLoadedDirectory:
                    if (!StepLoadedDirectory())
                    {
                        DisposeEnumerator();
                        phase = BuildPhase.PrepareAssetRoots;
                    }
                    break;

                case BuildPhase.PrepareAssetRoots:
                    MeasureUnit("capture AssetManager roots", PrepareAssetRoots);
                    phase = BuildPhase.ScanAssetRoots;
                    break;

                case BuildPhase.ScanAssetRoots:
                    if (!StepAssetRoots())
                    {
                        DisposeEnumerator();
                        phase = BuildPhase.Finalize;
                    }
                    break;

                case BuildPhase.Finalize:
                    MeasureUnit("publish ambient filename snapshot", PublishBuild);
                    phase = BuildPhase.Ready;
                    return true;
            }
        }
        while (ElapsedMilliseconds(frameStart) < budgetMilliseconds);

        return phase == BuildPhase.Ready;
    }

    /// <summary>
    /// SoundPage's patched constructor consumes only a complete snapshot. Returning an empty array
    /// while a prewarm is in flight is intentional: the rebuilt UI can show its shell immediately,
    /// and SoundActivationPipeline publishes the complete names back to the page when ready.
    /// </summary>
    internal static string[] ConstructorNamesOrEmpty()
    {
        EnsureStarted();
        return IsReady ? currentNames : EmptyNames;
    }

    internal static void ResetRuntimeState()
    {
        DisposeEnumerator();
        currentNames = EmptyNames;
        currentLoadedAmbientFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        currentLooseAmbientFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        currentModAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        currentOfficialDlcAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        phase = BuildPhase.Dormant;
        buildingNames.Clear();
        buildingNameSet.Clear();
        secondarySeenNames.Clear();
        rootKeys.Clear();
        buildingLoadedAmbientFiles = null;
        buildingLooseAmbientFiles = null;
        buildingModAmbientOwners = null;
        buildingOfficialDlcAmbientOwners = null;
        roots.Clear();
        rootIndex = 0;
        activeRoot = null;
        loadedDirectory = string.Empty;
        processedEntries = 0;
        processedRoots = 0;
        maxUnitMilliseconds = 0d;
        maxUnit = string.Empty;
    }

    private static void ResolveLoadedDirectory()
    {
        const string local = "./Assets/LoadedSoundEffects/Ambient/";
        loadedDirectory = Directory.Exists(local)
            ? local
            : AssetManager.ResolveDirectory("LoadedSoundEffects" + Path.DirectorySeparatorChar + "Ambient");
        BeginEnumeration(loadedDirectory);
    }

    private static bool StepLoadedDirectory()
    {
        if (enumerator == null)
            return false;

        bool hasNext = MeasureMoveNext("enumerate loaded ambient file");
        if (!hasNext)
            return false;

        string path = enumerator.Current;
        string name = SafeFileName(path);
        if (!string.IsNullOrEmpty(name))
        {
            buildingNames.Add(name);
            buildingNameSet.Add(name);
            if (!buildingLoadedAmbientFiles.ContainsKey(name))
                buildingLoadedAmbientFiles[name] = path;
            processedEntries++;
        }
        return true;
    }

    private static void PrepareAssetRoots()
    {
        roots.Clear();
        rootKeys.Clear();
        string gameRoot = Custom.RootFolderDirectory();

        // Keep the same observable name precedence as AssetManager.ListDirectory. mergedmods and
        // regular soundeffects roots contribute names. Per-mod loadedsoundeffects roots are scanned
        // only for provenance so SoundSampleCatalog never needs a second directory pass.
        AddRoot(Path.Combine(gameRoot, "mergedmods"), "soundeffects/ambient", null, includeInNames: true);

        for (int i = ModManager.ActiveMods.Count - 1; i >= 0; i--)
        {
            ModManager.Mod mod = ModManager.ActiveMods[i];
            if (mod == null) continue;
            AddModRoots(mod.TargetedPath, mod);
            AddModRoots(mod.NewestPath, mod);
            AddModRoots(mod.path, mod);
        }

        string console = AssetManager.GetConsoleFilesSubfolder();
        if (!string.IsNullOrEmpty(console))
            AddRoot(Path.Combine(gameRoot, "consolefiles", console), "soundeffects/ambient", null, includeInNames: true);
        AddRoot(gameRoot, "soundeffects/ambient", null, includeInNames: true);

        rootIndex = 0;
        processedRoots = 0;
        DisposeEnumerator();
        activeRoot = null;
    }

    private static void AddModRoots(string root, ModManager.Mod mod)
    {
        if (string.IsNullOrWhiteSpace(root) || mod == null) return;
        AddRoot(root, "loadedsoundeffects/ambient", mod, includeInNames: false);
        AddRoot(root, "soundeffects/ambient", mod, includeInNames: true);
    }

    private static bool StepAssetRoots()
    {
        while (true)
        {
            if (enumerator != null)
            {
                bool hasNext = MeasureMoveNext("enumerate ambient provenance file");
                if (hasNext)
                {
                    AddAssetFile(enumerator.Current, activeRoot);
                    return true;
                }

                DisposeEnumerator();
                activeRoot = null;
                processedRoots++;
                return rootIndex < roots.Count;
            }

            if (rootIndex >= roots.Count)
                return false;

            activeRoot = roots[rootIndex++];
            string directory = Path.Combine(activeRoot.Root, activeRoot.RelativeDirectory);
            if (!MeasureExists(directory))
            {
                activeRoot = null;
                processedRoots++;
                return true;
            }

            BeginEnumeration(directory);
            return true;
        }
    }

    private static void AddAssetFile(string path, AssetRoot root)
    {
        string originalName = SafeFileName(path);
        if (string.IsNullOrEmpty(originalName))
            return;

        if (root?.Owner != null)
        {
            if (!buildingModAmbientOwners.ContainsKey(originalName))
                buildingModAmbientOwners[originalName] = root.Owner;
            if (IsOfficialDlc(root.Owner.id) && !buildingOfficialDlcAmbientOwners.ContainsKey(originalName))
                buildingOfficialDlcAmbientOwners[originalName] = root.Owner;
        }

        if (root == null || !root.IncludeInNames)
        {
            processedEntries++;
            return;
        }

        if (!secondarySeenNames.Add(originalName))
        {
            processedEntries++;
            return;
        }

        // AssetManager.ListDirectory lower-cases the returned path before SoundPage extracts the
        // filename. Preserve that observable behaviour so vanilla/DLC/mod duplicate precedence does
        // not change when the constructor switches to this snapshot.
        string finalName = originalName.ToLowerInvariant();
        if (buildingNameSet.Add(finalName))
            buildingNames.Add(finalName);

        if (!buildingLooseAmbientFiles.ContainsKey(finalName))
            buildingLooseAmbientFiles[finalName] = path;
        processedEntries++;
    }

    private static void PublishBuild()
    {
        for (int i = buildingNames.Count - 1; i >= 0; i--)
        {
            string name = buildingNames[i] ?? string.Empty;
            if (name.Length > 5 && name.EndsWith(".meta", StringComparison.Ordinal))
                buildingNames.RemoveAt(i);
        }

        currentNames = buildingNames.ToArray();
        currentLoadedAmbientFiles = buildingLoadedAmbientFiles ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        currentLooseAmbientFiles = buildingLooseAmbientFiles ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        currentModAmbientOwners = buildingModAmbientOwners ??
            new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        currentOfficialDlcAmbientOwners = buildingOfficialDlcAmbientOwners ??
            new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddRoot(
        string root,
        string relativeDirectory,
        ModManager.Mod owner,
        bool includeInNames)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativeDirectory)) return;

        string key;
        try { key = Path.GetFullPath(Path.Combine(root, relativeDirectory)); }
        catch { key = Path.Combine(root, relativeDirectory); }
        if (!rootKeys.Add(key)) return;

        roots.Add(new AssetRoot
        {
            Root = root,
            RelativeDirectory = relativeDirectory,
            Owner = owner,
            IncludeInNames = includeInNames
        });
    }

    private static bool IsOfficialDlc(string id) =>
        string.Equals(id, "moreslugcats", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, "watcher", StringComparison.OrdinalIgnoreCase);

    private static void BeginEnumeration(string directory)
    {
        DisposeEnumerator();
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return;
            enumerator = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).GetEnumerator();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool Sound ambient enumeration failed for " + directory + ": " + error.Message);
            enumerator = null;
        }
    }

    private static bool MeasureMoveNext(string label)
    {
        if (enumerator == null) return false;
        long started = Stopwatch.GetTimestamp();
        try
        {
            return enumerator.MoveNext();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool Sound ambient enumeration step failed: " + error.Message);
            DisposeEnumerator();
            return false;
        }
        finally
        {
            RecordUnit(label, ElapsedMilliseconds(started));
        }
    }

    private static bool MeasureExists(string directory)
    {
        long started = Stopwatch.GetTimestamp();
        try { return Directory.Exists(directory); }
        finally { RecordUnit("probe ambient root", ElapsedMilliseconds(started)); }
    }

    private static void MeasureUnit(string label, Action action)
    {
        long started = Stopwatch.GetTimestamp();
        action();
        RecordUnit(label, ElapsedMilliseconds(started));
    }

    private static void RecordUnit(string label, double milliseconds)
    {
        if (milliseconds <= maxUnitMilliseconds) return;
        maxUnitMilliseconds = milliseconds;
        maxUnit = label ?? string.Empty;
    }

    private static void DisposeEnumerator()
    {
        try { enumerator?.Dispose(); }
        catch { }
        enumerator = null;
    }

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static float RootProgress()
    {
        int total = roots.Count;
        if (total <= 0) return 1f;
        return Math.Max(0f, Math.Min(1f, processedRoots / (float)total));
    }

    private static double ElapsedMilliseconds(long startedTimestamp) =>
        (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency;
}
