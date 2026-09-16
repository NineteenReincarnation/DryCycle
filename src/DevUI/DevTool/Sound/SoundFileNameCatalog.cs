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
    }

    private static readonly string[] EmptyNames = Array.Empty<string>();
    private static string[] currentNames = EmptyNames;
    private static Dictionary<string, string> currentLooseAmbientFiles =
        new(StringComparer.OrdinalIgnoreCase);

    private static BuildPhase phase;
    private static readonly List<string> buildingNames = new();
    private static readonly HashSet<string> buildingNameSet = new(StringComparer.Ordinal);
    private static readonly HashSet<string> secondarySeenNames = new(StringComparer.Ordinal);
    private static Dictionary<string, string> buildingLooseAmbientFiles;
    private static readonly List<AssetRoot> roots = new();
    private static int rootIndex;
    private static IEnumerator<string> enumerator;
    private static string loadedDirectory = string.Empty;
    private static int processedEntries;
    private static int processedRoots;
    private static double maxUnitMilliseconds;
    private static string maxUnit = string.Empty;

    internal static bool IsReady => phase == BuildPhase.Ready;
    internal static string[] CurrentNames => currentNames;
    internal static Dictionary<string, string> CurrentLooseAmbientFiles => currentLooseAmbientFiles;
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
        buildingLooseAmbientFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        roots.Clear();
        rootIndex = 0;
        DisposeEnumerator();
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
        currentLooseAmbientFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        phase = BuildPhase.Dormant;
        buildingNames.Clear();
        buildingNameSet.Clear();
        secondarySeenNames.Clear();
        buildingLooseAmbientFiles = null;
        roots.Clear();
        rootIndex = 0;
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
            processedEntries++;
        }
        return true;
    }

    private static void PrepareAssetRoots()
    {
        roots.Clear();
        string gameRoot = Custom.RootFolderDirectory();
        roots.Add(new AssetRoot { Root = Path.Combine(gameRoot, "mergedmods") });

        for (int i = ModManager.ActiveMods.Count - 1; i >= 0; i--)
        {
            ModManager.Mod mod = ModManager.ActiveMods[i];
            if (mod == null) continue;
            if (mod.hasTargetedVersionFolder) AddRoot(mod.TargetedPath);
            if (mod.hasNewestFolder) AddRoot(mod.NewestPath);
            AddRoot(mod.path);
        }

        string console = AssetManager.GetConsoleFilesSubfolder();
        if (!string.IsNullOrEmpty(console))
            AddRoot(Path.Combine(gameRoot, "consolefiles", console));
        AddRoot(gameRoot);

        rootIndex = 0;
        processedRoots = 0;
        DisposeEnumerator();
    }

    private static bool StepAssetRoots()
    {
        while (true)
        {
            if (enumerator != null)
            {
                bool hasNext = MeasureMoveNext("enumerate soundeffects/ambient file");
                if (hasNext)
                {
                    AddSecondaryFile(enumerator.Current);
                    return true;
                }

                DisposeEnumerator();
                processedRoots++;
                return rootIndex < roots.Count;
            }

            if (rootIndex >= roots.Count)
                return false;

            string directory = Path.Combine(roots[rootIndex++].Root, "soundeffects", "ambient");
            if (!MeasureExists(directory))
            {
                processedRoots++;
                return true;
            }

            BeginEnumeration(directory);
            return true;
        }
    }

    private static void AddSecondaryFile(string path)
    {
        string originalName = SafeFileName(path);
        if (string.IsNullOrEmpty(originalName) || !secondarySeenNames.Add(originalName))
            return;

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
        currentLooseAmbientFiles = buildingLooseAmbientFiles ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        roots.Add(new AssetRoot { Root = root });
    }

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
