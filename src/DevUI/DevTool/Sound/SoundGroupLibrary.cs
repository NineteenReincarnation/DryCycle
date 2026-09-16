using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using BepInEx;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Sound;

public static class SoundGroupLibrary
{
    private const string FileName = "sound-groups.xml";
    private const string LocationFileName = "sound-groups.location.txt";

    private enum LoadPhase
    {
        Idle = 0,
        ResolveLocalDirectory = 1,
        DiscoverMods = 2,
        DiscoverLocal = 3,
        ParseFiles = 4,
        BuildSnapshots = 5,
        Finalize = 6
    }

    private sealed class PendingGroupFile
    {
        internal string File = string.Empty;
        internal string SourceName = string.Empty;
        internal bool IsLocal;
    }

    private sealed class ActiveGroupFileReader : IDisposable
    {
        internal PendingGroupFile Pending;
        internal XmlReader Reader;
        internal readonly HashSet<string> IdsInFile = new(StringComparer.OrdinalIgnoreCase);
        internal bool RootValidated;

        public void Dispose()
        {
            try { Reader?.Dispose(); } catch { }
            Reader = null;
        }
    }

    private static Dictionary<string, SoundGroupDefinition> effectiveGroups =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, SoundGroupDefinition> localGroups =
        new(StringComparer.OrdinalIgnoreCase);
    private static List<DevToolProblemSnapshot> problems = new();

    private static volatile SoundGroupLibrarySnapshot current = SoundGroupLibrarySnapshot.Empty;
    private static bool loaded;
    private static bool loading;
    private static string localDirectory;

    private static Dictionary<string, SoundGroupDefinition> buildingEffectiveGroups;
    private static Dictionary<string, SoundGroupDefinition> buildingLocalGroups;
    private static List<DevToolProblemSnapshot> buildingProblems;
    private static readonly List<PendingGroupFile> pendingFiles = new();
    private static HashSet<string> buildingSeenFiles;
    private static List<SoundGroupDefinition> buildingSnapshotSources;
    private static readonly List<SoundGroupSnapshot> buildingSnapshots = new();
    private static string buildingLocalDirectory;
    private static LoadPhase loadPhase;
    private static int discoverModIndex;
    private static int parseFileIndex;
    private static int snapshotGroupIndex;
    private static ActiveGroupFileReader activeGroupFile;
    private static double maxBlockingUnitMilliseconds;
    private static string maxBlockingUnit = string.Empty;

    public static SoundGroupLibrarySnapshot Current => current;

    public static string DefaultLocalDirectory =>
        Path.Combine(Paths.ConfigPath, "DryCycle", "DevTool");

    internal static bool IsReady => loaded && !loading;
    internal static int ProcessedFileCount => Math.Min(parseFileIndex, pendingFiles.Count);
    internal static int TotalFileCount => pendingFiles.Count;
    internal static double MaxBlockingUnitMilliseconds => maxBlockingUnitMilliseconds;
    internal static string MaxBlockingUnit => maxBlockingUnit;

    internal static float Progress
    {
        get
        {
            if (!loading)
                return loaded ? 1f : 0f;

            return loadPhase switch
            {
                LoadPhase.ResolveLocalDirectory => 0.03f,
                LoadPhase.DiscoverMods => 0.05f + 0.20f * DiscoverProgress(),
                LoadPhase.DiscoverLocal => 0.26f,
                LoadPhase.ParseFiles => 0.28f + 0.38f * FileProgress(),
                LoadPhase.BuildSnapshots => 0.67f + 0.31f * SnapshotProgress(),
                LoadPhase.Finalize => 0.99f,
                _ => 0f
            };
        }
    }

    internal static void EnsureLoaded()
    {
        if (!loaded && !loading)
            BeginReload(force: false);
    }

    internal static void Reload() => BeginReload(force: true);

