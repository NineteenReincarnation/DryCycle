using System;
using System.Collections.Generic;
using System.IO;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal sealed class WeatherSpatialRegionFamilySchedule
{
    internal string FamilyId { get; }
    internal bool Enabled { get; set; }
    internal float ChancePercent { get; set; }
    internal readonly Dictionary<string, bool> SubWeatherEnabled =
        new(StringComparer.OrdinalIgnoreCase);

    internal WeatherSpatialRegionFamilySchedule(string familyId, bool enabled, float chancePercent)
    {
        FamilyId = familyId ?? string.Empty;
        Enabled = enabled;
        ChancePercent = WeatherSpatialScheduleWeather.ClampChance(chancePercent);
    }
}

internal static partial class WeatherSpatialRegistry
{
    private static readonly Dictionary<string, Dictionary<string, WeatherSpatialRegionFamilySchedule>>
        RegionFamilySchedules = new(StringComparer.OrdinalIgnoreCase);

    internal static bool TryGetFamilySchedule(
        string regionId,
        string familyId,
        out bool enabled,
        out float chancePercent)
    {
        enabled = false;
        chancePercent = 0f;
        EnsureLegacyScheduleMigration();

        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0 ||
            !WeatherSpatialCatalog.TryGetFamily(familyId, out WeatherSpatialFamily family))
        {
            return false;
        }

        EnsureLegacyFamilySchedule(regionKey);
        if (!RegionFamilySchedules.TryGetValue(
                regionKey,
                out Dictionary<string, WeatherSpatialRegionFamilySchedule> families) ||
            !families.TryGetValue(family.Id, out WeatherSpatialRegionFamilySchedule setting))
        {
            return false;
        }

