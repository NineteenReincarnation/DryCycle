using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialRegistry
{
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
        // FamWeather is now a Region scheduling category only. Room authoring is exact
        // SubWeather/DangerType Allow/Forbidden and never depends on a room Family rule.
        if (!WeatherSpatialCatalog.IsKnownWeather(kind, weatherId))
        {
            return false;
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
            }

            WeatherSpatialRule exactDefault = GetRule(region.WeatherDefaults, exactKey);
            if (exactDefault != WeatherSpatialRule.Inherit)
            {
                return exactDefault == WeatherSpatialRule.Allow;
            }
        }

        return false;
    }

    internal static bool IsFamilyAllowed(string regionId, string roomName, string familyId)
    {
        if (!WeatherSpatialCatalog.TryGetFamily(familyId, out WeatherSpatialFamily family))
        {
            return false;
        }

        // Retained for Overview/hover compatibility: a Family is considered present in
        // a room when at least one of its concrete children is allowed there.
        for (int i = 0; i < family.Members.Count; i++)
        {
            WeatherSpatialMember member = family.Members[i];
            if (IsAllowed(regionId, roomName, member.Kind, member.Id))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool ClearRegionWeatherConfiguration(string regionId)
    {
        EnsureLegacyScheduleMigration();

        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0)
        {
            return false;
        }

        bool removedSpatial = Regions.Remove(regionKey);
        bool removedSchedule = RegionSchedules.Remove(regionKey);
        bool removedFamilySchedule = ClearRegionFamilyScheduleState(regionKey);
        if (!removedSpatial && !removedSchedule && !removedFamilySchedule)
        {
            return false;
        }

        Dirty = true;
        WeatherScheduleCacheInvalidation.InvalidateAll();
        return true;
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

            // FamWeather is no longer spatial authoring data. Drop legacy Family
            // room/default rules on canonical save so JSON matches the current UI.
            RemoveLegacyFamilySpatialRules();

            // Materialize explicit Region FamWeather/SubWeather enable state for any
            // legacy raw schedule before serializing the canonical file.
            List<string> scheduleRegions = new(RegionSchedules.Keys);
            for (int i = 0; i < scheduleRegions.Count; i++)
            {
                EnsureLegacyFamilySchedule(scheduleRegions[i]);
                SynchronizeGroupedFamilyChances(scheduleRegions[i]);
            }

            string json = Json.Serialize(BuildJsonRoot());
            if (!TryMergeRegionFamilyScheduleState(json, out json))
            {
                Dirty = true;
                Plugin.Logger?.LogError("DryCycle weather spatial: failed to build regional FamWeather JSON.");
                return false;
            }

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

    private static void RemoveLegacyFamilySpatialRules()
    {
        List<string> regionKeys = new(Regions.Keys);
        for (int regionIndex = 0; regionIndex < regionKeys.Count; regionIndex++)
        {
            string regionKey = regionKeys[regionIndex];
            if (!Regions.TryGetValue(regionKey, out WeatherSpatialRegionRules region))
            {
                continue;
            }

            region.FamilyDefaults.Clear();
            List<string> roomKeys = new(region.Rooms.Keys);
            for (int roomIndex = 0; roomIndex < roomKeys.Count; roomIndex++)
            {
                string roomKey = roomKeys[roomIndex];
                WeatherSpatialRoomRules room = region.Rooms[roomKey];
                room.Families.Clear();
                if (room.IsEmpty)
                {
                    region.Rooms.Remove(roomKey);
                }
            }

            TrimRegion(regionKey, region);
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
        RegionSchedules.Clear();
        RegionFamilySchedules.Clear();
        ParseWarnings.Clear();
        _globalDefault = WeatherSpatialRule.Deny;
        FatalLoadError = null;
        _recoveredFromBackup = false;
        Dirty = true;
        LoadedPath = path;
        return Save();
    }
}
