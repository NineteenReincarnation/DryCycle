using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal static class WeatherSpatialRegistry
{
    private const int CurrentVersion = 1;
    private const int HotReloadFrames = 120;
    internal const string FileName = "WeatherSpatial.json";

    private static readonly Dictionary<string, WeatherSpatialRegionRules> Regions =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> ParseWarnings = new();

    private static WeatherSpatialRule _globalDefault = WeatherSpatialRule.Allow;
    private static DateTime _lastWriteUtc;
    private static int _lastPollFrame = int.MinValue;
    private static bool _recoveredFromBackup;

    internal static string LoadedPath { get; private set; }
    internal static bool Dirty { get; private set; }
    internal static string FatalLoadError { get; private set; }
    internal static IReadOnlyList<string> Warnings => ParseWarnings;
    internal static WeatherSpatialRule GlobalDefault => _globalDefault;

    internal static void Reload()
    {
        if (Dirty)
        {
            return;
        }

        string path = ResolvePath(forSave: true);
        LoadedPath = path;
        _lastPollFrame = int.MinValue;
        _recoveredFromBackup = false;
        FatalLoadError = null;
        ParseWarnings.Clear();
        Regions.Clear();
        _globalDefault = WeatherSpatialRule.Allow;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Dirty = false;
            _lastWriteUtc = default;
            return;
        }

        if (TryLoadFile(path, out string primaryError))
        {
            Dirty = false;
            RememberWriteTime(path);
            return;
        }

        string backupPath = path + ".bak";
        string primaryWarning = $"Primary {FileName} is invalid: {primaryError}";
        if (File.Exists(backupPath) && TryLoadFile(backupPath, out string backupError))
        {
            ParseWarnings.Insert(0, primaryWarning);
            ParseWarnings.Add($"Recovered {FileName} from '{Path.GetFileName(backupPath)}'. Save from DevTools to repair the primary file.");
            Dirty = true;
            _recoveredFromBackup = true;
            RememberWriteTime(path);
            return;
        }

        Regions.Clear();
        _globalDefault = WeatherSpatialRule.Allow;
        Dirty = false;
        FatalLoadError = File.Exists(backupPath)
            ? $"{FileName} and its .bak are both invalid. Primary: {primaryError}; backup could not be parsed."
            : $"{FileName} is invalid and no valid .bak exists: {primaryError}";
        Plugin.Logger?.LogError("DryCycle weather spatial: " + FatalLoadError);
    }

    internal static void PollHotReload(int frame)
    {
        if (Dirty)
        {
            return;
        }

        if (_lastPollFrame != int.MinValue &&
            frame >= _lastPollFrame &&
            frame - _lastPollFrame < HotReloadFrames)
        {
            return;
        }
        _lastPollFrame = frame;

        string path = LoadedPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            string resolved = ResolvePath(forSave: true);
            if (!string.Equals(resolved, LoadedPath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)))
            {
                Reload();
            }
            return;
        }

        try
        {
            DateTime write = File.GetLastWriteTimeUtc(path);
            if (write != _lastWriteUtc)
            {
                Reload();
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("DryCycle weather spatial hot-reload check failed: " + ex.Message);
        }
    }

    internal static WeatherSpatialRule GetDefaultRule(
        string regionId,
        in WeatherSpatialTarget target)
    {
        if (!TryGetRegion(regionId, out WeatherSpatialRegionRules region))
        {
            return WeatherSpatialRule.Inherit;
        }

        if (target.IsFamily)
        {
            return GetRule(region.FamilyDefaults, target.FamilyId);
        }
        return GetRule(region.WeatherDefaults, target.Key);
    }

    internal static WeatherSpatialRule GetRoomRule(
        string regionId,
        string roomName,
        in WeatherSpatialTarget target)
    {
        if (!TryGetRoom(regionId, roomName, out WeatherSpatialRoomRules room))
        {
            return WeatherSpatialRule.Inherit;
        }

        if (target.IsFamily)
        {
            return GetRule(room.Families, target.FamilyId);
        }
        return GetRule(room.Weather, target.Key);
    }

    internal static void SetDefaultRule(
        string regionId,
        in WeatherSpatialTarget target,
        WeatherSpatialRule rule)
    {
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0)
        {
            return;
        }

        WeatherSpatialRegionRules region = GetOrCreateRegion(regionKey);
        if (target.IsFamily)
        {
            SetRule(region.FamilyDefaults, target.FamilyId, rule);
        }
        else
        {
            SetRule(region.WeatherDefaults, target.Key, rule);
        }
        TrimRegion(regionKey, region);
        Dirty = true;
    }

    internal static void SetRoomRule(
        string regionId,
        string roomName,
        in WeatherSpatialTarget target,
        WeatherSpatialRule rule)
    {
        string regionKey = NormalizeRegion(regionId);
        string roomKey = (roomName ?? string.Empty).Trim();
        if (regionKey.Length == 0 || roomKey.Length == 0)
        {
            return;
        }

        WeatherSpatialRegionRules region = GetOrCreateRegion(regionKey);
        if (!region.Rooms.TryGetValue(roomKey, out WeatherSpatialRoomRules room))
        {
            room = new WeatherSpatialRoomRules();
            region.Rooms[roomKey] = room;
        }

        if (target.IsFamily)
        {
            SetRule(room.Families, target.FamilyId, rule);
        }
        else
        {
            SetRule(room.Weather, target.Key, rule);
        }

        if (room.IsEmpty)
        {
            region.Rooms.Remove(roomKey);
        }
        TrimRegion(regionKey, region);
        Dirty = true;
    }

    internal static bool IsAllowed(Room room, WeatherScheduleEventKind kind, string weatherId)
    {
        if (room?.world?.region == null || room.abstractRoom == null)
        {
            return true;
        }
        return IsAllowed(room.world.region.name, room.abstractRoom.name, kind, weatherId);
    }

    internal static bool IsAllowed(
        string regionId,
        string roomName,
        WeatherScheduleEventKind kind,
        string weatherId)
    {
        if (!WeatherSpatialCatalog.TryGetFamily(kind, weatherId, out WeatherSpatialFamily family))
        {
            return _globalDefault != WeatherSpatialRule.Deny;
        }

        string regionKey = NormalizeRegion(regionId);
        string roomKey = (roomName ?? string.Empty).Trim();
        string exactKey = WeatherSpatialCatalog.WeatherKey(kind, weatherId);

        if (Regions.TryGetValue(regionKey, out WeatherSpatialRegionRules region))
        {
            if (region.Rooms.TryGetValue(roomKey, out WeatherSpatialRoomRules room))
            {
                WeatherSpatialRule exactRoom = GetRule(room.Weather, exactKey);
                if (exactRoom != WeatherSpatialRule.Inherit)
                {
                    return exactRoom == WeatherSpatialRule.Allow;
                }

                WeatherSpatialRule familyRoom = GetRule(room.Families, family.Id);
                if (familyRoom != WeatherSpatialRule.Inherit)
                {
                    return familyRoom == WeatherSpatialRule.Allow;
                }
            }

            WeatherSpatialRule exactDefault = GetRule(region.WeatherDefaults, exactKey);
            if (exactDefault != WeatherSpatialRule.Inherit)
            {
                return exactDefault == WeatherSpatialRule.Allow;
            }

            WeatherSpatialRule familyDefault = GetRule(region.FamilyDefaults, family.Id);
            if (familyDefault != WeatherSpatialRule.Inherit)
            {
                return familyDefault == WeatherSpatialRule.Allow;
            }
        }

        return _globalDefault != WeatherSpatialRule.Deny;
    }

    internal static bool IsFamilyAllowed(string regionId, string roomName, string familyId)
    {
        if (!WeatherSpatialCatalog.TryGetFamily(familyId, out WeatherSpatialFamily family))
        {
            return _globalDefault != WeatherSpatialRule.Deny;
        }

        string regionKey = NormalizeRegion(regionId);
        string roomKey = (roomName ?? string.Empty).Trim();
        if (Regions.TryGetValue(regionKey, out WeatherSpatialRegionRules region))
        {
            if (region.Rooms.TryGetValue(roomKey, out WeatherSpatialRoomRules room))
            {
                WeatherSpatialRule roomRule = GetRule(room.Families, family.Id);
                if (roomRule != WeatherSpatialRule.Inherit)
                {
                    return roomRule == WeatherSpatialRule.Allow;
                }
            }

            WeatherSpatialRule regionRule = GetRule(region.FamilyDefaults, family.Id);
            if (regionRule != WeatherSpatialRule.Inherit)
            {
                return regionRule == WeatherSpatialRule.Allow;
            }
        }
        return _globalDefault != WeatherSpatialRule.Deny;
    }

    internal static bool Save()
    {
        string path = string.IsNullOrEmpty(LoadedPath) ? ResolvePath(forSave: true) : LoadedPath;
        if (string.IsNullOrEmpty(path))
        {
            Plugin.Logger?.LogError("DryCycle weather spatial: could not resolve a writable world/WeatherSpatial.json path.");
            return false;
        }

        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = Json.Serialize(BuildJsonRoot());
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);

            if (_recoveredFromBackup && File.Exists(path))
            {
                ArchiveInvalid(path);
            }
            else if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temp, path);

            LoadedPath = path;
            FatalLoadError = null;
            Dirty = false;
            _recoveredFromBackup = false;
            ParseWarnings.Clear();
            RememberWriteTime(path);
            Plugin.Logger?.LogInfo("DryCycle weather spatial saved: " + path);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError("DryCycle weather spatial save failed: " + ex);
            return false;
        }
    }

    internal static bool RepairBrokenFile()
    {
        string path = string.IsNullOrEmpty(LoadedPath) ? ResolvePath(forSave: true) : LoadedPath;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                ArchiveInvalid(path);
            }
            if (File.Exists(path + ".bak"))
            {
                ArchiveInvalid(path + ".bak");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("DryCycle weather spatial could not archive invalid JSON: " + ex.Message);
        }

        Regions.Clear();
        ParseWarnings.Clear();
        _globalDefault = WeatherSpatialRule.Allow;
        FatalLoadError = null;
        _recoveredFromBackup = false;
        Dirty = true;
        LoadedPath = path;
        return Save();
    }

    internal static WeatherSpatialValidationResult Validate(World activeWorld)
    {
        WeatherSpatialValidationResult result = new();
        if (!string.IsNullOrEmpty(FatalLoadError))
        {
            result.Error(FatalLoadError);
        }
        for (int i = 0; i < ParseWarnings.Count; i++)
        {
            result.Warn(ParseWarnings[i]);
        }

        foreach (KeyValuePair<string, WeatherSpatialRegionRules> regionPair in Regions)
        {
            string regionId = regionPair.Key;
            WeatherSpatialRegionRules rules = regionPair.Value;
            ValidateRuleKeys(regionId, rules, result);

            HashSet<string> knownRooms = LoadKnownRooms(regionId, activeWorld, out string roomSourceWarning);
            if (!string.IsNullOrEmpty(roomSourceWarning))
            {
                result.Warn(roomSourceWarning);
            }

            foreach (KeyValuePair<string, WeatherSpatialRoomRules> roomPair in rules.Rooms)
            {
                string roomName = roomPair.Key;
                if (knownRooms.Count > 0 && !knownRooms.Contains(roomName))
                {
                    result.Error($"{regionId}: configured room '{roomName}' does not exist in the region world file.");
                }

                if (!roomName.StartsWith(regionId + "_", StringComparison.OrdinalIgnoreCase))
                {
                    result.Warn($"{regionId}: room '{roomName}' does not use the usual '{regionId}_' prefix.");
                }
                ValidateRoomKeys(regionId, roomName, roomPair.Value, result);
            }

            if (!RegionClimateRegistry.TryGetProfile(regionId, out RegionClimateProfile profile))
            {
                if (!rules.IsEmpty)
                {
                    result.Warn($"{regionId}: spatial rules exist but RegionClimate.txt has no profile for this region.");
                }
                continue;
            }

            ValidateClimateReferences(regionId, rules, profile, result);
            if (knownRooms.Count > 0)
            {
                ValidateClimateCoverage(regionId, knownRooms, profile, result);
            }
        }

        return result;
    }

    internal static IEnumerable<string> ConfiguredRegionIds => Regions.Keys;

    private static void ValidateRuleKeys(
        string regionId,
        WeatherSpatialRegionRules rules,
        WeatherSpatialValidationResult result)
    {
        foreach (string family in rules.FamilyDefaults.Keys)
        {
            if (!WeatherSpatialCatalog.IsKnownFamily(family))
            {
                result.Error($"{regionId}: unknown family default '{family}'.");
            }
        }
        foreach (string weather in rules.WeatherDefaults.Keys)
        {
            if (!WeatherSpatialCatalog.TryParseWeatherKey(weather, out _, out _))
            {
                result.Error($"{regionId}: unknown weather default '{weather}'.");
            }
        }
    }

    private static void ValidateRoomKeys(
        string regionId,
        string roomName,
        WeatherSpatialRoomRules room,
        WeatherSpatialValidationResult result)
    {
        foreach (string family in room.Families.Keys)
        {
            if (!WeatherSpatialCatalog.IsKnownFamily(family))
            {
                result.Error($"{regionId}/{roomName}: unknown family '{family}'.");
            }
        }
        foreach (string weather in room.Weather.Keys)
        {
            if (!WeatherSpatialCatalog.TryParseWeatherKey(weather, out _, out _))
            {
                result.Error($"{regionId}/{roomName}: unknown weather '{weather}'.");
            }
        }
    }

    private static void ValidateClimateReferences(
        string regionId,
        WeatherSpatialRegionRules rules,
        RegionClimateProfile profile,
        WeatherSpatialValidationResult result)
    {
        HashSet<string> families = new(StringComparer.OrdinalIgnoreCase);
        foreach (string family in rules.FamilyDefaults.Keys)
        {
            families.Add(family);
        }
        foreach (WeatherSpatialRoomRules room in rules.Rooms.Values)
        {
            foreach (string family in room.Families.Keys)
            {
                families.Add(family);
            }
        }
        foreach (string familyId in families)
        {
            if (WeatherSpatialCatalog.TryGetFamily(familyId, out WeatherSpatialFamily family) &&
                !ClimateHasAnyMember(profile, family))
            {
                result.Warn($"{regionId}: family '{family.Id}' has spatial rules but no member is enabled by RegionClimate.txt.");
            }
        }

        HashSet<string> exactKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (string key in rules.WeatherDefaults.Keys)
        {
            exactKeys.Add(key);
        }
        foreach (WeatherSpatialRoomRules room in rules.Rooms.Values)
        {
            foreach (string key in room.Weather.Keys)
            {
                exactKeys.Add(key);
            }
        }
        foreach (string key in exactKeys)
        {
            if (!WeatherSpatialCatalog.TryParseWeatherKey(key, out WeatherScheduleEventKind kind, out string id))
            {
                continue;
            }
            bool present = kind == WeatherScheduleEventKind.Weather
                ? profile.ContainsWeatherId(id)
                : profile.ContainsDangerId(id);
            if (!present)
            {
                result.Warn($"{regionId}: '{key}' has spatial rules but is not enabled by RegionClimate.txt.");
            }
        }
    }

    private static void ValidateClimateCoverage(
        string regionId,
        HashSet<string> rooms,
        RegionClimateProfile profile,
        WeatherSpatialValidationResult result)
    {
        IReadOnlyList<WeatherSpatialFamily> families = WeatherSpatialCatalog.AllFamilies;
        for (int i = 0; i < families.Count; i++)
        {
            WeatherSpatialFamily family = families[i];
            List<WeatherSpatialMember> scheduledMembers = new();
            for (int j = 0; j < family.Members.Count; j++)
            {
                WeatherSpatialMember member = family.Members[j];
                bool climateEnabled = member.Kind == WeatherScheduleEventKind.Weather
                    ? profile.ContainsWeatherId(member.Id)
                    : profile.ContainsDangerId(member.Id);
                if (climateEnabled && WeatherTypeRegistry.IsSchedulable(member.Id, member.Kind))
                {
                    scheduledMembers.Add(member);
                }
            }

            if (scheduledMembers.Count == 0)
            {
                continue;
            }

            bool anyAllowed = false;
            foreach (string room in rooms)
            {
                for (int memberIndex = 0; memberIndex < scheduledMembers.Count; memberIndex++)
                {
                    WeatherSpatialMember member = scheduledMembers[memberIndex];
                    if (IsAllowed(regionId, room, member.Kind, member.Id))
                    {
                        anyAllowed = true;
                        break;
                    }
                }
                if (anyAllowed)
                {
                    break;
                }
            }

            if (!anyAllowed)
            {
                result.Warn($"{regionId}: RegionClimate can schedule '{family.Id}', but no known room allows any schedulable member of that family.");
            }
        }
    }

    private static bool ClimateHasAnyMember(RegionClimateProfile profile, WeatherSpatialFamily family)
    {
        for (int i = 0; i < family.Members.Count; i++)
        {
            WeatherSpatialMember member = family.Members[i];
            if ((member.Kind == WeatherScheduleEventKind.Weather && profile.ContainsWeatherId(member.Id)) ||
                (member.Kind == WeatherScheduleEventKind.DangerType && profile.ContainsDangerId(member.Id)))
            {
                return true;
            }
        }
        return false;
    }

    private static HashSet<string> LoadKnownRooms(
        string regionId,
        World activeWorld,
        out string warning)
    {
        warning = null;
        HashSet<string> rooms = new(StringComparer.OrdinalIgnoreCase);
        if (activeWorld?.region != null &&
            string.Equals(activeWorld.region.name, regionId, StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < activeWorld.NumberOfRooms; i++)
            {
                AbstractRoom room = activeWorld.GetAbstractRoom(activeWorld.firstRoomIndex + i);
                if (room != null && !string.IsNullOrEmpty(room.name))
                {
                    rooms.Add(room.name);
                }
            }
            return rooms;
        }

        string relative = "World" + Path.DirectorySeparatorChar + regionId +
                          Path.DirectorySeparatorChar + "world_" + regionId + ".txt";
        string path = null;
        try
        {
            path = AssetManager.ResolveFilePath(relative);
        }
        catch
        {
            path = null;
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            warning = $"{regionId}: could not locate '{relative}' to validate configured room names.";
            return rooms;
        }

        bool inRooms = false;
        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = StripComment(lines[i]).Trim();
                if (line.Equals("ROOMS", StringComparison.OrdinalIgnoreCase))
                {
                    inRooms = true;
                    continue;
                }
                if (!inRooms)
                {
                    continue;
                }
                if (line.StartsWith("END", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                if (line.Length == 0)
                {
                    continue;
                }
                int colon = line.IndexOf(':');
                string name = (colon >= 0 ? line.Substring(0, colon) : line).Trim();
                if (name.Length > 0)
                {
                    rooms.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            warning = $"{regionId}: failed reading room list for validation: {ex.Message}";
        }
        return rooms;
    }

    private static bool TryLoadFile(string path, out string error)
    {
        error = null;
        try
        {
            object parsed = Json.Deserialize(File.ReadAllText(path));
            if (parsed is not Dictionary<string, object> root)
            {
                error = "root JSON value is not an object";
                return false;
            }

            Dictionary<string, WeatherSpatialRegionRules> parsedRegions =
                new(StringComparer.OrdinalIgnoreCase);
            WeatherSpatialRule parsedGlobal = WeatherSpatialRule.Allow;
            List<string> warnings = new();
            ParseRoot(root, parsedRegions, ref parsedGlobal, warnings);

            Regions.Clear();
            foreach (KeyValuePair<string, WeatherSpatialRegionRules> pair in parsedRegions)
            {
                Regions[pair.Key] = pair.Value;
            }
            _globalDefault = parsedGlobal;
            ParseWarnings.Clear();
            ParseWarnings.AddRange(warnings);
            FatalLoadError = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ParseRoot(
        Dictionary<string, object> root,
        Dictionary<string, WeatherSpatialRegionRules> regions,
        ref WeatherSpatialRule globalDefault,
        List<string> warnings)
    {
        if (root.TryGetValue("version", out object versionObj) &&
            TryNumber(versionObj, out double version) &&
            (int)version != CurrentVersion)
        {
            warnings.Add($"WeatherSpatial.json version {(int)version} differs from supported version {CurrentVersion}; known fields will still be loaded.");
        }

        if (root.TryGetValue("globalDefault", out object globalObj))
        {
            if (TryParseRule(globalObj, out WeatherSpatialRule parsed) && parsed != WeatherSpatialRule.Inherit)
            {
                globalDefault = parsed;
            }
            else
            {
                warnings.Add("globalDefault is invalid; falling back to Allow.");
                globalDefault = WeatherSpatialRule.Allow;
            }
        }

        if (!root.TryGetValue("regions", out object regionsObj) ||
            regionsObj is not Dictionary<string, object> regionMap)
        {
            return;
        }

        foreach (KeyValuePair<string, object> regionPair in regionMap)
        {
            string regionId = NormalizeRegion(regionPair.Key);
            if (regionId.Length == 0 || regionPair.Value is not Dictionary<string, object> regionObject)
            {
                warnings.Add($"Skipped malformed region entry '{regionPair.Key}'.");
                continue;
            }

            WeatherSpatialRegionRules region = new();
            ParseRuleMap(regionObject, "familyDefaults", region.FamilyDefaults, warnings, regionId);
            ParseRuleMap(regionObject, "weatherDefaults", region.WeatherDefaults, warnings, regionId);

            if (regionObject.TryGetValue("rooms", out object roomsObj) &&
                roomsObj is Dictionary<string, object> roomMap)
            {
                foreach (KeyValuePair<string, object> roomPair in roomMap)
                {
                    string roomName = (roomPair.Key ?? string.Empty).Trim();
                    if (roomName.Length == 0 || roomPair.Value is not Dictionary<string, object> roomObject)
                    {
                        warnings.Add($"{regionId}: skipped malformed room entry '{roomPair.Key}'.");
                        continue;
                    }
                    WeatherSpatialRoomRules room = new();
                    ParseRuleMap(roomObject, "families", room.Families, warnings, regionId + "/" + roomName);
                    ParseRuleMap(roomObject, "weather", room.Weather, warnings, regionId + "/" + roomName);
                    if (!room.IsEmpty)
                    {
                        region.Rooms[roomName] = room;
                    }
                }
            }

            if (!region.IsEmpty)
            {
                regions[regionId] = region;
            }
        }
    }

    private static void ParseRuleMap(
        Dictionary<string, object> owner,
        string field,
        Dictionary<string, WeatherSpatialRule> destination,
        List<string> warnings,
        string scope)
    {
        if (!owner.TryGetValue(field, out object mapObj) || mapObj == null)
        {
            return;
        }
        if (mapObj is not Dictionary<string, object> map)
        {
            warnings.Add($"{scope}: '{field}' is not an object and was ignored.");
            return;
        }

        foreach (KeyValuePair<string, object> pair in map)
        {
            string key = (pair.Key ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                continue;
            }
            if (!TryParseRule(pair.Value, out WeatherSpatialRule rule))
            {
                warnings.Add($"{scope}: rule '{field}/{key}' has invalid value and was ignored.");
                continue;
            }
            if (rule != WeatherSpatialRule.Inherit)
            {
                destination[key] = rule;
            }
        }
    }

    private static Dictionary<string, object> BuildJsonRoot()
    {
        Dictionary<string, object> root = new()
        {
            ["version"] = CurrentVersion,
            ["globalDefault"] = RuleText(_globalDefault)
        };

        Dictionary<string, object> regionsObject = new();
        foreach (string regionId in SortedKeys(Regions))
        {
            WeatherSpatialRegionRules region = Regions[regionId];
            Dictionary<string, object> regionObject = new();
            Dictionary<string, object> familyDefaults = BuildRuleMap(region.FamilyDefaults);
            Dictionary<string, object> weatherDefaults = BuildRuleMap(region.WeatherDefaults);
            if (familyDefaults.Count > 0)
            {
                regionObject["familyDefaults"] = familyDefaults;
            }
            if (weatherDefaults.Count > 0)
            {
                regionObject["weatherDefaults"] = weatherDefaults;
            }

            Dictionary<string, object> roomsObject = new();
            foreach (string roomName in SortedKeys(region.Rooms))
            {
                WeatherSpatialRoomRules room = region.Rooms[roomName];
                Dictionary<string, object> roomObject = new();
                Dictionary<string, object> families = BuildRuleMap(room.Families);
                Dictionary<string, object> weather = BuildRuleMap(room.Weather);
                if (families.Count > 0)
                {
                    roomObject["families"] = families;
                }
                if (weather.Count > 0)
                {
                    roomObject["weather"] = weather;
                }
                if (roomObject.Count > 0)
                {
                    roomsObject[roomName] = roomObject;
                }
            }
            if (roomsObject.Count > 0)
            {
                regionObject["rooms"] = roomsObject;
            }
            if (regionObject.Count > 0)
            {
                regionsObject[regionId] = regionObject;
            }
        }
        root["regions"] = regionsObject;
        return root;
    }

    private static Dictionary<string, object> BuildRuleMap(Dictionary<string, WeatherSpatialRule> source)
    {
        Dictionary<string, object> result = new();
        foreach (string key in SortedKeys(source))
        {
            WeatherSpatialRule rule = source[key];
            if (rule != WeatherSpatialRule.Inherit)
            {
                result[key] = RuleText(rule);
            }
        }
        return result;
    }

    private static List<string> SortedKeys<T>(Dictionary<string, T> source)
    {
        List<string> keys = new(source.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        return keys;
    }

    private static bool TryParseRule(object value, out WeatherSpatialRule rule)
    {
        rule = WeatherSpatialRule.Inherit;
        string text = value as string;
        if (text == null)
        {
            return false;
        }
        if (text.Equals("Allow", StringComparison.OrdinalIgnoreCase))
        {
            rule = WeatherSpatialRule.Allow;
            return true;
        }
        if (text.Equals("Deny", StringComparison.OrdinalIgnoreCase))
        {
            rule = WeatherSpatialRule.Deny;
            return true;
        }
        if (text.Equals("Inherit", StringComparison.OrdinalIgnoreCase))
        {
            rule = WeatherSpatialRule.Inherit;
            return true;
        }
        return false;
    }

    private static string RuleText(WeatherSpatialRule rule)
    {
        return rule == WeatherSpatialRule.Deny
            ? "Deny"
            : rule == WeatherSpatialRule.Allow
                ? "Allow"
                : "Inherit";
    }

    private static bool TryNumber(object value, out double number)
    {
        switch (value)
        {
            case long l:
                number = l;
                return true;
            case int i:
                number = i;
                return true;
            case double d:
                number = d;
                return true;
            case float f:
                number = f;
                return true;
            default:
                return double.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out number);
        }
    }

    private static WeatherSpatialRule GetRule(
        Dictionary<string, WeatherSpatialRule> source,
        string key)
    {
        return !string.IsNullOrEmpty(key) && source.TryGetValue(key, out WeatherSpatialRule rule)
            ? rule
            : WeatherSpatialRule.Inherit;
    }

    private static void SetRule(
        Dictionary<string, WeatherSpatialRule> destination,
        string key,
        WeatherSpatialRule rule)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        if (rule == WeatherSpatialRule.Inherit)
        {
            destination.Remove(key);
        }
        else
        {
            destination[key] = rule;
        }
    }

    private static bool TryGetRegion(string regionId, out WeatherSpatialRegionRules region)
    {
        return Regions.TryGetValue(NormalizeRegion(regionId), out region);
    }

    private static bool TryGetRoom(
        string regionId,
        string roomName,
        out WeatherSpatialRoomRules room)
    {
        room = null;
        return TryGetRegion(regionId, out WeatherSpatialRegionRules region) &&
               region.Rooms.TryGetValue((roomName ?? string.Empty).Trim(), out room);
    }

    private static WeatherSpatialRegionRules GetOrCreateRegion(string regionId)
    {
        if (!Regions.TryGetValue(regionId, out WeatherSpatialRegionRules region))
        {
            region = new WeatherSpatialRegionRules();
            Regions[regionId] = region;
        }
        return region;
    }

    private static void TrimRegion(string regionId, WeatherSpatialRegionRules region)
    {
        if (region.IsEmpty)
        {
            Regions.Remove(regionId);
        }
    }

    private static string NormalizeRegion(string regionId) =>
        (regionId ?? string.Empty).Trim().ToUpperInvariant();

    private static string StripComment(string line)
    {
        int comment = (line ?? string.Empty).IndexOf("//", StringComparison.Ordinal);
        return comment >= 0 ? line.Substring(0, comment) : line ?? string.Empty;
    }

    private static string ResolvePath(bool forSave)
    {
        try
        {
            if (ModManager.ActiveMods != null)
            {
                for (int i = 0; i < ModManager.ActiveMods.Count; i++)
                {
                    ModManager.Mod mod = ModManager.ActiveMods[i];
                    if (mod == null ||
                        !string.Equals(mod.id, Plugin.RainWorldModId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] roots =
                    {
                        mod.path,
                        mod.NewestPath,
                        mod.TargetedPath,
                        mod.basePath
                    };
                    string firstCandidate = null;
                    for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                    {
                        string root = roots[rootIndex];
                        if (string.IsNullOrEmpty(root))
                        {
                            continue;
                        }
                        string candidate = Path.Combine(root, "world", FileName);
                        firstCandidate ??= candidate;
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    if (forSave && !string.IsNullOrEmpty(firstCandidate))
                    {
                        return firstCandidate;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("DryCycle weather spatial direct path lookup failed: " + ex.Message);
        }

        string[] assets = { "World/" + FileName, "world/" + FileName };
        for (int i = 0; i < assets.Length; i++)
        {
            try
            {
                string resolved = AssetManager.ResolveFilePath(assets[i]);
                if (!string.IsNullOrEmpty(resolved) && (forSave || File.Exists(resolved)))
                {
                    return resolved;
                }
            }
            catch
            {
                // Keep probing.
            }
        }
        return null;
    }

    private static void RememberWriteTime(string path)
    {
        try
        {
            _lastWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        }
        catch
        {
            _lastWriteUtc = default;
        }
    }

    private static void ArchiveInvalid(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileName(path);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string archive = Path.Combine(directory, name + ".invalid-" + stamp + ".bak");
        int suffix = 1;
        while (File.Exists(archive))
        {
            archive = Path.Combine(directory, name + ".invalid-" + stamp + "-" + suffix + ".bak");
            suffix++;
        }
        File.Move(path, archive);
    }
}