        enabled = setting.Enabled;
        chancePercent = setting.ChancePercent;
        return true;
    }

    internal static bool SetFamilyScheduleEnabled(string regionId, string familyId, bool enabled)
    {
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0 ||
            !WeatherSpatialCatalog.TryGetFamily(familyId, out WeatherSpatialFamily family))
        {
            return false;
        }

        EnsureLegacyScheduleMigration();
        EnsureLegacyFamilySchedule(regionKey);

        if (!RegionFamilySchedules.TryGetValue(
                regionKey,
                out Dictionary<string, WeatherSpatialRegionFamilySchedule> families))
        {
            if (!enabled)
            {
                return true;
            }
            families = new Dictionary<string, WeatherSpatialRegionFamilySchedule>(StringComparer.OrdinalIgnoreCase);
            RegionFamilySchedules[regionKey] = families;
        }

        if (!families.TryGetValue(family.Id, out WeatherSpatialRegionFamilySchedule setting))
        {
            if (!enabled)
            {
                return true;
            }
            setting = new WeatherSpatialRegionFamilySchedule(family.Id, true, 100f);
            families[family.Id] = setting;
            Dirty = true;
            WeatherScheduleCacheInvalidation.InvalidateAll();
            return true;
        }

        if (setting.Enabled == enabled)
        {
            return true;
        }

        setting.Enabled = enabled;
        Dirty = true;
        WeatherScheduleCacheInvalidation.InvalidateAll();
        return true;
    }

    internal static bool SetFamilyScheduleChance(string regionId, string familyId, float chancePercent)
    {
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0 ||
            !WeatherSpatialCatalog.TryGetFamily(familyId, out WeatherSpatialFamily family))
        {
            return false;
        }

        EnsureLegacyScheduleMigration();
        EnsureLegacyFamilySchedule(regionKey);
        chancePercent = WeatherSpatialScheduleWeather.ClampChance(chancePercent);

        WeatherSpatialRegionFamilySchedule setting = EnsureFamilyScheduleState(
            regionKey,
            family,
            enabledWhenCreated: false,
            defaultChance: chancePercent);
        if (setting == null)
        {
            return false;
        }

        bool changed = Math.Abs(setting.ChancePercent - chancePercent) >= 0.001f;
        setting.ChancePercent = chancePercent;
        SynchronizeGroupedFamilyChance(regionKey, family.Id, chancePercent);
        if (changed)
        {
            Dirty = true;
            WeatherScheduleCacheInvalidation.InvalidateAll();
        }
        return true;
    }

    internal static bool TryGetSubWeatherSchedule(
        string regionId,
        WeatherScheduleEventKind kind,
        string weatherId,
        out bool enabled,
        out float chancePercent)
    {
        enabled = false;
        chancePercent = 0f;
        EnsureLegacyScheduleMigration();

        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0 ||
            !WeatherSpatialCatalog.TryGetFamily(kind, weatherId, out WeatherSpatialFamily family))
        {
            return false;
        }

        EnsureLegacyFamilySchedule(regionKey);
        string key = WeatherSpatialCatalog.WeatherKey(kind, weatherId);
        if (!RegionFamilySchedules.TryGetValue(
                regionKey,
                out Dictionary<string, WeatherSpatialRegionFamilySchedule> families) ||
            !families.TryGetValue(family.Id, out WeatherSpatialRegionFamilySchedule familySetting) ||
            !familySetting.SubWeatherEnabled.TryGetValue(key, out enabled))
        {
            return false;
        }

        WeatherSpatialTarget target = new(kind, weatherId, weatherId);
        TryGetSubWeatherChance(regionKey, target, out chancePercent);
        return true;
    }

    internal static bool SetSubWeatherScheduleEnabled(
        string regionId,
        WeatherScheduleEventKind kind,
        string weatherId,
        bool enabled)
    {
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0 ||
            !WeatherSpatialCatalog.TryGetFamily(kind, weatherId, out WeatherSpatialFamily family))
        {
            return false;
        }

        EnsureLegacyScheduleMigration();
        EnsureLegacyFamilySchedule(regionKey);
        WeatherSpatialRegionFamilySchedule familySetting = EnsureFamilyScheduleState(
            regionKey,
            family,
            enabledWhenCreated: false,
            defaultChance: 100f);
        if (familySetting == null)
        {
            return false;
        }

        string key = WeatherSpatialCatalog.WeatherKey(kind, weatherId);
        bool changed = !familySetting.SubWeatherEnabled.TryGetValue(key, out bool previous) ||
                       previous != enabled;
        familySetting.SubWeatherEnabled[key] = enabled;

        if (enabled)
        {
            WeatherSpatialTarget target = new(kind, weatherId, weatherId);
            if (!TryGetSubWeatherChance(regionKey, target, out _))
            {
                SetSubWeatherChance(regionKey, target, 100f);
            }
        }

        if (changed)
        {
            Dirty = true;
        }
        WeatherScheduleCacheInvalidation.InvalidateAll();
        return true;
    }

    internal static bool SetSubWeatherScheduleChance(
        string regionId,
        WeatherScheduleEventKind kind,
        string weatherId,
        float chancePercent)
    {
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0 ||
            !WeatherSpatialCatalog.TryGetFamily(kind, weatherId, out WeatherSpatialFamily family))
        {
            return false;
        }

        EnsureLegacyScheduleMigration();
        EnsureLegacyFamilySchedule(regionKey);
        WeatherSpatialRegionFamilySchedule familySetting = EnsureFamilyScheduleState(
            regionKey,
            family,
            enabledWhenCreated: false,
            defaultChance: 100f);
        if (familySetting == null)
        {
            return false;
        }

        string key = WeatherSpatialCatalog.WeatherKey(kind, weatherId);
        if (!familySetting.SubWeatherEnabled.ContainsKey(key))
        {
            familySetting.SubWeatherEnabled[key] = false;
        }

        WeatherSpatialTarget target = new(kind, weatherId, weatherId);
        return SetSubWeatherChance(regionKey, target, chancePercent);
    }

    private static WeatherSpatialRegionFamilySchedule EnsureFamilyScheduleStateForChildEditing(
        string regionKey,
        WeatherSpatialFamily family)
    {
        return EnsureFamilyScheduleState(
            regionKey,
            family,
            enabledWhenCreated: false,
            defaultChance: 100f);
    }

    private static WeatherSpatialRegionFamilySchedule EnsureFamilyScheduleState(
        string regionKey,
        WeatherSpatialFamily family,
        bool enabledWhenCreated,
        float defaultChance)
    {
        if (string.IsNullOrEmpty(regionKey) || family == null)
        {
            return null;
        }

        EnsureLegacyFamilySchedule(regionKey);
        if (!RegionFamilySchedules.TryGetValue(
                regionKey,
                out Dictionary<string, WeatherSpatialRegionFamilySchedule> families))
        {
            families = new Dictionary<string, WeatherSpatialRegionFamilySchedule>(StringComparer.OrdinalIgnoreCase);
            RegionFamilySchedules[regionKey] = families;
        }

        if (!families.TryGetValue(family.Id, out WeatherSpatialRegionFamilySchedule setting))
        {
            setting = new WeatherSpatialRegionFamilySchedule(
                family.Id,
                enabledWhenCreated,
                defaultChance);
            families[family.Id] = setting;
        }
        return setting;
    }

    private static float StoredFamilyChanceOrDefault(
        string regionKey,
        string familyId,
        float fallback = 100f)
    {
        if (RegionFamilySchedules.TryGetValue(
                regionKey,
                out Dictionary<string, WeatherSpatialRegionFamilySchedule> families) &&
            families.TryGetValue(familyId, out WeatherSpatialRegionFamilySchedule setting))
        {
            return setting.ChancePercent;
        }
        return WeatherSpatialScheduleWeather.ClampChance(fallback);
    }

    private static void SynchronizeGroupedFamilyChance(
        string regionKey,
        string familyId,
        float chancePercent)
    {
        if (RegionSchedules.TryGetValue(regionKey, out WeatherSpatialRegionSchedule schedule) &&
            schedule.Weather.TryGetValue(familyId, out WeatherSpatialScheduleWeather grouped) &&
            grouped.IsFamily)
        {
            grouped.ChancePercent = WeatherSpatialScheduleWeather.ClampChance(chancePercent);
        }
    }

    private static void SynchronizeGroupedFamilyChances(string regionKey)
    {
        if (!RegionFamilySchedules.TryGetValue(
                regionKey,
                out Dictionary<string, WeatherSpatialRegionFamilySchedule> families))
        {
            return;
        }

        foreach (WeatherSpatialRegionFamilySchedule setting in families.Values)
        {
            SynchronizeGroupedFamilyChance(regionKey, setting.FamilyId, setting.ChancePercent);
        }
    }

    private static bool ClearRegionFamilyScheduleState(string regionId)
    {
        return RegionFamilySchedules.Remove(NormalizeRegion(regionId));
    }

    private static void EnsureLegacyFamilySchedule(string regionKey)
    {
        if (string.IsNullOrEmpty(regionKey) ||
            !RegionSchedules.TryGetValue(regionKey, out WeatherSpatialRegionSchedule schedule))
        {
            return;
        }

        if (!RegionFamilySchedules.TryGetValue(
                regionKey,
                out Dictionary<string, WeatherSpatialRegionFamilySchedule> families))
        {
            families = new Dictionary<string, WeatherSpatialRegionFamilySchedule>(StringComparer.OrdinalIgnoreCase);
            RegionFamilySchedules[regionKey] = families;
        }

        for (int familyIndex = 0; familyIndex < WeatherSpatialCatalog.AllFamilies.Count; familyIndex++)
        {
            WeatherSpatialFamily family = WeatherSpatialCatalog.AllFamilies[familyIndex];
            bool configured = false;
            float familyChance = 100f;
            if (schedule.Weather.TryGetValue(family.Id, out WeatherSpatialScheduleWeather grouped) &&
                grouped.IsFamily)
            {
                configured = true;
                familyChance = grouped.ChancePercent;
            }
            else
            {
                for (int memberIndex = 0; memberIndex < family.Members.Count; memberIndex++)
                {
                    WeatherSpatialMember member = family.Members[memberIndex];
                    if (schedule.Contains(member.Kind, member.Id))
                    {
                        configured = true;
                        break;
                    }
                }
            }

            if (!families.TryGetValue(family.Id, out WeatherSpatialRegionFamilySchedule setting))
            {
                if (!configured)
                {
                    continue;
                }
                setting = new WeatherSpatialRegionFamilySchedule(family.Id, true, familyChance);
                families[family.Id] = setting;
            }

            for (int memberIndex = 0; memberIndex < family.Members.Count; memberIndex++)
            {
                WeatherSpatialMember member = family.Members[memberIndex];
                if (!setting.SubWeatherEnabled.ContainsKey(member.Key) &&
                    schedule.Contains(member.Kind, member.Id))
                {
                    setting.SubWeatherEnabled[member.Key] = true;
                }
            }
        }

        if (families.Count == 0)
        {
            RegionFamilySchedules.Remove(regionKey);
        }
    }

    private static void LoadRegionFamilyScheduleState(string path)
    {
        RegionFamilySchedules.Clear();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            object parsed = Json.Deserialize(File.ReadAllText(path));
            if (parsed is Dictionary<string, object> root &&
                root.TryGetValue("regions", out object regionsObj) &&
                regionsObj is Dictionary<string, object> regions)
            {
                foreach (KeyValuePair<string, object> regionPair in regions)
                {
                    string regionKey = NormalizeRegion(regionPair.Key);
                    if (regionKey.Length == 0 ||
                        regionPair.Value is not Dictionary<string, object> regionObject ||
                        !regionObject.TryGetValue("schedule", out object scheduleObj) ||
                        scheduleObj is not Dictionary<string, object> scheduleMap ||
                        !scheduleMap.TryGetValue("families", out object familyObj) ||
                        familyObj is not Dictionary<string, object> familyMap)
                    {
                        continue;
                    }

                    Dictionary<string, WeatherSpatialRegionFamilySchedule> loaded =
                        new(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, object> familyPair in familyMap)
                    {
                        if (!WeatherSpatialCatalog.TryGetFamily(familyPair.Key, out WeatherSpatialFamily family) ||
                            familyPair.Value is not Dictionary<string, object> settingMap ||
                            !TryReadEnabled(settingMap, out bool enabled) ||
                            !settingMap.TryGetValue("chance", out object chanceObj) ||
                            !TryNumber(chanceObj, out double chance) ||
                            double.IsNaN(chance) ||
                            double.IsInfinity(chance) ||
                            chance < 0d ||
                            chance > 100d)
                        {
                            ParseWarnings.Add(
                                $"{regionKey}: malformed schedule/families entry '{familyPair.Key}' was ignored.");
                            continue;
                        }

                        WeatherSpatialRegionFamilySchedule setting = new(
                            family.Id,
                            enabled,
                            (float)chance);

                        if (settingMap.TryGetValue("subWeather", out object subObj) && subObj != null)
                        {
                            if (subObj is Dictionary<string, object> subMap)
                            {
                                foreach (KeyValuePair<string, object> subPair in subMap)
                                {
                                    if (!TryParseFamilySubWeatherKey(
                                            family,
                                            subPair.Key,
                                            out string canonicalKey) ||
                                        !TryReadEnabledValue(subPair.Value, out bool subEnabled))
                                    {
                                        ParseWarnings.Add(
                                            $"{regionKey}: malformed schedule/families/{family.Id}/subWeather entry '{subPair.Key}' was ignored.");
                                        continue;
                                    }
                                    setting.SubWeatherEnabled[canonicalKey] = subEnabled;
                                }
                            }
                            else
                            {
                                ParseWarnings.Add(
                                    $"{regionKey}: schedule/families/{family.Id}/subWeather is not an object and was ignored.");
                            }
                        }

                        loaded[family.Id] = setting;
                    }

                    if (loaded.Count > 0)
                    {
                        RegionFamilySchedules[regionKey] = loaded;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ParseWarnings.Add("Could not read schedule/families state: " + ex.Message);
        }

        HashSet<string> relevantRegions = new(StringComparer.OrdinalIgnoreCase);
        foreach (string regionKey in RegionSchedules.Keys)
        {
            relevantRegions.Add(regionKey);
        }
        foreach (string regionKey in RegionFamilySchedules.Keys)
        {
            relevantRegions.Add(regionKey);
        }

        foreach (string regionKey in relevantRegions)
        {
            EnsureLegacyFamilySchedule(regionKey);
            SynchronizeGroupedFamilyChances(regionKey);
        }
    }

    private static bool TryMergeRegionFamilyScheduleState(string baseJson, out string mergedJson)
    {
        mergedJson = baseJson ?? string.Empty;
        try
        {
            object parsed = Json.Deserialize(mergedJson);
            if (parsed is not Dictionary<string, object> root)
            {
                return false;
            }

            if (!root.TryGetValue("regions", out object regionsObj) ||
                regionsObj is not Dictionary<string, object> regions)
            {
                regions = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                root["regions"] = regions;
            }

            List<string> regionKeys = new(RegionFamilySchedules.Keys);
            regionKeys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int regionIndex = 0; regionIndex < regionKeys.Count; regionIndex++)
            {
                string regionKey = regionKeys[regionIndex];
                Dictionary<string, WeatherSpatialRegionFamilySchedule> configured = RegionFamilySchedules[regionKey];
                if (configured == null || configured.Count == 0)
                {
                    continue;
                }

                if (!regions.TryGetValue(regionKey, out object regionObj) ||
                    regionObj is not Dictionary<string, object> regionMap)
                {
                    regionMap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    regions[regionKey] = regionMap;
                }
                if (!regionMap.TryGetValue("schedule", out object scheduleObj) ||
                    scheduleObj is not Dictionary<string, object> scheduleMap)
                {
                    scheduleMap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    regionMap["schedule"] = scheduleMap;
                }

                Dictionary<string, object> familyMap = new(StringComparer.OrdinalIgnoreCase);
                List<string> familyKeys = new(configured.Keys);
                familyKeys.Sort(StringComparer.OrdinalIgnoreCase);
                for (int familyIndex = 0; familyIndex < familyKeys.Count; familyIndex++)
                {
                    WeatherSpatialRegionFamilySchedule setting = configured[familyKeys[familyIndex]];
                    Dictionary<string, object> entry = new()
                    {
                        ["enabled"] = setting.Enabled,
                        ["chance"] = setting.ChancePercent
                    };

                    if (setting.SubWeatherEnabled.Count > 0)
                    {
                        Dictionary<string, object> subWeather = new(StringComparer.OrdinalIgnoreCase);
                        List<string> subKeys = new(setting.SubWeatherEnabled.Keys);
                        subKeys.Sort(StringComparer.OrdinalIgnoreCase);
                        for (int subIndex = 0; subIndex < subKeys.Count; subIndex++)
                        {
                            string key = subKeys[subIndex];
                            subWeather[key] = setting.SubWeatherEnabled[key];
                        }
                        entry["subWeather"] = subWeather;
                    }

                    familyMap[setting.FamilyId] = entry;
                }
                scheduleMap["families"] = familyMap;
            }

            mergedJson = Json.Serialize(root);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError("DryCycle schedule/families JSON merge failed: " + ex);
            return false;
        }
    }

    private static bool TryParseFamilySubWeatherKey(
        WeatherSpatialFamily family,
        string rawKey,
        out string canonicalKey)
    {
        canonicalKey = null;
        if (family == null ||
            !WeatherSpatialCatalog.TryParseWeatherKey(
                rawKey,
                out WeatherScheduleEventKind kind,
                out string id) ||
            !WeatherSpatialCatalog.TryGetFamily(kind, id, out WeatherSpatialFamily actualFamily) ||
            !string.Equals(actualFamily.Id, family.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        canonicalKey = WeatherSpatialCatalog.WeatherKey(kind, id);
        return true;
    }

    private static bool TryReadEnabled(Dictionary<string, object> map, out bool enabled)
    {
        enabled = false;
        return map != null &&
               map.TryGetValue("enabled", out object value) &&
               TryReadEnabledValue(value, out enabled);
    }

    private static bool TryReadEnabledValue(object value, out bool enabled)
    {
        enabled = false;
        if (value is bool boolean)
        {
            enabled = boolean;
            return true;
        }
        if (value is string text && bool.TryParse(text, out boolean))
        {
            enabled = boolean;
            return true;
        }
        return false;
    }
}
