using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DryCycle.DevUI.DevTool.Sound;

/// <summary>
/// Authoritative ambient-file catalogue for the rebuilt Sound workspace.
///
/// Rain World owns asset resolution. In particular, AssetManager.ListDirectory contains the real
/// mergedmods / active-mod / targeted-version / newest-version / console / vanilla precedence and
/// duplicate masking rules. Do not reproduce those rules here. We merge both ambient source sets
/// through AssetManager, then consume their returned paths incrementally and publish one
/// immutable snapshot when the pass is complete.
///
/// A small secondary scan of active mod folders is retained only for provenance labels. It never
/// decides whether a sound exists and therefore cannot change the authoritative filename set.
/// </summary>
internal static class SoundFileNameCatalog
{
    private enum BuildPhase
    {
        Dormant = 0,
        CaptureVanillaSources = 1,
        ProcessLoadedNames = 2,
        ProcessResolvedNames = 3,
        PrepareProvenanceRoots = 4,
        ScanProvenanceRoots = 5,
        Finalize = 6,
        Ready = 7
    }

    private sealed class ProvenanceRoot
    {
        internal string Root = string.Empty;
        internal string RelativeDirectory = string.Empty;
        internal ModManager.Mod Owner;
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
    private static Dictionary<string, string> buildingLoadedAmbientFiles;
    private static Dictionary<string, string> buildingLooseAmbientFiles;
    private static Dictionary<string, ModManager.Mod> buildingModAmbientOwners;
    private static Dictionary<string, ModManager.Mod> buildingOfficialDlcAmbientOwners;

    private static string[] loadedSourceFiles = EmptyNames;
    private static string[] resolvedSourceFiles = EmptyNames;
    private static int loadedSourceIndex;
    private static int resolvedSourceIndex;

    private static readonly List<ProvenanceRoot> provenanceRoots = new();
    private static readonly HashSet<string> provenanceRootKeys = new(StringComparer.OrdinalIgnoreCase);
    private static int provenanceRootIndex;
    private static IEnumerator<string> provenanceEnumerator;
    private static ProvenanceRoot activeProvenanceRoot;

    private static int processedEntries;
    private static int processedRoots;
    private static double maxUnitMilliseconds;
    private static string maxUnit = string.Empty;
    private static int publishedRevision;

    internal static bool IsReady => phase == BuildPhase.Ready;
    internal static string[] CurrentNames => currentNames;
    internal static Dictionary<string, string> CurrentLoadedAmbientFiles => currentLoadedAmbientFiles;
    internal static Dictionary<string, string> CurrentLooseAmbientFiles => currentLooseAmbientFiles;
    internal static Dictionary<string, ModManager.Mod> CurrentModAmbientOwners => currentModAmbientOwners;
    internal static Dictionary<string, ModManager.Mod> CurrentOfficialDlcAmbientOwners => currentOfficialDlcAmbientOwners;
    internal static int ProcessedEntries => processedEntries;
    internal static int ProcessedRoots => processedRoots;
    internal static int TotalRoots => provenanceRoots.Count;
    internal static double MaxUnitMilliseconds => maxUnitMilliseconds;
    internal static string MaxUnit => maxUnit;
    internal static int PublishedRevision => publishedRevision;

    internal static float Progress => phase switch
    {
        BuildPhase.Dormant => 0f,
        BuildPhase.CaptureVanillaSources => 0.03f,
        BuildPhase.ProcessLoadedNames =>
            0.08f + 0.20f * Fraction(loadedSourceIndex, loadedSourceFiles.Length),
        BuildPhase.ProcessResolvedNames =>
            0.28f + 0.44f * Fraction(resolvedSourceIndex, resolvedSourceFiles.Length),
        BuildPhase.PrepareProvenanceRoots => 0.74f,
        BuildPhase.ScanProvenanceRoots => 0.76f + 0.22f * RootProgress(),
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
        buildingLoadedAmbientFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        buildingLooseAmbientFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        buildingModAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        buildingOfficialDlcAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);

