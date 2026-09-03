using System;
using System.Collections.Generic;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal sealed class WeatherSpatialScheduleWeather
{
    internal string Id { get; }
    internal float ChancePercent { get; set; }
    internal readonly Dictionary<string, float> Variants =
        new(StringComparer.OrdinalIgnoreCase);

    internal WeatherSpatialScheduleWeather(string id, float chancePercent)
    {
        Id = (id ?? string.Empty).Trim();
        ChancePercent = ClampChance(chancePercent);
    }

    internal static float ClampChance(float value) => Math.Max(0f, Math.Min(100f, value));
}

internal sealed class WeatherSpatialRegionSchedule
{
    internal readonly Dictionary<string, WeatherSpatialScheduleWeather> Weather =
        new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, float> DangerTypes =
        new(StringComparer.OrdinalIgnoreCase);

    internal bool IsEmpty => Weather.Count == 0 && DangerTypes.Count == 0;

    internal bool Contains(WeatherScheduleEventKind kind, string id)
    {
        string normalized = WeatherSpatialCatalog.NormalizeId(id);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (kind == WeatherScheduleEventKind.DangerType)
        {
            foreach (string dangerId in DangerTypes.Keys)
            {
                if (WeatherSpatialCatalog.NormalizeId(dangerId) == normalized)
                {
                    return true;
                }
            }
            return false;
        }

        foreach (WeatherSpatialScheduleWeather weather in Weather.Values)
        {
            if (weather.Variants.Count == 0 &&
                WeatherSpatialCatalog.NormalizeId(weather.Id) == normalized)
            {
                return true;
            }

            foreach (string variantId in weather.Variants.Keys)
            {
                if (WeatherSpatialCatalog.NormalizeId(variantId) == normalized)
                {
                    return true;
                }
            }
        }
        return false;
    }
}

internal static partial class WeatherSpatialRegistry
{
    private static readonly Dictionary<string, WeatherSpatialRegionSchedule> RegionSchedules =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool TryGetScheduleRules(
        string regionId,
        out WeatherSpatialRegionSchedule schedule)
    {
        return RegionSchedules.TryGetValue(NormalizeRegion(regionId), out schedule);
    }

    internal static bool TryGetScheduleProfile(
        string regionId,
        out RegionClimateProfile profile)
    {
        profile = null;
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0 ||
            !RegionSchedules.TryGetValue(regionKey, out WeatherSpatialRegionSchedule schedule) ||
            schedule.IsEmpty)
        {
            return false;
        }

        RegionClimateProfile built = new(regionKey);
        foreach (string weatherId in SortedKeys(schedule.Weather))
        {
            WeatherSpatialScheduleWeather configured = schedule.Weather[weatherId];
            WeatherFamilyClimateEntry family = new(configured.Id, configured.ChancePercent);
            foreach (string variantId in SortedKeys(configured.Variants))
            {
                family.AddVariant(new ClimateChanceEntry(
                    variantId,
                    configured.Variants[variantId]));
            }
            built.AddWeather(family);
        }

        foreach (string dangerId in SortedKeys(schedule.DangerTypes))
        {
            built.AddDanger(new ClimateChanceEntry(
                dangerId,
                schedule.DangerTypes[dangerId]));
        }

        profile = built;
        return true;
    }

    internal static bool RegionScheduleContains(
        string regionId,
        WeatherScheduleEventKind kind,
        string id)
    {
        return TryGetScheduleRules(regionId, out WeatherSpatialRegionSchedule schedule) &&
               schedule.Contains(kind, id);
    }
}
