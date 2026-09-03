using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DryCycle.Weather.Climate;
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

}