        loadedSourceFiles = EmptyNames;
        resolvedSourceFiles = EmptyNames;
        loadedSourceIndex = 0;
        resolvedSourceIndex = 0;

        provenanceRoots.Clear();
        provenanceRootKeys.Clear();
        provenanceRootIndex = 0;
        DisposeProvenanceEnumerator();
        activeProvenanceRoot = null;

        processedEntries = 0;
        processedRoots = 0;
        maxUnitMilliseconds = 0d;
        maxUnit = string.Empty;
        phase = BuildPhase.CaptureVanillaSources;
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
                case BuildPhase.CaptureVanillaSources:
                    MeasureUnit("capture Rain World ambient sources", CaptureVanillaSources);
                    phase = BuildPhase.ProcessLoadedNames;
                    break;

                case BuildPhase.ProcessLoadedNames:
                    if (!StepLoadedName())
                        phase = BuildPhase.ProcessResolvedNames;
                    break;

                case BuildPhase.ProcessResolvedNames:
                    if (!StepResolvedName())
                        phase = BuildPhase.PrepareProvenanceRoots;
                    break;

                case BuildPhase.PrepareProvenanceRoots:
                    MeasureUnit("capture sound provenance roots", PrepareProvenanceRoots);
                    phase = BuildPhase.ScanProvenanceRoots;
                    break;

                case BuildPhase.ScanProvenanceRoots:
                    if (!StepProvenanceRoots())
                    {
                        DisposeProvenanceEnumerator();
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
    /// The patched SoundPage constructor consumes only a complete immutable snapshot. During the
    /// first background pass it receives an empty shell; SoundActivationPipeline installs the final
    /// array once this catalogue reaches Ready.
    /// </summary>
    internal static string[] ConstructorNamesOrEmpty()
    {
        EnsureStarted();
        return IsReady ? currentNames : EmptyNames;
    }

    internal static void ResetRuntimeState()
    {
        DisposeProvenanceEnumerator();
        currentNames = EmptyNames;
        currentLoadedAmbientFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        currentLooseAmbientFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        currentModAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);
        currentOfficialDlcAmbientOwners = new Dictionary<string, ModManager.Mod>(StringComparer.OrdinalIgnoreCase);

        phase = BuildPhase.Dormant;
        buildingNames.Clear();
        buildingNameSet.Clear();
        buildingLoadedAmbientFiles = null;
        buildingLooseAmbientFiles = null;
        buildingModAmbientOwners = null;
        buildingOfficialDlcAmbientOwners = null;

        loadedSourceFiles = EmptyNames;
        resolvedSourceFiles = EmptyNames;
        loadedSourceIndex = 0;
        resolvedSourceIndex = 0;

        provenanceRoots.Clear();
        provenanceRootKeys.Clear();
        provenanceRootIndex = 0;
        activeProvenanceRoot = null;

        processedEntries = 0;
        processedRoots = 0;
        maxUnitMilliseconds = 0d;
        maxUnit = string.Empty;
        publishedRevision = 0;
    }

    private static void CaptureVanillaSources()
    {
        // ResolveDirectory selects only one winning directory, so even an empty mod ambient folder
        // hides the entire vanilla library. Resolve individual files from both source sets through
        // AssetManager, as the external audio loader does, preserving its override precedence.
        const string localLoadedDirectory = "./Assets/LoadedSoundEffects/Ambient/";
        loadedSourceFiles = Directory.Exists(localLoadedDirectory)
            ? Directory.GetFiles(localLoadedDirectory)
            : AssetManager.ListDirectory("loadedsoundeffects/ambient") ?? EmptyNames;
        resolvedSourceFiles = AssetManager.ListDirectory("soundeffects/ambient") ?? EmptyNames;
        loadedSourceIndex = 0;
        resolvedSourceIndex = 0;
    }

