using System;
using System.Collections.Generic;
using System.IO;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialRegistry
{
    private const string DeprecatedVanillaSandDangerKey = "DangerType/SandStorm";

    private static string _legacyMigrationCheckedPath;
    private static DateTime _legacyMigrationCheckedWriteUtc;

    private static void EnsureLegacyScheduleMigration()
    {
        // DangerType/SandStorm is Rain World's vanilla room danger type and is not a
        // DryCycle Sand FamWeather member. Older WeatherSpatial builds exposed it by
        // mistake. Strip that exact legacy entry from the in-memory model so existing
        // authoring files migrate cleanly on their next Save.
        if (RemoveDeprecatedVanillaSandDanger())
        {
            Dirty = true;
        }

        string path = LoadedPath;
        if (_recoveredFromBackup && !string.IsNullOrEmpty(path) && File.Exists(path + ".bak"))
        {
            path += ".bak";
        }
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        DateTime writeUtc;
        try
        {
            writeUtc = File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            writeUtc = default;
        }

        if (string.Equals(_legacyMigrationCheckedPath, path, StringComparison.OrdinalIgnoreCase) &&
            _legacyMigrationCheckedWriteUtc == writeUtc)
        {
            return;
        }

        _legacyMigrationCheckedPath = path;
        _legacyMigrationCheckedWriteUtc = writeUtc;
        if (TryApplyLegacyScheduleMigration(path))
        {
            Dirty = true;
        }
    }

    private static bool RemoveDeprecatedVanillaSandDanger()
    {
        bool changed = false;

        List<string> scheduleRegions = new(RegionSchedules.Keys);
        for (int regionIndex = 0; regionIndex < scheduleRegions.Count; regionIndex++)
        {
            string regionId = scheduleRegions[regionIndex];
            WeatherSpatialRegionSchedule schedule = RegionSchedules[regionId];
            List<string> dangerIds = new(schedule.DangerTypes.Keys);
            for (int dangerIndex = 0; dangerIndex < dangerIds.Count; dangerIndex++)
            {
                string dangerId = dangerIds[dangerIndex];
                if (WeatherSpatialCatalog.NormalizeId(dangerId) == "SANDSTORM")
                {
                    schedule.DangerTypes.Remove(dangerId);
                    changed = true;
                }
            }

            if (schedule.IsEmpty)
            {
                RegionSchedules.Remove(regionId);
            }
        }

        foreach (Dictionary<string, WeatherSpatialRegionFamilySchedule> families in RegionFamilySchedules.Values)
        {
            if (!families.TryGetValue("Sand", out WeatherSpatialRegionFamilySchedule sand))
            {
                continue;
            }

            List<string> subWeatherKeys = new(sand.SubWeatherEnabled.Keys);
            for (int keyIndex = 0; keyIndex < subWeatherKeys.Count; keyIndex++)
            {
                string key = subWeatherKeys[keyIndex];
                if (string.Equals(key, DeprecatedVanillaSandDangerKey, StringComparison.OrdinalIgnoreCase))
                {
                    sand.SubWeatherEnabled.Remove(key);
                    changed = true;
                }
            }
        }

        List<string> spatialRegions = new(Regions.Keys);
        for (int regionIndex = 0; regionIndex < spatialRegions.Count; regionIndex++)
        {
            string regionId = spatialRegions[regionIndex];
            WeatherSpatialRegionRules region = Regions[regionId];
            changed |= RemoveDeprecatedRule(region.WeatherDefaults);

            List<string> roomNames = new(region.Rooms.Keys);
            for (int roomIndex = 0; roomIndex < roomNames.Count; roomIndex++)
            {
                string roomName = roomNames[roomIndex];
                WeatherSpatialRoomRules room = region.Rooms[roomName];
                changed |= RemoveDeprecatedRule(room.Weather);
                if (room.IsEmpty)
                {
                    region.Rooms.Remove(roomName);
                }
            }

            if (region.IsEmpty)
            {
                Regions.Remove(regionId);
            }
        }

        // LoadRegionFamilyScheduleState intentionally rejects the retired key after the
        // catalog change. Suppress that one migration-only parse warning; all unrelated
        // malformed entries continue to be reported normally.
        for (int warningIndex = ParseWarnings.Count - 1; warningIndex >= 0; warningIndex--)
        {
            if (ParseWarnings[warningIndex].IndexOf(
                    DeprecatedVanillaSandDangerKey,
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ParseWarnings.RemoveAt(warningIndex);
                changed = true;
            }
        }

        if (changed)
        {
            WeatherScheduleCacheInvalidation.InvalidateAll();
        }
        return changed;
    }

    private static bool RemoveDeprecatedRule(Dictionary<string, WeatherSpatialRule> rules)
    {
        if (rules == null || rules.Count == 0)
        {
            return false;
        }

        List<string> keys = new(rules.Keys);
        bool changed = false;
        for (int i = 0; i < keys.Count; i++)
        {
            if (string.Equals(
                    keys[i],
                    DeprecatedVanillaSandDangerKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                rules.Remove(keys[i]);
                changed = true;
            }
        }
        return changed;
    }

    private static bool TryApplyLegacyScheduleMigration(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            object parsed = Json.Deserialize(File.ReadAllText(path));
            if (parsed is not Dictionary<string, object> root)
            {
                return false;
            }

            // Only the explicit v1 format is unambiguously legacy. A v2 file with
            // spatial rules but no schedule is now a valid authoring state (for example
            // every Region FamWeather can intentionally be NO), so never resurrect
            // historic SU/B5 defaults merely because a v2 schedule is absent.
            if (ReadConfigVersion(root) != 1)
            {
                return false;
            }

            MergeLegacyRegionClimateDefaults();
            RemoveExpectedV1VersionWarning();
            Plugin.Logger?.LogInfo(
                "DryCycle migrated WeatherSpatial v1 climate data into the v2 schedule section in memory. Save WeatherSpatial to persist the migration.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                "DryCycle could not inspect WeatherSpatial.json for legacy schedule migration: " + ex.Message);
            return false;
        }
    }

    private static int ReadConfigVersion(Dictionary<string, object> root)
    {
        if (root != null &&
            root.TryGetValue("version", out object versionObj) &&
            TryNumber(versionObj, out double version))
        {
            return (int)version;
        }
        return 0;
    }

    private static bool MergeLegacyRegionClimateDefaults()
    {
        bool changed = false;

        WeatherSpatialRegionSchedule su = GetOrCreateSchedule("SU", ref changed);
        changed |= EnsureWeatherFamily(
            su,
            "Rain",
            80f,
            ("LightRain", 70f),
            ("HeavyRain", 45f));
        changed |= EnsureWeatherFamily(
            su,
            "Fog",
            100f,
            ("Fog", 100f),
            ("DenseFog", 100f));
        changed |= EnsureDanger(su, "DeathRain", 25f);

        WeatherSpatialRegionSchedule b5 = GetOrCreateSchedule("B5", ref changed);
        changed |= EnsureSimpleWeather(b5, "HeatWave", 100f);
        changed |= EnsureSimpleWeather(b5, "SandStorm", 100f);
        changed |= EnsureDanger(b5, "IntenseHeat", 100f);

        return changed;
    }

    private static WeatherSpatialRegionSchedule GetOrCreateSchedule(
        string regionId,
        ref bool changed)
    {
        if (!RegionSchedules.TryGetValue(regionId, out WeatherSpatialRegionSchedule schedule))
        {
            schedule = new WeatherSpatialRegionSchedule();
            RegionSchedules[regionId] = schedule;
            changed = true;
        }
        return schedule;
    }

    private static bool EnsureWeatherFamily(
        WeatherSpatialRegionSchedule schedule,
        string familyId,
        float chance,
        params (string Id, float Chance)[] variants)
    {
        bool changed = false;
        if (!schedule.Weather.TryGetValue(familyId, out WeatherSpatialScheduleWeather weather))
        {
            weather = new WeatherSpatialScheduleWeather(familyId, chance, isFamily: true);
            schedule.Weather[familyId] = weather;
            changed = true;
        }

        for (int i = 0; i < variants.Length; i++)
        {
            string variantId = WeatherSpatialCatalog.CanonicalWeatherId(
                WeatherScheduleEventKind.Weather,
                variants[i].Id);
            if (!weather.Variants.ContainsKey(variantId))
            {
                weather.Variants[variantId] = variants[i].Chance;
                changed = true;
            }
        }
        return changed;
    }

    private static bool EnsureSimpleWeather(
        WeatherSpatialRegionSchedule schedule,
        string weatherId,
        float chance)
    {
        string canonical = WeatherSpatialCatalog.CanonicalWeatherId(
            WeatherScheduleEventKind.Weather,
            weatherId);
        if (schedule.Weather.ContainsKey(canonical))
        {
            return false;
        }

        schedule.Weather[canonical] = new WeatherSpatialScheduleWeather(canonical, chance);
        return true;
    }

    private static bool EnsureDanger(
        WeatherSpatialRegionSchedule schedule,
        string dangerId,
        float chance)
    {
        string canonical = WeatherSpatialCatalog.CanonicalWeatherId(
            WeatherScheduleEventKind.DangerType,
            dangerId);
        if (schedule.DangerTypes.ContainsKey(canonical))
        {
            return false;
        }

        schedule.DangerTypes[canonical] = WeatherSpatialScheduleWeather.ClampChance(chance);
        return true;
    }

    private static void RemoveExpectedV1VersionWarning()
    {
        string prefix = "WeatherSpatial.json version 1 differs from supported version ";
        for (int i = ParseWarnings.Count - 1; i >= 0; i--)
        {
            if (ParseWarnings[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                ParseWarnings.RemoveAt(i);
            }
        }
    }
}
