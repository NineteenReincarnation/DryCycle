using System;
using System.Collections.Generic;
using System.IO;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialRegistry
{
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

        foreach (string regionId in AllConfiguredRegionIds())
        {
            TryGetRegion(regionId, out WeatherSpatialRegionRules rules);
            TryGetScheduleRules(regionId, out WeatherSpatialRegionSchedule schedule);

            if (rules != null)
            {
                ValidateRuleKeys(regionId, rules, result);
            }

            HashSet<string> knownRooms = LoadKnownRooms(regionId, activeWorld, out string roomSourceWarning);
            if (!string.IsNullOrEmpty(roomSourceWarning))
            {
                result.Warn(roomSourceWarning);
            }

            if (rules != null)
            {
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
            }

            if (schedule != null)
            {
                ValidateSchedule(regionId, schedule, result);
            }

            if (rules != null)
            {
                ValidateSpatialScheduleReferences(regionId, rules, schedule, result);
            }
        }

        return result;
    }

    internal static IEnumerable<string> ConfiguredRegionIds => AllConfiguredRegionIds();

    private static List<string> AllConfiguredRegionIds()
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (string regionId in Regions.Keys)
        {
            ids.Add(regionId);
        }
        foreach (string regionId in RegionSchedules.Keys)
        {
            ids.Add(regionId);
        }

        List<string> result = new(ids);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

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
            if (!WeatherSpatialCatalog.TryParseWeatherKey(
                    weather,
                    out WeatherScheduleEventKind kind,
                    out string id))
            {
                result.Error($"{regionId}/{roomName}: unknown weather '{weather}'.");
                continue;
            }

            if (WeatherSpatialCatalog.TryGetFamily(kind, id, out WeatherSpatialFamily family) &&
                !IsFamilyAllowed(regionId, roomName, family.Id))
            {
                result.Warn($"{regionId}/{roomName}: '{weather}' is configured but parent family '{family.Id}' is not allowed in this room; the child rule is inactive.");
            }
        }
    }

    private static void ValidateSchedule(
        string regionId,
        WeatherSpatialRegionSchedule schedule,
        WeatherSpatialValidationResult result)
    {
        foreach (WeatherSpatialScheduleWeather configured in schedule.Weather.Values)
        {
            if (configured.Variants.Count == 0)
            {
                if (!WeatherSpatialCatalog.IsKnownWeather(
                        WeatherScheduleEventKind.Weather,
                        configured.Id))
                {
                    result.Error($"{regionId}: schedule contains unknown weather '{configured.Id}'.");
                    continue;
                }

                if (!WeatherTypeRegistry.IsSchedulable(
                        configured.Id,
                        WeatherScheduleEventKind.Weather))
                {
                    result.Warn($"{regionId}: schedule weather '{configured.Id}' is known but is not schedulable by the current runtime.");
                }
                continue;
            }

            if (!WeatherSpatialCatalog.TryGetFamily(configured.Id, out WeatherSpatialFamily configuredFamily))
            {
                result.Error($"{regionId}: schedule family '{configured.Id}' is unknown.");
                continue;
            }

            foreach (string variantId in configured.Variants.Keys)
            {
                if (!WeatherSpatialCatalog.IsKnownWeather(
                        WeatherScheduleEventKind.Weather,
                        variantId))
                {
                    result.Error($"{regionId}: schedule family '{configured.Id}' contains unknown variant '{variantId}'.");
                    continue;
                }

                if (!WeatherSpatialCatalog.TryGetFamily(
                        WeatherScheduleEventKind.Weather,
                        variantId,
                        out WeatherSpatialFamily actualFamily) ||
                    !string.Equals(actualFamily.Id, configuredFamily.Id, StringComparison.OrdinalIgnoreCase))
                {
                    result.Error($"{regionId}: schedule variant '{variantId}' does not belong to family '{configured.Id}'.");
                    continue;
                }

                if (!WeatherTypeRegistry.IsSchedulable(
                        variantId,
                        WeatherScheduleEventKind.Weather))
                {
                    result.Warn($"{regionId}: schedule variant '{variantId}' is known but is not schedulable by the current runtime.");
                }
            }
        }

        foreach (string dangerId in schedule.DangerTypes.Keys)
        {
            if (!WeatherSpatialCatalog.IsKnownWeather(
                    WeatherScheduleEventKind.DangerType,
                    dangerId))
            {
                result.Error($"{regionId}: schedule contains unknown DangerType '{dangerId}'.");
                continue;
            }

            if (!WeatherTypeRegistry.IsSchedulable(
                    dangerId,
                    WeatherScheduleEventKind.DangerType))
            {
                result.Warn($"{regionId}: schedule DangerType '{dangerId}' is known but is not schedulable by the current runtime.");
            }
        }
    }

    private static void ValidateSpatialScheduleReferences(
        string regionId,
        WeatherSpatialRegionRules rules,
        WeatherSpatialRegionSchedule schedule,
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
                !ScheduleHasAnyMember(schedule, family))
            {
                result.Warn($"{regionId}: family '{family.Id}' has spatial rules but no member is enabled in WeatherSpatial.json schedule.");
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
            if (!WeatherSpatialCatalog.TryParseWeatherKey(
                    key,
                    out WeatherScheduleEventKind kind,
                    out string id))
            {
                continue;
            }

            if (schedule == null || !schedule.Contains(kind, id))
            {
                result.Warn($"{regionId}: '{key}' has spatial rules but is not enabled in WeatherSpatial.json schedule.");
            }
        }
    }

    private static bool ScheduleHasAnyMember(
        WeatherSpatialRegionSchedule schedule,
        WeatherSpatialFamily family)
    {
        if (schedule == null || family == null)
        {
            return false;
        }

        for (int i = 0; i < family.Members.Count; i++)
        {
            WeatherSpatialMember member = family.Members[i];
            if (schedule.Contains(member.Kind, member.Id))
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
}
