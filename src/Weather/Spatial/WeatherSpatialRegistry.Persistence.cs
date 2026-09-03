using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DryCycle.Weather.Climate;
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
            WeatherSpatialRule parsedGlobal = WeatherSpatialRule.Deny;
            List<string> warnings = new();
            ParseRoot(root, parsedRegions, ref parsedGlobal, warnings);

            // Migrate the exact untouched seed shipped by the first Weather Zones
            // implementation. Without this, existing developer installs that already
            // contain { globalDefault: Allow, regions: {} } would keep enabling every
            // weather even though the new system default is Forbidden.
            if (IsLegacyAllowSeed(root, parsedRegions, parsedGlobal))
            {
                parsedGlobal = WeatherSpatialRule.Deny;
                warnings.Add("Migrated legacy empty globalDefault Allow seed to Forbidden; save WeatherSpatial to persist the new default.");
            }

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

    private static bool IsLegacyAllowSeed(
        Dictionary<string, object> root,
        Dictionary<string, WeatherSpatialRegionRules> parsedRegions,
        WeatherSpatialRule parsedGlobal)
    {
        if (root == null || parsedRegions == null ||
            parsedGlobal != WeatherSpatialRule.Allow ||
            parsedRegions.Count != 0 ||
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
        if (text.Equals("Forbidden", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Deny", StringComparison.OrdinalIgnoreCase))
        {
            // "Deny" is accepted only for backward compatibility with older
            // WeatherSpatial.json files. New saves always write "Forbidden".
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