    internal static void BeginReload(bool force)
    {
        if (loading && !force)
            return;

        loading = true;
        loadPhase = LoadPhase.ResolveLocalDirectory;
        buildingEffectiveGroups = new Dictionary<string, SoundGroupDefinition>(StringComparer.OrdinalIgnoreCase);
        buildingLocalGroups = new Dictionary<string, SoundGroupDefinition>(StringComparer.OrdinalIgnoreCase);
        buildingProblems = new List<DevToolProblemSnapshot>();
        buildingSeenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        buildingSnapshotSources = null;
        buildingSnapshots.Clear();
        pendingFiles.Clear();
        buildingLocalDirectory = null;
        discoverModIndex = -1;
        parseFileIndex = 0;
        snapshotGroupIndex = 0;
        DisposeActiveGroupFile();
        maxBlockingUnitMilliseconds = 0d;
        maxBlockingUnit = string.Empty;
    }

    internal static bool StepReload(double budgetMilliseconds)
    {
        if (!loading)
            return loaded;
        if (budgetMilliseconds <= 0d)
            return false;

        long started = Stopwatch.GetTimestamp();
        do
        {
            switch (loadPhase)
            {
                case LoadPhase.ResolveLocalDirectory:
                {
                    long unitStarted = Stopwatch.GetTimestamp();
                    buildingLocalDirectory = ResolveConfiguredLocalDirectory();
                    RecordBlockingUnit("resolve sound-group local directory", ElapsedMilliseconds(unitStarted));
                    discoverModIndex = ModManager.ActiveMods.Count - 1;
                    loadPhase = LoadPhase.DiscoverMods;
                    break;
                }

                case LoadPhase.DiscoverMods:
                    StepDiscoverMod();
                    break;

                case LoadPhase.DiscoverLocal:
                    DiscoverLocalFile();
                    loadPhase = LoadPhase.ParseFiles;
                    break;

                case LoadPhase.ParseFiles:
                    StepParseFile();
                    break;

                case LoadPhase.BuildSnapshots:
                    StepBuildSnapshot();
                    break;

                case LoadPhase.Finalize:
                    PublishBuild();
                    break;
            }

            if (!loading)
                return true;
        }
        while (ElapsedMilliseconds(started) < budgetMilliseconds);

        return false;
    }

