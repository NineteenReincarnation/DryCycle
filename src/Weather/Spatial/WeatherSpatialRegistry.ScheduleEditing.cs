using System;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialRegistry
{
    internal static bool TryGetFamilyWeatherChance(
        string regionId,
        in WeatherSpatialTarget target,
        out float chancePercent)
    {
        chancePercent = 0f;
        if (!TryGetTargetFamily(target, out WeatherSpatialFamily family))
        {
            return false;
        }

        EnsureLegacyScheduleMigration();
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0 ||
            !RegionSchedules.TryGetValue(regionKey, out WeatherSpatialRegionSchedule schedule) ||
            !schedule.Weather.TryGetValue(family.Id, out WeatherSpatialScheduleWeather entry) ||
            !entry.IsFamily)
        {
            return false;
        }

        chancePercent = entry.ChancePercent;
        return true;
    }

    internal static bool SetFamilyWeatherChance(
        string regionId,
        in WeatherSpatialTarget target,
        float chancePercent)
    {
        if (!TryGetTargetFamily(target, out WeatherSpatialFamily family))
        {
            return false;
        }

        EnsureLegacyScheduleMigration();
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0)
        {
            return false;
        }

        chancePercent = WeatherSpatialScheduleWeather.ClampChance(chancePercent);
        if (!RegionSchedules.TryGetValue(regionKey, out WeatherSpatialRegionSchedule schedule))
        {
            schedule = new WeatherSpatialRegionSchedule();
            RegionSchedules[regionKey] = schedule;
        }

        if (schedule.Weather.TryGetValue(family.Id, out WeatherSpatialScheduleWeather existing) &&
            existing.IsFamily)
        {
            existing.ChancePercent = chancePercent;
            Dirty = true;
            return true;
        }

        WeatherSpatialScheduleWeather created = new(
            family.Id,
            chancePercent,
            isFamily: true);

        // A child can have the same ID as its family (Fog/Fog). Preserve that
        // child's independent chance while converting the old simple entry.
        for (int i = 0; i < family.Members.Count; i++)
        {
            WeatherSpatialMember member = family.Members[i];
            if (member.Kind != WeatherScheduleEventKind.Weather)
            {
                continue;
            }

            string memberId = WeatherSpatialCatalog.CanonicalWeatherId(member.Kind, member.Id);
            if (schedule.Weather.TryGetValue(memberId, out WeatherSpatialScheduleWeather simple) &&
                !simple.IsFamily)
            {
                created.Variants[memberId] = simple.ChancePercent;
                schedule.Weather.Remove(memberId);
            }
        }

        schedule.Weather[family.Id] = created;
        Dirty = true;
        return true;
    }

    internal static bool TryGetSubWeatherChance(
        string regionId,
        in WeatherSpatialTarget target,
        out float chancePercent)
    {
        chancePercent = 0f;
        if (target.IsFamily)
        {
            return false;
        }

        EnsureLegacyScheduleMigration();
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0 ||
            !RegionSchedules.TryGetValue(regionKey, out WeatherSpatialRegionSchedule schedule))
        {
            return false;
        }

        string id = WeatherSpatialCatalog.CanonicalWeatherId(target.Kind, target.WeatherId);
        if (target.Kind == WeatherScheduleEventKind.DangerType)
        {
            return schedule.DangerTypes.TryGetValue(id, out chancePercent);
        }

        if (TryGetTargetFamily(target, out WeatherSpatialFamily family) &&
            schedule.Weather.TryGetValue(family.Id, out WeatherSpatialScheduleWeather familyEntry) &&
            familyEntry.IsFamily &&
            familyEntry.Variants.TryGetValue(id, out chancePercent))
        {
            return true;
        }

        if (schedule.Weather.TryGetValue(id, out WeatherSpatialScheduleWeather exact) &&
            !exact.IsFamily)
        {
            chancePercent = exact.ChancePercent;
            return true;
        }

        return false;
    }

    internal static bool SetSubWeatherChance(
        string regionId,
        in WeatherSpatialTarget target,
        float chancePercent)
    {
        if (target.IsFamily)
        {
            return false;
        }

        EnsureLegacyScheduleMigration();
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0)
        {
            return false;
        }

        chancePercent = WeatherSpatialScheduleWeather.ClampChance(chancePercent);
        if (!RegionSchedules.TryGetValue(regionKey, out WeatherSpatialRegionSchedule schedule))
        {
            schedule = new WeatherSpatialRegionSchedule();
            RegionSchedules[regionKey] = schedule;
        }

        string id = WeatherSpatialCatalog.CanonicalWeatherId(target.Kind, target.WeatherId);
        if (target.Kind == WeatherScheduleEventKind.DangerType)
        {
            schedule.DangerTypes[id] = chancePercent;
            Dirty = true;
            return true;
        }

        if (TryGetTargetFamily(target, out WeatherSpatialFamily family) &&
            schedule.Weather.TryGetValue(family.Id, out WeatherSpatialScheduleWeather familyEntry) &&
            familyEntry.IsFamily)
        {
            familyEntry.Variants[id] = chancePercent;
            Dirty = true;
            return true;
        }

        if (schedule.Weather.TryGetValue(id, out WeatherSpatialScheduleWeather exact) &&
            !exact.IsFamily)
        {
            exact.ChancePercent = chancePercent;
            Dirty = true;
            return true;
        }

        if (family != null)
        {
            int weatherMemberCount = 0;
            for (int i = 0; i < family.Members.Count; i++)
            {
                if (family.Members[i].Kind == WeatherScheduleEventKind.Weather)
                {
                    weatherMemberCount++;
                }
            }

            if (weatherMemberCount > 1)
            {
                WeatherSpatialScheduleWeather created = new(
                    family.Id,
                    100f,
                    isFamily: true);
                created.Variants[id] = chancePercent;
                schedule.Weather[family.Id] = created;
                Dirty = true;
                return true;
            }
        }

        schedule.Weather[id] = new WeatherSpatialScheduleWeather(id, chancePercent);
        Dirty = true;
        return true;
    }

    private static bool TryGetTargetFamily(
        in WeatherSpatialTarget target,
        out WeatherSpatialFamily family)
    {
        return target.IsFamily
            ? WeatherSpatialCatalog.TryGetFamily(target.FamilyId, out family)
            : WeatherSpatialCatalog.TryGetFamily(target.Kind, target.WeatherId, out family);
    }
}
