using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Expedition;
using MoreSlugcats;

namespace DryCycle.WorldLink;

internal static class GateUnlockRequirements
{
    private const string RelativePath = "world/GateUnlockRequirements.txt";
    private static readonly Dictionary<WorldLinkPortAddress, RegionGate.GateRequirement> Requirements = new();
    private static string _loadedPath;
    private static DateTime _lastWriteUtc;
    private static int _lastPollFrame = int.MinValue;

    internal static int Revision { get; private set; }
    internal static bool Dirty { get; private set; }
    internal static string LoadedPath => _loadedPath ?? string.Empty;

    internal static void Reload()
    {
        Requirements.Clear();
        Revision++;
        Dirty = false;
        _loadedPath = ResolvePath();
        _lastWriteUtc = DateTime.MinValue;
        if (string.IsNullOrEmpty(_loadedPath) || !File.Exists(_loadedPath))
        {
            Plugin.Logger?.LogWarning("WorldLink: GateUnlockRequirements.txt was not found under the active mod's world folder.");
            return;
        }

        try
        {
            string[] lines = File.ReadAllLines(_loadedPath);
            for (int i = 0; i < lines.Length; i++) ParseLine(lines[i], i + 1);
            _lastWriteUtc = File.GetLastWriteTimeUtc(_loadedPath);
            Plugin.Logger?.LogInfo($"WorldLink: loaded {Requirements.Count} gate requirement(s) from '{_loadedPath}'.");
        }
        catch (Exception ex)
        {
            Requirements.Clear();
            Dirty = false;
            Plugin.Logger?.LogError($"WorldLink: failed to load GateUnlockRequirements.txt: {ex}");
        }
    }

    internal static void PollHotReload(int frame)
    {
        // DevTool edits mutate the authoritative in-memory dictionary immediately.
        // Never let the external file watcher overwrite unsaved authoring changes.
        if (Dirty) return;

        if (_lastPollFrame != int.MinValue && frame >= _lastPollFrame && frame - _lastPollFrame < 120) return;
        _lastPollFrame = frame;

        if (string.IsNullOrEmpty(_loadedPath) || !File.Exists(_loadedPath))
        {
            string resolved = ResolvePath();
            if (!string.Equals(resolved, _loadedPath, StringComparison.OrdinalIgnoreCase)) Reload();
            return;
        }

        try
        {
            DateTime write = File.GetLastWriteTimeUtc(_loadedPath);
            if (write != _lastWriteUtc) Reload();
        }
        catch { }
    }

    internal static RegionGate.GateRequirement Get(WorldLinkPortAddress address)
    {
        return Requirements.TryGetValue(address, out RegionGate.GateRequirement requirement)
            ? requirement
            : RegionGate.GateRequirement.DemoLock;
    }

    internal static bool TryGet(WorldLinkPortAddress address, out RegionGate.GateRequirement requirement) =>
        Requirements.TryGetValue(address, out requirement);