    internal static bool SetLocalDirectory(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            string candidate = Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'));
            if (string.Equals(Path.GetExtension(candidate), ".xml", StringComparison.OrdinalIgnoreCase))
                candidate = Path.GetDirectoryName(candidate);
            if (string.IsNullOrWhiteSpace(candidate)) return false;

            candidate = Path.GetFullPath(candidate);
            Directory.CreateDirectory(candidate);
            Directory.CreateDirectory(DefaultLocalDirectory);
            File.WriteAllText(Path.Combine(DefaultLocalDirectory, LocationFileName), candidate);
            localDirectory = candidate;
            BeginReload(force: true);
            return true;
        }
        catch (Exception error)
        {
            AddProblem(
                DevToolProblemSeverity.Error,
                "sound-group-library-path",
                "Unable to use sound-group library directory: " + error.Message,
                string.Empty,
                folder ?? string.Empty);
            RebuildSnapshot();
            return false;
        }
    }

    internal static void ResetLocalDirectory()
    {
        try
        {
            string setting = Path.Combine(DefaultLocalDirectory, LocationFileName);
            if (File.Exists(setting)) File.Delete(setting);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool sound-group path reset failed: " + error.Message);
        }

        localDirectory = DefaultLocalDirectory;
        BeginReload(force: true);
    }

    internal static bool CreateLocalGroup(string id, string name)
    {
        EnsureLoaded();
        if (!loaded || loading) return false;

        id = (id ?? string.Empty).Trim();
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return false;

        if (effectiveGroups.ContainsKey(id) || localGroups.ContainsKey(id))
        {
            AddProblem(
                DevToolProblemSeverity.Warning,
                "duplicate-sound-group-id",
                "Cannot create local sound group because the ID already exists: " + id,
                id,
                Current.LocalFilePath);
            RebuildSnapshot();
            return false;
        }

        localGroups[id] = new SoundGroupDefinition
        {
            Id = id,
            Name = name,
            SourceName = "Local",
            SourcePath = Path.Combine(GetLocalDirectory(), FileName),
            IsLocal = true
        };
        return SaveLocalAndReload();
    }

    internal static bool DeleteLocalGroup(string id)
    {
        EnsureLoaded();
        if (!loaded || loading || string.IsNullOrWhiteSpace(id) || !localGroups.Remove(id)) return false;
        return SaveLocalAndReload();
    }

    internal static bool AddSoundToLocalGroup(string id, SoundGroupSoundDefinition sound)
    {
        EnsureLoaded();
        if (!loaded || loading || sound == null || string.IsNullOrWhiteSpace(sound.Sample)) return false;
        if (!localGroups.TryGetValue(id ?? string.Empty, out SoundGroupDefinition group)) return false;

        if (!string.Equals(sound.Type, "Spot", StringComparison.Ordinal))
        {
            for (int i = group.Sounds.Count - 1; i >= 0; i--)
            {
                SoundGroupSoundDefinition existing = group.Sounds[i];
                if (string.Equals(existing.Type, sound.Type, StringComparison.Ordinal) &&
                    string.Equals(existing.Sample, sound.Sample, StringComparison.OrdinalIgnoreCase))
                {
                    group.Sounds[i] = sound;
                    return SaveLocalAndReload();
                }
            }
        }

        group.Sounds.Add(sound);
        return SaveLocalAndReload();
    }

    internal static bool TryGetGroup(string id, out SoundGroupDefinition group)
    {
        EnsureLoaded();
        if (!loaded || loading)
        {
            group = null;
            return false;
        }
        return effectiveGroups.TryGetValue(id ?? string.Empty, out group);
    }

    internal static void ResetRuntimeState()
    {
        effectiveGroups = new Dictionary<string, SoundGroupDefinition>(StringComparer.OrdinalIgnoreCase);
        localGroups = new Dictionary<string, SoundGroupDefinition>(StringComparer.OrdinalIgnoreCase);
        problems = new List<DevToolProblemSnapshot>();
        current = SoundGroupLibrarySnapshot.Empty;
        loaded = false;
        loading = false;
        localDirectory = null;

        buildingEffectiveGroups = null;
        buildingLocalGroups = null;
        buildingProblems = null;
        buildingSeenFiles = null;
        buildingSnapshotSources = null;
        buildingSnapshots.Clear();
        pendingFiles.Clear();
        buildingLocalDirectory = null;
        loadPhase = LoadPhase.Idle;
        discoverModIndex = -1;
        parseFileIndex = 0;
        snapshotGroupIndex = 0;
        DisposeActiveGroupFile();
        maxBlockingUnitMilliseconds = 0d;
        maxBlockingUnit = string.Empty;
    }

    private static void StepDiscoverMod()
    {
        if (discoverModIndex < 0)
        {
            loadPhase = LoadPhase.DiscoverLocal;
            return;
        }

        ModManager.Mod mod = ModManager.ActiveMods[discoverModIndex--];
        long started = Stopwatch.GetTimestamp();
        try
        {
            // ResolveStandardFile already returns only an existing candidate; do not probe the same
            // path a second time after it succeeds. This keeps group discovery to one filesystem
            // decision per candidate root.
            string file = ResolveStandardFile(mod);
            if (string.IsNullOrEmpty(file))
                return;

            string canonical = CanonicalPath(file);
            if (!buildingSeenFiles.Add(canonical))
                return;

            bool dlc = mod != null && ModManager.PrePackagedModIDs.Contains(mod.id);
            pendingFiles.Add(new PendingGroupFile
            {
                File = file,
                SourceName = dlc ? "DLC · " + SafeModName(mod) : SafeModName(mod),
                IsLocal = false
            });
        }
        finally
        {
            RecordBlockingUnit(
                "discover sound-group file " + (mod?.id ?? string.Empty),
                ElapsedMilliseconds(started));
        }
    }

    private static void DiscoverLocalFile()
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            string localFile = Path.Combine(buildingLocalDirectory ?? DefaultLocalDirectory, FileName);
            if (!File.Exists(localFile))
                return;

            string canonical = CanonicalPath(localFile);
            if (!buildingSeenFiles.Add(canonical))
                return;

            pendingFiles.Add(new PendingGroupFile
            {
                File = localFile,
                SourceName = "Local",
                IsLocal = true
            });
        }
        finally
        {
            RecordBlockingUnit("discover local sound-group file", ElapsedMilliseconds(started));
        }
    }

    private static void StepParseFile()
    {
        if (activeGroupFile != null)
        {
            StepActiveGroupFile();
            return;
        }

        if (parseFileIndex < pendingFiles.Count)
        {
            BeginGroupFile(pendingFiles[parseFileIndex]);
            return;
        }

        buildingSnapshotSources = new List<SoundGroupDefinition>(buildingEffectiveGroups.Values);
        snapshotGroupIndex = 0;
        loadPhase = LoadPhase.BuildSnapshots;
    }

    private static void StepBuildSnapshot()
    {
        if (buildingSnapshotSources != null && snapshotGroupIndex < buildingSnapshotSources.Count)
        {
            SoundGroupDefinition group = buildingSnapshotSources[snapshotGroupIndex++];
            long started = Stopwatch.GetTimestamp();
            buildingSnapshots.Add(BuildGroupSnapshot(group, buildingProblems, reportMissing: true));
            RecordBlockingUnit("build sound-group snapshot " + (group?.Id ?? string.Empty), ElapsedMilliseconds(started));
            return;
        }

        loadPhase = LoadPhase.Finalize;
    }

    private static void PublishBuild()
    {
        long publishStarted = Stopwatch.GetTimestamp();
        buildingSnapshots.Sort(CompareGroupSnapshots);

        effectiveGroups = buildingEffectiveGroups ??
            new Dictionary<string, SoundGroupDefinition>(StringComparer.OrdinalIgnoreCase);
        localGroups = buildingLocalGroups ??
            new Dictionary<string, SoundGroupDefinition>(StringComparer.OrdinalIgnoreCase);
        problems = buildingProblems ?? new List<DevToolProblemSnapshot>();
        localDirectory = string.IsNullOrWhiteSpace(buildingLocalDirectory)
            ? DefaultLocalDirectory
            : buildingLocalDirectory;

        current = new SoundGroupLibrarySnapshot
        {
            LocalDirectory = localDirectory,
            LocalFilePath = Path.Combine(localDirectory, FileName),
            Groups = buildingSnapshots.ToArray(),
            Problems = problems.ToArray()
        };

        loaded = true;
        loading = false;
        loadPhase = LoadPhase.Idle;
        buildingEffectiveGroups = null;
        buildingLocalGroups = null;
        buildingProblems = null;
        buildingSeenFiles = null;
        buildingSnapshotSources = null;
        buildingSnapshots.Clear();
        pendingFiles.Clear();
        buildingLocalDirectory = null;
        discoverModIndex = -1;
        parseFileIndex = 0;
        snapshotGroupIndex = 0;
        RecordBlockingUnit("publish sound-group snapshot", ElapsedMilliseconds(publishStarted));
    }

    private static bool SaveLocalAndReload()
    {
        try
        {
            string dir = GetLocalDirectory();
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, FileName);

            XElement root = new("SoundGroups", new XAttribute("version", "1"));
            foreach (SoundGroupDefinition group in localGroups.Values.OrderBy(value => value.Id, StringComparer.OrdinalIgnoreCase))
            {
                XElement groupElement = new(
                    "SoundGroup",
                    new XAttribute("id", group.Id ?? string.Empty),
                    new XElement("Name", group.Name ?? group.Id ?? string.Empty));

                for (int i = 0; i < group.Sounds.Count; i++)
                    groupElement.Add(SerializeSound(group.Sounds[i]));
                root.Add(groupElement);
            }

            XDocument document = new(new XDeclaration("1.0", "utf-8", null), root);
            document.Save(file);
            BeginReload(force: true);
            return true;
        }
        catch (Exception error)
        {
            AddProblem(
                DevToolProblemSeverity.Error,
                "sound-group-save",
                "Unable to save local sound-group library: " + error.Message,
                string.Empty,
                Path.Combine(GetLocalDirectory(), FileName));
            RebuildSnapshot();
            return false;
        }
    }

    private static XElement SerializeSound(SoundGroupSoundDefinition sound)
    {
        XElement element = new(
            "Sound",
            new XAttribute("type", sound.Type ?? "Omnidirectional"),
            new XAttribute("sample", sound.Sample ?? string.Empty),
            new XAttribute("volume", F(sound.Volume)),
            new XAttribute("pitch", F(sound.Pitch)));

        if (string.Equals(sound.Type, "Directional", StringComparison.Ordinal))
        {
            element.Add(new XAttribute("doppler", F(sound.Doppler)));
            element.Add(new XAttribute("directionX", F(sound.DirectionX)));
            element.Add(new XAttribute("directionY", F(sound.DirectionY)));
        }
        else if (string.Equals(sound.Type, "Spot", StringComparison.Ordinal))
        {
            element.Add(new XAttribute("doppler", F(sound.Doppler)));
            element.Add(new XAttribute("x", F(sound.X)));
            element.Add(new XAttribute("y", F(sound.Y)));
            element.Add(new XAttribute("radius", F(sound.Radius)));
            element.Add(new XAttribute("taper", F(sound.Taper)));
        }

        return element;
    }

    private static void BeginGroupFile(PendingGroupFile pending)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            XmlReaderSettings settings = new()
            {
                CloseInput = true,
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };
            activeGroupFile = new ActiveGroupFileReader
            {
                Pending = pending,
                Reader = XmlReader.Create(pending.File, settings)
            };
        }
        catch (Exception error)
        {
            AddProblem(
                DevToolProblemSeverity.Error,
                "sound-group-xml",
                "Unable to read sound-groups.xml: " + error.Message,
                string.Empty,
                pending?.File ?? string.Empty);
            parseFileIndex++;
            DisposeActiveGroupFile();
        }
        finally
        {
            RecordBlockingUnit("open sound-group XML", ElapsedMilliseconds(started));
        }
    }

    private static void StepActiveGroupFile()
    {
        ActiveGroupFileReader state = activeGroupFile;
        if (state?.Reader == null)
        {
            FinishActiveGroupFile();
            return;
        }

        try
        {
            XmlReader reader = state.Reader;
            if (!state.RootValidated)
            {
                long rootStarted = Stopwatch.GetTimestamp();
                XmlNodeType nodeType = reader.MoveToContent();
                RecordBlockingUnit("read sound-group XML root", ElapsedMilliseconds(rootStarted));
                if (nodeType != XmlNodeType.Element ||
                    !string.Equals(reader.LocalName, "SoundGroups", StringComparison.OrdinalIgnoreCase))
                {
                    AddProblem(
                        DevToolProblemSeverity.Error,
                        "sound-group-xml-root",
                        "sound-groups.xml must use <SoundGroups> as its root element.",
                        string.Empty,
                        state.Pending.File);
                    FinishActiveGroupFile();
                    return;
                }

                state.RootValidated = true;
                if (reader.IsEmptyElement)
                {
                    FinishActiveGroupFile();
                    return;
                }

                reader.Read();
                return;
            }

            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.EndElement &&
                    string.Equals(reader.LocalName, "SoundGroups", StringComparison.OrdinalIgnoreCase))
                {
                    FinishActiveGroupFile();
                    return;
                }

                if (reader.NodeType == XmlNodeType.Element &&
                    string.Equals(reader.LocalName, "SoundGroup", StringComparison.OrdinalIgnoreCase))
                {
                    long groupStarted = Stopwatch.GetTimestamp();
                    XElement element = XNode.ReadFrom(reader) as XElement;
                    if (element != null)
                        ProcessGroupElement(state.Pending, state.IdsInFile, element);
                    RecordBlockingUnit(
                        "parse SoundGroup " + (element == null ? string.Empty : Attr(element, "id")),
                        ElapsedMilliseconds(groupStarted));
                    return;
                }

                if (!reader.Read())
                    break;
            }

            FinishActiveGroupFile();
        }
        catch (Exception error)
        {
            AddProblem(
                DevToolProblemSeverity.Error,
                "sound-group-xml",
                "Unable to read sound-groups.xml: " + error.Message,
                string.Empty,
                state.Pending?.File ?? string.Empty);
            FinishActiveGroupFile();
        }
    }

    private static void ProcessGroupElement(
        PendingGroupFile pending,
        HashSet<string> idsInFile,
        XElement element)
    {
        string file = pending?.File ?? string.Empty;
        string sourceName = pending?.SourceName ?? string.Empty;
        bool isLocal = pending?.IsLocal ?? false;
        string id = Attr(element, "id");
        string name = element.Elements()
            .FirstOrDefault(value => string.Equals(value.Name.LocalName, "Name", StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(id))
        {
            AddProblem(
                DevToolProblemSeverity.Warning,
                "sound-group-missing-id",
                "A sound group was ignored because it has no ID.",
                string.Empty,
                file);
            return;
        }

        if (!idsInFile.Add(id))
        {
            AddProblem(
                DevToolProblemSeverity.Warning,
                "duplicate-sound-group-id",
                "Duplicate sound-group ID inside the same XML file. The first definition is used: " + id,
                id,
                file);
            return;
        }

        SoundGroupDefinition group = new()
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            SourceName = sourceName,
            SourcePath = file,
            IsLocal = isLocal
        };

        foreach (XElement soundElement in element.Elements())
        {
            if (!string.Equals(soundElement.Name.LocalName, "Sound", StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryParseSound(soundElement, group, out SoundGroupSoundDefinition sound))
                group.Sounds.Add(sound);
        }

        if (group.Sounds.Count == 0)
        {
            AddProblem(
                DevToolProblemSeverity.Warning,
                "sound-group-empty",
                "Sound group contains no valid sounds.",
                id,
                file);
        }

        if (isLocal)
            buildingLocalGroups[id] = group;

        if (!buildingEffectiveGroups.TryGetValue(id, out SoundGroupDefinition winner))
        {
            buildingEffectiveGroups[id] = group;
        }
        else
        {
            AddProblem(
                DevToolProblemSeverity.Warning,
                "duplicate-sound-group-id",
                "Duplicate sound-group ID. Using \"" + winner.SourceName +
                "\" and ignoring \"" + sourceName + "\".",
                id,
                file);
        }
    }

    private static void FinishActiveGroupFile()
    {
        DisposeActiveGroupFile();
        parseFileIndex++;
    }

    private static void DisposeActiveGroupFile()
    {
        try { activeGroupFile?.Dispose(); } catch { }
        activeGroupFile = null;
    }

    private static bool TryParseSound(
        XElement element,
        SoundGroupDefinition group,
        out SoundGroupSoundDefinition sound)
    {
        sound = null;
        string type = Attr(element, "type");
        string sample = Attr(element, "sample");
        if (string.IsNullOrWhiteSpace(type)) type = "Omnidirectional";

        if (!string.Equals(type, "Omnidirectional", StringComparison.Ordinal) &&
            !string.Equals(type, "Directional", StringComparison.Ordinal) &&
            !string.Equals(type, "Spot", StringComparison.Ordinal))
        {
            AddProblem(
                DevToolProblemSeverity.Warning,
                "sound-group-type",
                "Unsupported sound type \"" + type + "\" was ignored.",
                group.Id,
                group.SourcePath);
            return false;
        }

        if (string.IsNullOrWhiteSpace(sample))
        {
            AddProblem(
                DevToolProblemSeverity.Error,
                "sound-group-sample",
                "A sound entry is missing its sample name.",
                group.Id,
                group.SourcePath);
            return false;
        }

        sound = new SoundGroupSoundDefinition
        {
            Type = type,
            Sample = sample,
            Volume = FloatAttr(element, "volume", 0.5f),
            Pitch = FloatAttr(element, "pitch", 1f),
            Doppler = FloatAttr(element, "doppler", 0f),
            Taper = FloatAttr(element, "taper", 0.1f),
            X = Mathf.Clamp01(FloatAttr(element, "x", 0.5f)),
            Y = Mathf.Clamp01(FloatAttr(element, "y", 0.5f)),
            Radius = Mathf.Max(0f, FloatAttr(element, "radius", 50f)),
            DirectionX = FloatAttr(element, "directionX", 0f),
            DirectionY = FloatAttr(element, "directionY", -1f)
        };

        // Group parsing must never trigger sample-catalog I/O. Resolution belongs to the later
        // snapshot stage, after the catalog has reached Ready.
        return true;
    }

    private static SoundGroupSnapshot BuildGroupSnapshot(
        SoundGroupDefinition group,
        List<DevToolProblemSnapshot> targetProblems,
        bool reportMissing)
    {
        List<SoundGroupEntrySnapshot> entries = new();
        bool missing = false;
        for (int i = 0; i < group.Sounds.Count; i++)
        {
            SoundGroupSoundDefinition sound = group.Sounds[i];
            EditorSoundSampleSnapshot resource = SoundSampleCatalog.Resolve(sound.Sample);
            if (!resource.Available)
            {
                missing = true;
                if (reportMissing)
                {
                    targetProblems?.Add(new DevToolProblemSnapshot
                    {
                        Severity = DevToolProblemSeverity.Error,
                        Code = "sound-group-missing-sample",
                        Message = "Sound group references an ambient sample that is not available: " + sound.Sample,
                        GroupId = group.Id,
                        SourcePath = group.SourcePath
                    });
                }
            }

            entries.Add(new SoundGroupEntrySnapshot
            {
                Type = sound.Type,
                Sample = sound.Sample,
                Volume = sound.Volume,
                Pitch = sound.Pitch,
                Doppler = sound.Doppler,
                Taper = sound.Taper,
                X = sound.X,
                Y = sound.Y,
                Radius = sound.Radius,
                DirectionX = sound.DirectionX,
                DirectionY = sound.DirectionY,
                Available = resource.Available,
                SourceKind = resource.SourceKind,
                SourceName = resource.SourceName,
                SourceId = resource.SourceId
            });
        }

        return new SoundGroupSnapshot
        {
            Id = group.Id,
            Name = group.Name,
            SourceName = group.SourceName,
            SourcePath = group.SourcePath,
            IsLocal = group.IsLocal,
            HasMissingResources = missing,
            Sounds = entries.ToArray()
        };
    }

    private static void RebuildSnapshot()
    {
        List<SoundGroupSnapshot> groups = new();
        foreach (SoundGroupDefinition group in effectiveGroups.Values)
            groups.Add(BuildGroupSnapshot(group, problems, reportMissing: false));

        groups.Sort(CompareGroupSnapshots);
        current = new SoundGroupLibrarySnapshot
        {
            LocalDirectory = GetLocalDirectory(),
            LocalFilePath = Path.Combine(GetLocalDirectory(), FileName),
            Groups = groups.ToArray(),
            Problems = problems.ToArray()
        };
    }

    private static int CompareGroupSnapshots(SoundGroupSnapshot a, SoundGroupSnapshot b)
    {
        int local = b.IsLocal.CompareTo(a.IsLocal);
        if (local != 0) return local;
        int source = string.Compare(a.SourceName, b.SourceName, StringComparison.OrdinalIgnoreCase);
        if (source != 0) return source;
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveConfiguredLocalDirectory()
    {
        string fallback = DefaultLocalDirectory;
        try
        {
            Directory.CreateDirectory(fallback);
            string setting = Path.Combine(fallback, LocationFileName);
            if (!File.Exists(setting)) return fallback;
            string configured = File.ReadAllText(setting)?.Trim();
            if (string.IsNullOrWhiteSpace(configured)) return fallback;
            configured = Environment.ExpandEnvironmentVariables(configured.Trim('"'));
            return Path.GetFullPath(configured);
        }
        catch (Exception error)
        {
            AddProblem(
                DevToolProblemSeverity.Warning,
                "sound-group-library-path",
                "Unable to read the custom sound-group library directory. The default is used: " + error.Message,
                string.Empty,
                fallback);
            return fallback;
        }
    }

    private static string GetLocalDirectory()
    {
        if (string.IsNullOrWhiteSpace(localDirectory))
            localDirectory = ResolveConfiguredLocalDirectory();
        return localDirectory;
    }

    private static string ResolveStandardFile(ModManager.Mod mod)
    {
        if (mod == null) return string.Empty;

        string[] roots = { mod.path, mod.TargetedPath, mod.NewestPath };
        for (int i = 0; i < roots.Length; i++)
        {
            string root = roots[i];
            if (string.IsNullOrWhiteSpace(root)) continue;
            string candidate = Path.Combine(root, "music", FileName);
            if (File.Exists(candidate)) return candidate;
        }
        return string.Empty;
    }

    private static string CanonicalPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path ?? string.Empty; }
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

    private static void AddProblem(
        DevToolProblemSeverity severity,
        string code,
        string message,
        string groupId,
        string sourcePath)
    {
        List<DevToolProblemSnapshot> target = loading && buildingProblems != null
            ? buildingProblems
            : problems;
        target.Add(new DevToolProblemSnapshot
        {
            Severity = severity,
            Code = code ?? string.Empty,
            Message = message ?? string.Empty,
            GroupId = groupId ?? string.Empty,
            SourcePath = sourcePath ?? string.Empty
        });
    }

    private static string Attr(XElement element, string name) =>
        element?.Attribute(name)?.Value?.Trim() ?? string.Empty;

    private static float FloatAttr(XElement element, string name, float fallback)
    {
        string value = Attr(element, name);
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : fallback;
    }

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static float DiscoverProgress()
    {
        int total = ModManager.ActiveMods.Count;
        if (total <= 0) return 1f;
        int remaining = Math.Max(0, discoverModIndex + 1);
        return Math.Max(0f, Math.Min(1f, (total - remaining) / (float)total));
    }

    private static float FileProgress() => pendingFiles.Count == 0
        ? 1f
        : Math.Max(0f, Math.Min(1f, parseFileIndex / (float)pendingFiles.Count));

    private static float SnapshotProgress()
    {
        int total = buildingSnapshotSources?.Count ?? 0;
        return total <= 0
            ? 1f
            : Math.Max(0f, Math.Min(1f, snapshotGroupIndex / (float)total));
    }

    private static void RecordBlockingUnit(string label, double milliseconds)
    {
        if (milliseconds <= maxBlockingUnitMilliseconds) return;
        maxBlockingUnitMilliseconds = milliseconds;
        maxBlockingUnit = label ?? string.Empty;
    }

    private static double ElapsedMilliseconds(long startedTimestamp) =>
        (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency;
}
