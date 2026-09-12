using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BepInEx;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Sound;

public static class SoundGroupLibrary
{
    private const string FileName = "sound-groups.xml";
    private const string LocationFileName = "sound-groups.location.txt";

    private static readonly Dictionary<string, SoundGroupDefinition> effectiveGroups =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SoundGroupDefinition> localGroups =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<DevToolProblemSnapshot> problems = new();

    private static volatile SoundGroupLibrarySnapshot current = SoundGroupLibrarySnapshot.Empty;
    private static bool loaded;
    private static string localDirectory;

    public static SoundGroupLibrarySnapshot Current => current;

    public static string DefaultLocalDirectory =>
        Path.Combine(Paths.ConfigPath, "DryCycle", "DevTool");

    internal static void EnsureLoaded()
    {
        if (!loaded) Reload();
    }

    internal static void Reload()
    {
        loaded = true;
        effectiveGroups.Clear();
        localGroups.Clear();
        problems.Clear();

        localDirectory = ResolveConfiguredLocalDirectory();
        string localFile = Path.Combine(localDirectory, FileName);
        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);

        // Portable Mod definitions are authoritative. Walk in Rain World's real active-mod
        // priority order, then fall back to the developer-local library.
        for (int i = ModManager.ActiveMods.Count - 1; i >= 0; i--)
        {
            ModManager.Mod mod = ModManager.ActiveMods[i];
            string file = ResolveStandardFile(mod);
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;
            string canonical = CanonicalPath(file);
            if (!seenFiles.Add(canonical)) continue;
            bool dlc = mod != null && ModManager.PrePackagedModIDs.Contains(mod.id);
            LoadFile(
                file,
                dlc ? "DLC · " + SafeModName(mod) : SafeModName(mod),
                isLocal: false);
        }

        if (File.Exists(localFile))
        {
            string canonical = CanonicalPath(localFile);
            if (seenFiles.Add(canonical))
                LoadFile(localFile, "Local", isLocal: true);
        }

        RebuildSnapshot();
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
            Reload();
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
        Reload();
    }

    internal static bool CreateLocalGroup(string id, string name)
    {
        EnsureLoaded();
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
        if (string.IsNullOrWhiteSpace(id) || !localGroups.Remove(id)) return false;
        return SaveLocalAndReload();
    }

    internal static bool AddSoundToLocalGroup(string id, SoundGroupSoundDefinition sound)
    {
        EnsureLoaded();
        if (sound == null || string.IsNullOrWhiteSpace(sound.Sample)) return false;
        if (!localGroups.TryGetValue(id ?? string.Empty, out SoundGroupDefinition group)) return false;

        // Omni/Directional entries behave like Rain World's own toggled ambient entries: one
        // sample/type pair per group. Spot sounds may intentionally appear multiple times.
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
        return effectiveGroups.TryGetValue(id ?? string.Empty, out group);
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
            Reload();
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

    private static void LoadFile(string file, string sourceName, bool isLocal)
    {
        try
        {
            XDocument document = XDocument.Load(file, LoadOptions.None);
            XElement root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "SoundGroups", StringComparison.OrdinalIgnoreCase))
            {
                AddProblem(
                    DevToolProblemSeverity.Error,
                    "sound-group-xml-root",
                    "sound-groups.xml must use <SoundGroups> as its root element.",
                    string.Empty,
                    file);
                return;
            }

            HashSet<string> idsInFile = new(StringComparer.OrdinalIgnoreCase);
            foreach (XElement element in root.Elements())
            {
                if (!string.Equals(element.Name.LocalName, "SoundGroup", StringComparison.OrdinalIgnoreCase))
                    continue;

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
                    continue;
                }

                if (!idsInFile.Add(id))
                {
                    AddProblem(
                        DevToolProblemSeverity.Warning,
                        "duplicate-sound-group-id",
                        "Duplicate sound-group ID inside the same XML file. The first definition is used: " + id,
                        id,
                        file);
                    continue;
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
                    localGroups[id] = group;

                if (!effectiveGroups.TryGetValue(id, out SoundGroupDefinition winner))
                {
                    effectiveGroups[id] = group;
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
        }
        catch (Exception error)
        {
            AddProblem(
                DevToolProblemSeverity.Error,
                "sound-group-xml",
                "Unable to read sound-groups.xml: " + error.Message,
                string.Empty,
                file);
        }
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

        EditorSoundSampleSnapshot resource = SoundSampleCatalog.Resolve(sample);
        if (!resource.Available)
        {
            AddProblem(
                DevToolProblemSeverity.Error,
                "sound-group-missing-sample",
                "Sound group references an ambient sample that is not available: " + sample,
                group.Id,
                group.SourcePath);
        }

        return true;
    }

    private static void RebuildSnapshot()
    {
        List<SoundGroupSnapshot> groups = new();
        foreach (SoundGroupDefinition group in effectiveGroups.Values)
        {
            List<SoundGroupEntrySnapshot> entries = new();
            bool missing = false;
            for (int i = 0; i < group.Sounds.Count; i++)
            {
                SoundGroupSoundDefinition sound = group.Sounds[i];
                EditorSoundSampleSnapshot resource = SoundSampleCatalog.Resolve(sound.Sample);
                missing |= !resource.Available;
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
                    SourceName = resource.SourceName
                });
            }

            groups.Add(new SoundGroupSnapshot
            {
                Id = group.Id,
                Name = group.Name,
                SourceName = group.SourceName,
                SourcePath = group.SourcePath,
                IsLocal = group.IsLocal,
                HasMissingResources = missing,
                Sounds = entries.ToArray()
            });
        }

        groups.Sort((a, b) =>
        {
            int local = b.IsLocal.CompareTo(a.IsLocal);
            if (local != 0) return local;
            int source = string.Compare(a.SourceName, b.SourceName, StringComparison.OrdinalIgnoreCase);
            if (source != 0) return source;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        current = new SoundGroupLibrarySnapshot
        {
            LocalDirectory = GetLocalDirectory(),
            LocalFilePath = Path.Combine(GetLocalDirectory(), FileName),
            Groups = groups.ToArray(),
            Problems = problems.ToArray()
        };
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

        // The portable standard is mods/ModName/music/sound-groups.xml. Version folders are
        // accepted as compatibility fallbacks, but the Mod root remains the canonical location.
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
        problems.Add(new DevToolProblemSnapshot
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
}