    internal static IReadOnlyList<KeyValuePair<WorldLinkPortAddress, RegionGate.GateRequirement>> SnapshotEntries()
    {
        List<KeyValuePair<WorldLinkPortAddress, RegionGate.GateRequirement>> entries = new(Requirements);
        entries.Sort((a, b) => string.Compare(a.Key.ToString(), b.Key.ToString(), StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    internal static bool TrySet(
        WorldLinkPortAddress address,
        string requirementText,
        out string error)
    {
        error = null;
        if (!address.IsValid)
        {
            error = "Room, GateID and PortID are required.";
            return false;
        }

        if (!TryNormalizeRequirement(requirementText, out string value))
        {
            error = $"Unknown gate requirement '{requirementText}'. Use 1..5, DemoLock, RoboLock, OELock, or a registered GateRequirement value.";
            return false;
        }

        if (Requirements.TryGetValue(address, out RegionGate.GateRequirement current) &&
            string.Equals(current?.value, value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Requirements[address] = new RegionGate.GateRequirement(value);
        Dirty = true;
        Revision++;
        return true;
    }

    internal static bool Remove(WorldLinkPortAddress address)
    {
        if (!Requirements.Remove(address)) return false;
        Dirty = true;
        Revision++;
        return true;
    }

    internal static bool Save()
    {
        string path = !string.IsNullOrEmpty(_loadedPath) ? _loadedPath : ResolvePath(forSave: true);
        if (string.IsNullOrWhiteSpace(path))
        {
            Plugin.Logger?.LogError("WorldLink: could not resolve a writable world/GateUnlockRequirements.txt path.");
            return false;
        }

        string tempPath = path + ".tmp";
        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            IReadOnlyList<KeyValuePair<WorldLinkPortAddress, RegionGate.GateRequirement>> entries = SnapshotEntries();
            using (StreamWriter writer = new(tempPath, append: false))
            {
                writer.WriteLine("# DryCycle WorldLink directed gate requirements");
                writer.WriteLine("# Format: Room : GateID : PortID : Requirement");
                writer.WriteLine("# Requirement: 1..5, DemoLock, RoboLock, OELock, or a registered GateRequirement value");
                writer.WriteLine();
                for (int i = 0; i < entries.Count; i++)
                {
                    WorldLinkPortAddress address = entries[i].Key;
                    writer.Write(address.Room);
                    writer.Write(" : ");
                    writer.Write(address.Gate);
                    writer.Write(" : ");
                    writer.Write(address.Port);
                    writer.Write(" : ");
                    writer.WriteLine(FormatRequirement(entries[i].Value));
                }
            }

            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
                File.Delete(path);
            }
            File.Move(tempPath, path);

            _loadedPath = path;
            _lastWriteUtc = File.GetLastWriteTimeUtc(path);
            Dirty = false;
            Plugin.Logger?.LogInfo("WorldLink: GateUnlockRequirements saved: " + path);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError("WorldLink: GateUnlockRequirements save failed: " + ex);
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch { }
            return false;
        }
    }

    internal static bool IsUnlocked(RainWorldGame game, WorldLinkPortAddress address)
    {
        if (game == null || !game.IsStorySession || !address.IsValid) return false;
        List<string> unlocked = game.GetStorySession.saveState.deathPersistentSaveData.unlockedGates;
        if (unlocked == null) return false;
        for (int i = 0; i < unlocked.Count; i++)
        {
            if (string.Equals(unlocked[i], address.SaveKey, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static void UnlockIfAllowed(RainWorldGame game, WorldLinkPortAddress address)
    {
        if (game == null || !game.IsStorySession || !address.IsValid) return;
        DeathPersistentSaveData data = game.GetStorySession.saveState.deathPersistentSaveData;
        if (!data.CanUseUnlockedGates(game.StoryCharacter)) return;

        data.unlockedGates ??= new List<string>();
        for (int i = 0; i < data.unlockedGates.Count; i++)
        {
            if (string.Equals(data.unlockedGates[i], address.SaveKey, StringComparison.OrdinalIgnoreCase)) return;
        }
        data.unlockedGates.Add(address.SaveKey);
    }

    internal static bool Meets(RainWorldGame game, Room room, WorldLinkPortAddress address)
    {
        if (game == null || room == null) return false;
        if (IsUnlocked(game, address)) return true;

        AbstractCreature first = game.FirstAlivePlayer;
        Player player = first?.realizedCreature as Player;
        if (player == null || player.maxRippleLevel >= 1f) return false;

        RegionGate.GateRequirement requirement = Get(address);
        if (requirement == null) return true;

        if (int.TryParse(requirement.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
        {
            if (ModManager.MMF && MMF.cfgDisableGateKarma.Value) numeric = 1;
            int karma = player.Karma;
            if (game.bestHeldScavenger != null) karma += game.karmaOfBestHeldScavenger;
            return numeric - 1 <= karma;
        }

        if (ModManager.MSC && requirement == MoreSlugcatsEnums.GateRequirement.RoboLock)
            return MeetsRoboLock(game, room.world?.region?.name ?? string.Empty, room.abstractRoom?.name ?? string.Empty);

        if (ModManager.MSC && requirement == MoreSlugcatsEnums.GateRequirement.OELock)
            return MeetsOuterExpanseLock(game);

        return false;
    }

    internal static bool MeetsForMap(RainWorldGame game, string roomName, WorldLinkPortAddress address, int currentKarma)
    {
        if (game != null && IsUnlocked(game, address)) return true;
        RegionGate.GateRequirement requirement = Get(address);
        if (requirement == null) return true;

        if (game?.IsStorySession == true && game.GetStorySession.saveState.deathPersistentSaveData.maximumRippleLevel >= 1f)
            return false;

        if (int.TryParse(requirement.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
        {
            if (ModManager.MMF && MMF.cfgDisableGateKarma.Value) numeric = 1;
            return numeric - 1 <= currentKarma;
        }

        if (game == null) return false;
        if (ModManager.MSC && requirement == MoreSlugcatsEnums.GateRequirement.RoboLock)
        {
            string region = game.overWorld?.activeWorld?.region?.name ?? InferRegion(roomName);
            return MeetsRoboLock(game, region, roomName ?? string.Empty);
        }
        if (ModManager.MSC && requirement == MoreSlugcatsEnums.GateRequirement.OELock)
            return MeetsOuterExpanseLock(game);
        return false;
    }

    private static bool MeetsRoboLock(RainWorldGame game, string region, string roomName)
    {
        if (game == null) return false;
        if (ModManager.Expedition && game.rainWorld.ExpeditionMode &&
            ExpeditionData.slugcatPlayer == MoreSlugcatsEnums.SlugcatStatsName.Artificer &&
            string.Equals(region, "UW", StringComparison.OrdinalIgnoreCase) &&
            (roomName?.IndexOf("LC", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
        {
            return true;
        }

        if (game.session is not StoryGameSession story) return false;
        return story.saveState.hasRobo &&
               story.saveState.deathPersistentSaveData.theMark &&
               !string.Equals(region, "SL", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(region, "MS", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(region, "DM", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MeetsOuterExpanseLock(RainWorldGame game)
    {
        if (game?.session is not StoryGameSession story) return false;

        bool gourmandProgress =
            game.rainWorld.progression.miscProgressionData.beaten_Gourmand ||
            game.rainWorld.progression.miscProgressionData.beaten_Gourmand_Full ||
            global::MoreSlugcats.MoreSlugcats.chtUnlockOuterExpanse.Value;

        if (game.StoryCharacter == MoreSlugcatsEnums.SlugcatStatsName.Gourmand)
            return story.saveState.deathPersistentSaveData.theMark || gourmandProgress;

        return (game.StoryCharacter == SlugcatStats.Name.White || game.StoryCharacter == SlugcatStats.Name.Yellow) && gourmandProgress;
    }

    private static string InferRegion(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return string.Empty;
        int underscore = roomName.IndexOf('_');
        return underscore > 0 ? roomName.Substring(0, underscore) : string.Empty;
    }

    private static void ParseLine(string raw, int lineNumber)
    {
        string line = StripComment(raw).Trim();
        if (line.Length == 0) return;

        string[] parts = line.Split(':');
        WorldLinkPortAddress address;
        string requirementText;
        if (parts.Length == 4)
        {
            address = new WorldLinkPortAddress(parts[0], parts[1], parts[2]);
            requirementText = parts[3].Trim();
        }
        else if (parts.Length == 2 && WorldLinkPortAddress.TryParse(parts[0].Trim(), out address))
        {
            requirementText = parts[1].Trim();
        }
        else
        {
            Plugin.Logger?.LogWarning($"WorldLink: GateUnlockRequirements.txt line {lineNumber} has invalid format.");
            return;
        }

        if (!address.IsValid || requirementText.Length == 0)
        {
            Plugin.Logger?.LogWarning($"WorldLink: GateUnlockRequirements.txt line {lineNumber} has an empty address/requirement.");
            return;
        }

        if (Requirements.ContainsKey(address))
        {
            // A duplicated directed address is ambiguous authoring, not a legitimate
            // override mechanism. Fail closed so editing mistakes can never silently
            // weaken a route requirement because of file ordering.
            Plugin.Logger?.LogError($"WorldLink: GateUnlockRequirements.txt line {lineNumber} duplicates {address}. The route is fail-closed with DemoLock.");
            Requirements[address] = RegionGate.GateRequirement.DemoLock;
            return;
        }

        if (!TryNormalizeRequirement(requirementText, out string value))
        {
            Plugin.Logger?.LogWarning($"WorldLink: GateUnlockRequirements.txt line {lineNumber} uses unknown requirement '{requirementText}'. The route is fail-closed with DemoLock.");
            value = RegionGate.GateRequirement.DemoLock.value;
        }
        Requirements[address] = new RegionGate.GateRequirement(value);
    }

    private static bool TryNormalizeRequirement(string text, out string value)
    {
        value = (text ?? string.Empty).Trim();
        if (value.Equals("DemoLock", StringComparison.OrdinalIgnoreCase)) value = RegionGate.GateRequirement.DemoLock.value;
        else if (value.Equals("RoboLock", StringComparison.OrdinalIgnoreCase)) value = "R";
        else if (value.Equals("OELock", StringComparison.OrdinalIgnoreCase)) value = "L";

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return numeric >= 1 && numeric <= 5;

        if (ExtEnumBase.TryParse(typeof(RegionGate.GateRequirement), value, ignoreCase: true, out ExtEnumBase parsed) &&
            parsed is RegionGate.GateRequirement requirement)
        {
            value = requirement.value;
            return true;
        }
        return false;
    }

    private static string FormatRequirement(RegionGate.GateRequirement requirement)
    {
        if (requirement == null) return "DemoLock";
        if (requirement == RegionGate.GateRequirement.DemoLock) return "DemoLock";
        if (string.Equals(requirement.value, "R", StringComparison.OrdinalIgnoreCase)) return "RoboLock";
        if (string.Equals(requirement.value, "L", StringComparison.OrdinalIgnoreCase)) return "OELock";
        return requirement.value ?? "DemoLock";
    }

    private static string StripComment(string text)
    {
        int hash = text.IndexOf('#');
        int slash = text.IndexOf("//", StringComparison.Ordinal);
        int cut = -1;
        if (hash >= 0) cut = hash;
        if (slash >= 0 && (cut < 0 || slash < cut)) cut = slash;
        return cut >= 0 ? text.Substring(0, cut) : text;
    }

    private static string ResolvePath(bool forSave = false)
    {
        string saveCandidate = null;
        try
        {
            if (ModManager.ActiveMods != null)
            {
                for (int i = 0; i < ModManager.ActiveMods.Count; i++)
                {
                    ModManager.Mod mod = ModManager.ActiveMods[i];
                    if (mod == null || !string.Equals(mod.id, Plugin.RainWorldModId, StringComparison.OrdinalIgnoreCase)) continue;

                    string[] candidates =
                    {
                        Path.Combine(mod.path ?? string.Empty, "world", "GateUnlockRequirements.txt"),
                        Path.Combine(mod.NewestPath ?? string.Empty, "world", "GateUnlockRequirements.txt"),
                        Path.Combine(mod.TargetedPath ?? string.Empty, "world", "GateUnlockRequirements.txt"),
                        Path.Combine(mod.basePath ?? string.Empty, "world", "GateUnlockRequirements.txt")
                    };
                    for (int j = 0; j < candidates.Length; j++)
                    {
                        if (string.IsNullOrWhiteSpace(candidates[j])) continue;
                        saveCandidate ??= candidates[j];
                        if (File.Exists(candidates[j])) return candidates[j];
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"WorldLink: direct mod-path requirement lookup failed: {ex.Message}");
        }

        if (forSave && !string.IsNullOrWhiteSpace(saveCandidate)) return saveCandidate;

        try
        {
            string resolved = AssetManager.ResolveFilePath(RelativePath);
            if (string.IsNullOrEmpty(resolved)) return null;
            return forSave || File.Exists(resolved) ? resolved : null;
        }
        catch
        {
            return forSave ? saveCandidate : null;
        }
    }
}
