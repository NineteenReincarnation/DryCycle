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
        if (!WeatherSpatialCatalog.TryGetFamily(kind, weatherId, out WeatherSpatialFamily family))
        {
            return _globalDefault != WeatherSpatialRule.Deny;
        }

        // A Family is only the prerequisite/container for its members. It never
        // implicitly enables every child weather in the room.
        if (!IsFamilyAllowed(regionId, roomName, family.Id))
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

        // No explicit child rule means the child was never placed. The parent Family
        // only unlocks editing; it does not supply an implicit Allow.
        return false;
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

    internal static bool ClearRegionSpatialRules(string regionId)
    {
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0 ||
            !Regions.TryGetValue(regionKey, out WeatherSpatialRegionRules region) ||
            region == null ||
            region.IsEmpty)
        {
            return false;
        }

        // RegionSchedules deliberately lives in a separate dictionary. Removing this
        // entry clears only Weather Zones (defaults + room spatial rules) and leaves the
        // region's schedule/chances untouched.
        Regions.Remove(regionKey);
        Dirty = true;
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
        RegionSchedules.Clear();
        ParseWarnings.Clear();
        _globalDefault = WeatherSpatialRule.Deny;
        FatalLoadError = null;
        _recoveredFromBackup = false;
        Dirty = true;
        LoadedPath = path;
        return Save();
    }
}