    private static bool StepLoadedName()
    {
        if (loadedSourceIndex >= loadedSourceFiles.Length)
            return false;

        string path = loadedSourceFiles[loadedSourceIndex++];
        string name = SafeFileName(path);
        if (string.IsNullOrEmpty(name))
            return true;

        // Vanilla adds every file from LoadedSoundEffects first. Keep its exact filename casing so
        // the subsequent AssetManager list is compared with the same observable semantics.
        buildingNames.Add(name);
        buildingNameSet.Add(name);
        if (!buildingLoadedAmbientFiles.ContainsKey(name))
            buildingLoadedAmbientFiles[name] = path;
        TryRecordOwnerFromResolvedPath(name, path);
        processedEntries++;
        return true;
    }

    private static bool StepResolvedName()
    {
        if (resolvedSourceIndex >= resolvedSourceFiles.Length)
            return false;

        string path = resolvedSourceFiles[resolvedSourceIndex++];
        string name = SafeFileName(path);
        if (string.IsNullOrEmpty(name))
            return true;

        // AssetManager already applied active-mod precedence and duplicate masking. The only
        // remaining vanilla rule is that a LoadedSoundEffects filename with the exact same casing
        // wins over the resolved loose file.
        if (!buildingNameSet.Contains(name))
        {
            buildingNames.Add(name);
            buildingNameSet.Add(name);
        }

        if (!buildingLooseAmbientFiles.ContainsKey(name))
            buildingLooseAmbientFiles[name] = path;

        TryRecordOwnerFromResolvedPath(name, path);
        processedEntries++;
        return true;
    }

    private static void PrepareProvenanceRoots()
    {
        provenanceRoots.Clear();
        provenanceRootKeys.Clear();

        // These roots are metadata-only. They cannot add/remove catalogue entries. Iterate in the
        // same priority direction as AssetManager so merged output can still be attributed to the
        // highest-priority active mod that supplied a filename.
        for (int i = ModManager.ActiveMods.Count - 1; i >= 0; i--)
        {
            ModManager.Mod mod = ModManager.ActiveMods[i];
            if (mod == null) continue;
            AddModProvenanceRoots(mod.TargetedPath, mod);
            AddModProvenanceRoots(mod.NewestPath, mod);
            AddModProvenanceRoots(mod.path, mod);
        }

        provenanceRootIndex = 0;
        processedRoots = 0;
        DisposeProvenanceEnumerator();
        activeProvenanceRoot = null;
    }

    private static void AddModProvenanceRoots(string root, ModManager.Mod mod)
    {
        if (string.IsNullOrWhiteSpace(root) || mod == null) return;
        // Rain World mods/DLCs may expose assets either directly or through the standard
        // modify/ overlay. AssetManager merges both forms, so provenance must inspect both too.
        // Missing the modify/ roots caused official DLC samples to fall through as Vanilla.
        AddProvenanceRoot(root, "loadedsoundeffects/ambient", mod);
        AddProvenanceRoot(root, "soundeffects/ambient", mod);
        AddProvenanceRoot(root, "modify/loadedsoundeffects/ambient", mod);
        AddProvenanceRoot(root, "modify/soundeffects/ambient", mod);
    }

    private static bool StepProvenanceRoots()
    {
        while (true)
        {
            if (provenanceEnumerator != null)
            {
                bool hasNext = MeasureMoveNext();
                if (hasNext)
                {
                    RecordProvenanceFile(provenanceEnumerator.Current, activeProvenanceRoot?.Owner);
                    return true;
                }

                DisposeProvenanceEnumerator();
                activeProvenanceRoot = null;
                processedRoots++;
                return provenanceRootIndex < provenanceRoots.Count;
            }

            if (provenanceRootIndex >= provenanceRoots.Count)
                return false;

            activeProvenanceRoot = provenanceRoots[provenanceRootIndex++];
            string directory = Path.Combine(activeProvenanceRoot.Root, activeProvenanceRoot.RelativeDirectory);
            if (!Directory.Exists(directory))
            {
                activeProvenanceRoot = null;
                processedRoots++;
                return true;
            }

            BeginProvenanceEnumeration(directory);
            return true;
        }
    }

