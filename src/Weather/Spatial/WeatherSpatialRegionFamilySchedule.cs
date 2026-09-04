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
        if (regionKey.Length == 0 || !WeatherSpatialCatalog.TryGetFamily(familyId, out WeatherSpatialFamily family))
        {
            return false;
        }

        EnsureLegacyFamilySchedule(regionKey);
        if (!RegionFamilySchedules.TryGetValue(regionKey, out Dictionary<string, WeatherSpatialRegionFamilySchedule> families) ||
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
        if (regionKey.Length == 0 || !WeatherSpatialCatalog.TryGetFamily(familyId, out WeatherSpatialFamily family))
        {
            return false;
        }

        EnsureLegacyScheduleMigration();
        EnsureLegacyFamilySchedule(regionKey);

        if (!RegionFamilySchedules.TryGetValue(regionKey, out Dictionary<string, WeatherSpatialRegionFamilySchedule> families))
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
            setting = new WeatherSpatialRegionFamilySchedule(family.Id, enabled: true, chancePercent: 100f);
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
        if (regionKey.Length == 0 || !WeatherSpatialCatalog.TryGetFamily(familyId, out WeatherSpatialFamily family))
        {
            return false;
        }

        EnsureLegacyScheduleMigration();
        EnsureLegacyFamilySchedule(regionKey);
        chancePercent = WeatherSpatialScheduleWeather.ClampChance(chancePercent);

        if (!RegionFamilySchedules.TryGetValue(regionKey, out Dictionary<string, WeatherSpatialRegionFamilySchedule> families))
        {
            families = new Dictionary<string, WeatherSpatialRegionFamilySchedule>(StringComparer.OrdinalIgnoreCase);
            RegionFamilySchedules[regionKey] = families;
        }

        if (!families.TryGetValue(family.Id, out WeatherSpatialRegionFamilySchedule setting))
        {
            // Editing a chance while the Family is NO must not implicitly enable it.
            setting = new WeatherSpatialRegionFamilySchedule(family.Id, enabled: false, chancePercent);
            families[family.Id] = setting;
            Dirty = true;
            WeatherScheduleCacheInvalidation.InvalidateAll();
            return true;
        }

        if (Math.Abs(setting.ChancePercent - chancePercent) < 0.001f)
        {
            return true;
        }

        setting.ChancePercent = chancePercent;
        Dirty = true;
        WeatherScheduleCacheInvalidation.InvalidateAll();
        return true;
    }

    private static bool ClearRegionFamilyScheduleState(string regionId)
    {
        return RegionFamilySchedules.Remove(NormalizeRegion(regionId));
    }

    private static void EnsureLegacyFamilySchedule(string regionKey)
    {
        if (string.IsNullOrEmpty(regionKey) || !RegionSchedules.TryGetValue(regionKey, out WeatherSpatialRegionSchedule schedule))
        {
            return;
        }

        if (!RegionFamilySchedules.TryGetValue(regionKey, out Dictionary<string, WeatherSpatialRegionFamilySchedule> families))
        {
            families = new Dictionary<string, WeatherSpatialRegionFamilySchedule>(StringComparer.OrdinalIgnoreCase);
            RegionFamilySchedules[regionKey] = families;
        }

        for (int i = 0; i < WeatherSpatialCatalog.AllFamilies.Count; i++)
        {
            WeatherSpatialFamily family = WeatherSpatialCatalog.AllFamilies[i];
            if (families.ContainsKey(family.Id))
            {
                continue;
            }

            bool configured = false;
            float chance = 100f;
            if (schedule.Weather.TryGetValue(family.Id, out WeatherSpatialScheduleWeather grouped) && grouped.IsFamily)
            {
                configured = true;
                chance = grouped.ChancePercent;
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

            if (configured)
            {
                families[family.Id] = new WeatherSpatialRegionFamilySchedule(
                    family.Id,
                    enabled: true,
                    chancePercent: chance);
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
                    if (regionKey.Length == 0 || regionPair.Value is not Dictionary<string, object> regionObject ||
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
                            !TryNumber(chanceObj, out double number) ||
                            double.IsNaN(number) || double.IsInfinity(number) || number < 0d || number > 100d)
                        {
                            ParseWarnings.Add($"{regionKey}: malformed schedule/families entry '{familyPair.Key}' was ignored.");
                            continue;
                        }

                        loaded[family.Id] = new WeatherSpatialRegionFamilySchedule(
                            family.Id,
                            enabled,
                            (float)number);
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

        List<string> legacyRegions = new(RegionSchedules.Keys);
        for (int i = 0; i < legacyRegions.Count; i++)
        {
            EnsureLegacyFamilySchedule(legacyRegions[i]);
        }
    }

    private static bool PersistRegionFamilyScheduleState(string path)
    {
        try
        {
            object parsed = Json.Deserialize(File.ReadAllText(path));
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

                if (!regions.TryGetValue(regionKey, out object regionObj) || regionObj is not Dictionary<string, object> regionMap)
                {
                    regionMap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    regions[regionKey] = regionMap;
                }
                if (!regionMap.TryGetValue("schedule", out object scheduleObj) || scheduleObj is not Dictionary<string, object> scheduleMap)
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
                    familyMap[setting.FamilyId] = new Dictionary<string, object>
                    {
                        ["enabled"] = setting.Enabled,
                        ["chance"] = setting.ChancePercent
                    };
                }
                scheduleMap["families"] = familyMap;
            }

            string temp = path + ".families.tmp";
            File.WriteAllText(temp, Json.Serialize(root));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temp, path);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError("DryCycle schedule/families save failed: " + ex);
            return false;
        }
    }

    private static bool TryReadEnabled(Dictionary<string, object> map, out bool enabled)
    {
        enabled = false;
        if (map == null || !map.TryGetValue("enabled", out object value))
        {
            return false;
        }
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
