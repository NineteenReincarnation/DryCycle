using System;
using System.Collections.Generic;
using System.IO;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialRegistry
{
    internal static WeatherSpatialValidationResult Validate(World activeWorld)
    {
        EnsureLegacyScheduleMigration();
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
            EnsureLegacyFamilySchedule(NormalizeRegion(regionId));

            ValidateFamilyScheduleState(regionId, schedule, result);

            if (rules != null)
            {
                ValidateRuleKeys(regionId, rules, result);
            }

            HashSet<string> knownRooms = new(StringComparer.OrdinalIgnoreCase);
            if (rules != null && rules.Rooms.Count > 0)
            {
                knownRooms = LoadKnownRooms(regionId, activeWorld, out string roomSourceWarning);
                if (!string.IsNullOrEmpty(roomSourceWarning))
                {
                    result.Warn(roomSourceWarning);
                }
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
        foreach (string regionId in RegionFamilySchedules.Keys)
        {
            ids.Add(regionId);
        }

        List<string> result = new(ids);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static void ValidateFamilyScheduleState(
        string regionId,
        WeatherSpatialRegionSchedule schedule,
        WeatherSpatialValidationResult result)
    {
        if (!RegionFamilySchedules.TryGetValue(
                NormalizeRegion(regionId),
                out Dictionary<string, WeatherSpatialRegionFamilySchedule> families))
        {
            return;
        }

        List<WeatherSpatialRegionFamilySchedule> settings = new(families.Values);
        foreach (WeatherSpatialRegionFamilySchedule setting in settings)
        {
            if (!WeatherSpatialCatalog.TryGetFamily(setting.FamilyId, out WeatherSpatialFamily family))
            {
                result.Error($"{regionId}: schedule/families contains unknown FamWeather '{setting.FamilyId}'.");
                continue;
            }

            if (!setting.Enabled)
            {
                continue;
            }

            if (setting.ChancePercent <= 0f)
            {
                result.Warn($"{regionId}: FamWeather '{family.Id}' is YES but FamWeatherChance is 0%; it cannot schedule.");
                continue;
            }

            if (!ScheduleHasAnyActiveMember(regionId, schedule, family))
            {
                result.Warn($"{regionId}: FamWeather '{family.Id}' is YES but no SubWeather has a non-zero active chance.");
            }
        }
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
        // Legacy room Family rules are intentionally ignored. FamWeather now belongs
        // to Region scheduling only; rooms author concrete SubWeather/DangerType rules.
        foreach (string weather in room.Weather.Keys)
        {
            if (!WeatherSpatialCatalog.TryParseWeatherKey(weather, out _, out _))
            {
                result.Error($"{regionId}/{roomName}: unknown weather '{weather}'.");
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
            if (!configured.IsFamily)
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
        HashSet<string> exactKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, WeatherSpatialRule> weatherPair in rules.WeatherDefaults)
        {
            if (weatherPair.Value == WeatherSpatialRule.Allow)
            {
                exactKeys.Add(weatherPair.Key);
            }
        }
        foreach (WeatherSpatialRoomRules room in rules.Rooms.Values)
        {
            foreach (KeyValuePair<string, WeatherSpatialRule> weatherPair in room.Weather)
            {
                if (weatherPair.Value == WeatherSpatialRule.Allow)
                {
                    exactKeys.Add(weatherPair.Key);
                }
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

            if (!RegionScheduleContains(regionId, kind, id))
            {
                result.Warn($"{regionId}: '{key}' is allowed in Weather Zones but its FamWeather/SubWeather schedule is not active.");
            }
        }
    }

    private static bool ScheduleHasAnyActiveMember(
        string regionId,
        WeatherSpatialRegionSchedule schedule,
        WeatherSpatialFamily family)
    {
        if (family == null)
        {
            return false;
        }

        for (int i = 0; i < family.Members.Count; i++)
        {
            WeatherSpatialMember member = family.Members[i];
            if (RegionScheduleContains(regionId, member.Kind, member.Id))
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
