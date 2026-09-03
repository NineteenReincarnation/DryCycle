using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialRegistry
{
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
            Dictionary<string, WeatherSpatialRegionSchedule> parsedSchedules =
                new(StringComparer.OrdinalIgnoreCase);
            WeatherSpatialRule parsedGlobal = WeatherSpatialRule.Deny;
            List<string> warnings = new();
            ParseRoot(root, parsedRegions, parsedSchedules, ref parsedGlobal, warnings);

            // Migrate the exact untouched seed shipped by the first Weather Zones
            // implementation. Existing empty seeds must follow the current Forbidden
            // default instead of silently enabling every weather.
            if (IsLegacyAllowSeed(root, parsedRegions, parsedSchedules, parsedGlobal))
            {
                parsedGlobal = WeatherSpatialRule.Deny;
                warnings.Add("Migrated legacy empty globalDefault Allow seed to Forbidden; save WeatherSpatial to persist the new default.");
            }

            Regions.Clear();
            foreach (KeyValuePair<string, WeatherSpatialRegionRules> pair in parsedRegions)
            {
                Regions[pair.Key] = pair.Value;
            }

            RegionSchedules.Clear();
            foreach (KeyValuePair<string, WeatherSpatialRegionSchedule> pair in parsedSchedules)
            {
                RegionSchedules[pair.Key] = pair.Value;
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

    private static bool IsLegacyAllowSeed(
        Dictionary<string, object> root,
        Dictionary<string, WeatherSpatialRegionRules> parsedRegions,
        Dictionary<string, WeatherSpatialRegionSchedule> parsedSchedules,
        WeatherSpatialRule parsedGlobal)
    {
        if (root == null || parsedRegions == null || parsedSchedules == null ||
            parsedGlobal != WeatherSpatialRule.Allow ||
            parsedRegions.Count != 0 ||
            parsedSchedules.Count != 0 ||
            root.Count != 3)
        {
            return false;
        }

        if (!root.TryGetValue("globalDefault", out object globalObj) ||
            globalObj is not string globalText ||
            !globalText.Equals("Allow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return root.ContainsKey("version") &&
               root.TryGetValue("regions", out object regionsObj) &&
               regionsObj is Dictionary<string, object> regionMap &&
               regionMap.Count == 0;
    }

    private static void ParseRoot(
        Dictionary<string, object> root,
        Dictionary<string, WeatherSpatialRegionRules> regions,
        Dictionary<string, WeatherSpatialRegionSchedule> schedules,
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
                warnings.Add("globalDefault is invalid; falling back to Forbidden.");
                globalDefault = WeatherSpatialRule.Deny;
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

            WeatherSpatialRegionSchedule schedule = new();
            ParseSchedule(regionObject, schedule, warnings, regionId);
            if (!schedule.IsEmpty)
            {
                schedules[regionId] = schedule;
            }
        }
    }

    private static void ParseSchedule(
        Dictionary<string, object> regionObject,
        WeatherSpatialRegionSchedule schedule,
        List<string> warnings,
        string regionId)
    {
        if (!regionObject.TryGetValue("schedule", out object scheduleObj) || scheduleObj == null)
        {
            return;
        }
        if (scheduleObj is not Dictionary<string, object> scheduleMap)
        {
            warnings.Add($"{regionId}: 'schedule' is not an object and was ignored.");
            return;
        }

        if (scheduleMap.TryGetValue("weather", out object weatherObj) && weatherObj != null)
        {
            if (weatherObj is Dictionary<string, object> weatherMap)
            {
                foreach (KeyValuePair<string, object> pair in weatherMap)
                {
                    ParseScheduledWeather(pair.Key, pair.Value, schedule, warnings, regionId);
                }
            }
            else
            {
                warnings.Add($"{regionId}: 'schedule/weather' is not an object and was ignored.");
            }
        }

        if (scheduleMap.TryGetValue("dangerTypes", out object dangerObj) && dangerObj != null)
        {
            if (dangerObj is Dictionary<string, object> dangerMap)
            {
                foreach (KeyValuePair<string, object> pair in dangerMap)
                {
                    string id = (pair.Key ?? string.Empty).Trim();
                    if (id.Length == 0 || !TryChance(pair.Value, out float chance))
                    {
                        warnings.Add($"{regionId}: malformed schedule dangerTypes entry '{pair.Key}' was ignored; expected a 0-100 number.");
                        continue;
                    }
                    schedule.DangerTypes[WeatherSpatialCatalog.CanonicalWeatherId(
                        WeatherScheduleEventKind.DangerType,
                        id)] = chance;
                }
            }
            else
            {
                warnings.Add($"{regionId}: 'schedule/dangerTypes' is not an object and was ignored.");
            }
        }
    }

    private static void ParseScheduledWeather(
        string rawId,
        object value,
        WeatherSpatialRegionSchedule schedule,
        List<string> warnings,
        string regionId)
    {
        string id = (rawId ?? string.Empty).Trim();
        if (id.Length == 0)
        {
            return;
        }

        if (TryChance(value, out float compactChance))
        {
            string compactId = WeatherSpatialCatalog.CanonicalWeatherId(
                WeatherScheduleEventKind.Weather,
                id);
            schedule.Weather[compactId] = new WeatherSpatialScheduleWeather(compactId, compactChance);
            return;
        }

        if (value is not Dictionary<string, object> entryMap)
        {
            warnings.Add($"{regionId}: malformed schedule weather entry '{id}' was ignored; expected a number or object.");
            return;
        }

        if (!entryMap.TryGetValue("chance", out object chanceObj) ||
            !TryChance(chanceObj, out float chance))
        {
            warnings.Add($"{regionId}: schedule weather '{id}' has no valid 0-100 'chance' and was ignored.");
            return;
        }

        string canonicalId = id;
        if (WeatherSpatialCatalog.TryGetFamily(id, out WeatherSpatialFamily family))
        {
            canonicalId = family.Id;
        }
        else if (WeatherSpatialCatalog.IsKnownWeather(WeatherScheduleEventKind.Weather, id))
        {
            canonicalId = WeatherSpatialCatalog.CanonicalWeatherId(
                WeatherScheduleEventKind.Weather,
                id);
        }

        WeatherSpatialScheduleWeather configured = new(canonicalId, chance);
        if (entryMap.TryGetValue("variants", out object variantsObj) && variantsObj != null)
        {
            if (variantsObj is Dictionary<string, object> variantsMap)
            {
                foreach (KeyValuePair<string, object> variantPair in variantsMap)
                {
                    string variantId = (variantPair.Key ?? string.Empty).Trim();
                    if (variantId.Length == 0 || !TryChance(variantPair.Value, out float variantChance))
                    {
                        warnings.Add($"{regionId}: malformed variant '{id}/{variantPair.Key}' was ignored; expected a 0-100 number.");
                        continue;
                    }
                    variantId = WeatherSpatialCatalog.CanonicalWeatherId(
                        WeatherScheduleEventKind.Weather,
                        variantId);
                    configured.Variants[variantId] = variantChance;
                }
            }
            else
            {
                warnings.Add($"{regionId}: schedule weather '{id}' has a non-object 'variants' field; variants were ignored.");
            }
        }

        schedule.Weather[canonicalId] = configured;
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
        HashSet<string> regionIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (string regionId in Regions.Keys)
        {
            regionIds.Add(regionId);
        }
        foreach (string regionId in RegionSchedules.Keys)
        {
            regionIds.Add(regionId);
        }

        List<string> sortedRegionIds = new(regionIds);
        sortedRegionIds.Sort(StringComparer.OrdinalIgnoreCase);
        for (int regionIndex = 0; regionIndex < sortedRegionIds.Count; regionIndex++)
        {
            string regionId = sortedRegionIds[regionIndex];
            Dictionary<string, object> regionObject = new();

            if (RegionSchedules.TryGetValue(regionId, out WeatherSpatialRegionSchedule schedule) &&
                !schedule.IsEmpty)
            {
                regionObject["schedule"] = BuildSchedule(schedule);
            }

            if (Regions.TryGetValue(regionId, out WeatherSpatialRegionRules region))
            {
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
            }

            if (regionObject.Count > 0)
            {
                regionsObject[regionId] = regionObject;
            }
        }

        root["regions"] = regionsObject;
        return root;
    }

    private static Dictionary<string, object> BuildSchedule(WeatherSpatialRegionSchedule schedule)
    {
        Dictionary<string, object> result = new();
        Dictionary<string, object> weather = new();
        foreach (string weatherId in SortedKeys(schedule.Weather))
        {
            WeatherSpatialScheduleWeather configured = schedule.Weather[weatherId];
            Dictionary<string, object> entry = new()
            {
                ["chance"] = configured.ChancePercent
            };
            if (configured.Variants.Count > 0)
            {
                Dictionary<string, object> variants = new();
                foreach (string variantId in SortedKeys(configured.Variants))
                {
                    variants[variantId] = configured.Variants[variantId];
                }
                entry["variants"] = variants;
            }
            weather[configured.Id] = entry;
        }
        if (weather.Count > 0)
        {
            result["weather"] = weather;
        }

        Dictionary<string, object> danger = new();
        foreach (string dangerId in SortedKeys(schedule.DangerTypes))
        {
            danger[dangerId] = schedule.DangerTypes[dangerId];
        }
        if (danger.Count > 0)
        {
            result["dangerTypes"] = danger;
        }
        return result;
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
        if (text.Equals("Forbidden", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Deny", StringComparison.OrdinalIgnoreCase))
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
            ? "Forbidden"
            : rule == WeatherSpatialRule.Allow
                ? "Allow"
                : "Inherit";
    }

    private static bool TryChance(object value, out float chance)
    {
        chance = 0f;
        if (!TryNumber(value, out double number) ||
            double.IsNaN(number) ||
            double.IsInfinity(number) ||
            number < 0d ||
            number > 100d)
        {
            return false;
        }
        chance = (float)number;
        return true;
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
