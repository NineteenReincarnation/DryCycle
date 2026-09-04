using System;
using System.Collections.Generic;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal sealed class WeatherSpatialScheduleWeather
{
    internal string Id { get; }
    internal bool IsFamily { get; }
    internal float ChancePercent { get; set; }
    internal readonly Dictionary<string, float> Variants =
        new(StringComparer.OrdinalIgnoreCase);

    internal WeatherSpatialScheduleWeather(
        string id,
        float chancePercent,
        bool isFamily = false)
    {
        Id = (id ?? string.Empty).Trim();
        IsFamily = isFamily;
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
            if (!weather.IsFamily &&
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
        EnsureLegacyScheduleMigration();
        return RegionSchedules.TryGetValue(NormalizeRegion(regionId), out schedule);
    }

    internal static bool TryGetScheduleProfile(
        string regionId,
        out RegionClimateProfile profile)
    {
        EnsureLegacyScheduleMigration();
        profile = null;
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0)
        {
            return false;
        }

        EnsureLegacyFamilySchedule(regionKey);
        RegionClimateProfile built = new(regionKey);
        bool any = false;

        for (int familyIndex = 0; familyIndex < WeatherSpatialCatalog.AllFamilies.Count; familyIndex++)
        {
            WeatherSpatialFamily family = WeatherSpatialCatalog.AllFamilies[familyIndex];
            if (!TryGetFamilySchedule(regionKey, family.Id, out bool familyEnabled, out float familyChance) ||
                !familyEnabled ||
                familyChance <= 0f)
            {
                continue;
            }

            WeatherFamilyClimateEntry weatherGroup = null;
            for (int memberIndex = 0; memberIndex < family.Members.Count; memberIndex++)
            {
                WeatherSpatialMember member = family.Members[memberIndex];
                if (!TryGetSubWeatherSchedule(
                        regionKey,
                        member.Kind,
                        member.Id,
                        out bool childEnabled,
                        out float childChance) ||
                    !childEnabled ||
                    childChance <= 0f)
                {
                    continue;
                }

                if (member.Kind == WeatherScheduleEventKind.Weather)
                {
                    weatherGroup ??= new WeatherFamilyClimateEntry(family.Id, familyChance);
                    weatherGroup.AddVariant(new ClimateChanceEntry(member.Id, childChance));
                }
                else
                {
                    // DangerType remains a flat compatibility list. Folding the Family
                    // roll into its chance preserves the same independent probability.
                    built.AddDanger(new ClimateChanceEntry(
                        member.Id,
                        childChance * familyChance / 100f));
                    any = true;
                }
            }

            if (weatherGroup != null && weatherGroup.Variants.Count > 0)
            {
                built.AddWeather(weatherGroup);
                any = true;
            }
        }

        profile = built;
        return any;
    }

    internal static bool RegionScheduleContains(
        string regionId,
        WeatherScheduleEventKind kind,
        string id)
    {
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0 ||
            !WeatherSpatialCatalog.TryGetFamily(kind, id, out WeatherSpatialFamily family) ||
            !TryGetFamilySchedule(regionKey, family.Id, out bool familyEnabled, out float familyChance) ||
            !familyEnabled ||
            familyChance <= 0f ||
            !TryGetSubWeatherSchedule(
                regionKey,
                kind,
                id,
                out bool childEnabled,
                out float childChance))
        {
            return false;
        }

        return childEnabled && childChance > 0f;
    }

    private static bool TryGetConfiguredMemberChance(
        WeatherSpatialRegionSchedule schedule,
        WeatherSpatialFamily family,
        in WeatherSpatialMember member,
        out float chancePercent)
    {
        chancePercent = 0f;
        if (schedule == null || family == null)
        {
            return false;
        }

        string id = WeatherSpatialCatalog.CanonicalWeatherId(member.Kind, member.Id);
        if (member.Kind == WeatherScheduleEventKind.DangerType)
        {
            return schedule.DangerTypes.TryGetValue(id, out chancePercent);
        }

        if (schedule.Weather.TryGetValue(family.Id, out WeatherSpatialScheduleWeather grouped) &&
            grouped.IsFamily &&
            grouped.Variants.TryGetValue(id, out chancePercent))
        {
            return true;
        }

        if (schedule.Weather.TryGetValue(id, out WeatherSpatialScheduleWeather exact) &&
            !exact.IsFamily)
        {
            chancePercent = exact.ChancePercent;
            return true;
        }

        foreach (WeatherSpatialScheduleWeather candidate in schedule.Weather.Values)
        {
            if (candidate.IsFamily && candidate.Variants.TryGetValue(id, out chancePercent))
            {
                return true;
            }
        }
        return false;
    }
}