    private static void RecordProvenanceFile(string path, ModManager.Mod owner)
    {
        if (owner == null) return;
        string name = SafeFileName(path);
        if (string.IsNullOrEmpty(name)) return;

        if (!buildingModAmbientOwners.ContainsKey(name))
            buildingModAmbientOwners[name] = owner;
        if (IsOfficialDlc(owner.id) && !buildingOfficialDlcAmbientOwners.ContainsKey(name))
            buildingOfficialDlcAmbientOwners[name] = owner;
    }

    private static void TryRecordOwnerFromResolvedPath(string name, string path)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return;
        for (int i = ModManager.ActiveMods.Count - 1; i >= 0; i--)
        {
            ModManager.Mod mod = ModManager.ActiveMods[i];
            if (mod == null) continue;
            if (!IsUnder(path, mod.TargetedPath) && !IsUnder(path, mod.NewestPath) && !IsUnder(path, mod.path))
                continue;

            if (!buildingModAmbientOwners.ContainsKey(name))
                buildingModAmbientOwners[name] = mod;
            if (IsOfficialDlc(mod.id) && !buildingOfficialDlcAmbientOwners.ContainsKey(name))
                buildingOfficialDlcAmbientOwners[name] = mod;
            return;
        }
    }

    private static void PublishBuild()
    {
        // Vanilla removes .meta entries after merging the two source sets.
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
        publishedRevision = publishedRevision == int.MaxValue ? 1 : publishedRevision + 1;
    }

    private static void AddProvenanceRoot(string root, string relativeDirectory, ModManager.Mod owner)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativeDirectory) || owner == null)
            return;

        string key;
        try { key = Path.GetFullPath(Path.Combine(root, relativeDirectory)); }
        catch { key = Path.Combine(root, relativeDirectory); }
        if (!provenanceRootKeys.Add(key)) return;

        provenanceRoots.Add(new ProvenanceRoot
        {
            Root = root,
            RelativeDirectory = relativeDirectory,
            Owner = owner
        });
    }

    private static void BeginProvenanceEnumeration(string directory)
    {
        DisposeProvenanceEnumerator();
        try
        {
            provenanceEnumerator = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).GetEnumerator();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool Sound provenance enumeration failed for " + directory + ": " + error.Message);
            provenanceEnumerator = null;
        }
    }

    private static bool MeasureMoveNext()
    {
        if (provenanceEnumerator == null) return false;
        long started = Stopwatch.GetTimestamp();
        try
        {
            return provenanceEnumerator.MoveNext();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool Sound provenance enumeration step failed: " + error.Message);
            DisposeProvenanceEnumerator();
            return false;
        }
        finally
        {
            RecordUnit("enumerate sound provenance file", ElapsedMilliseconds(started));
        }
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

    private static void DisposeProvenanceEnumerator()
    {
        try { provenanceEnumerator?.Dispose(); }
        catch { }
        provenanceEnumerator = null;
    }

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool IsOfficialDlc(string id) =>
        string.Equals(id, "moreslugcats", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, "watcher", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string filePath, string root)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            string file = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string dir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return file.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static float RootProgress()
    {
        int total = provenanceRoots.Count;
        if (total <= 0) return 1f;
        return Math.Max(0f, Math.Min(1f, processedRoots / (float)total));
    }

    private static float Fraction(int completed, int total) =>
        total <= 0 ? 1f : Math.Max(0f, Math.Min(1f, completed / (float)total));

    private static double ElapsedMilliseconds(long startedTimestamp) =>
        (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency;
}
