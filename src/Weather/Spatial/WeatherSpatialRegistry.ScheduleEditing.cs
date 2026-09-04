using System;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialRegistry
{
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

        foreach (WeatherSpatialScheduleWeather entry in schedule.Weather.Values)
        {
            if (entry.Variants.TryGetValue(id, out chancePercent))
            {
                return true;
            }
        }

        if (schedule.Weather.TryGetValue(id, out WeatherSpatialScheduleWeather exact))
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

        foreach (WeatherSpatialScheduleWeather entry in schedule.Weather.Values)
        {
            if (entry.Variants.ContainsKey(id))
            {
                entry.Variants[id] = chancePercent;
                Dirty = true;
                return true;
            }
        }

        if (schedule.Weather.TryGetValue(id, out WeatherSpatialScheduleWeather exact))
        {
            exact.ChancePercent = chancePercent;
            Dirty = true;
            return true;
        }

        if (WeatherSpatialCatalog.TryGetFamily(
                target.Kind,
                target.WeatherId,
                out WeatherSpatialFamily family))
        {
            if (schedule.Weather.TryGetValue(family.Id, out WeatherSpatialScheduleWeather familyEntry))
            {
                familyEntry.Variants[id] = chancePercent;
                Dirty = true;
                return true;
            }

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
                WeatherSpatialScheduleWeather created = new(family.Id, 100f);
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
}
