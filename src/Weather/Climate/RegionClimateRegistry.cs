using System;
using System.Collections.Generic;
using DryCycle.Weather.Spatial;

namespace DryCycle.Weather.Climate;

// Compatibility data model retained for WeatherScheduleRuntime. The source of truth is
// now world/WeatherSpatial.json; no RegionClimate.txt loader remains.
internal class ClimateChanceEntry
{
    internal string Id { get; }
    internal float ChancePercent { get; }

    internal ClimateChanceEntry(string id, float chancePercent)
    {
        Id = (id ?? string.Empty).Trim();
        ChancePercent = Math.Max(0f, Math.Min(100f, chancePercent));
    }
}

internal sealed class WeatherFamilyClimateEntry : ClimateChanceEntry
{
    private readonly List<ClimateChanceEntry> _variants = new();

    internal IReadOnlyList<ClimateChanceEntry> Variants => _variants;

    internal WeatherFamilyClimateEntry(string id, float chancePercent)
        : base(id, chancePercent)
    {
    }

    internal void AddVariant(ClimateChanceEntry variant)
    {
        if (variant != null)
        {
            _variants.Add(variant);
        }
    }
}

internal sealed class RegionClimateProfile
{
    private readonly List<WeatherFamilyClimateEntry> _weather = new();
    private readonly List<ClimateChanceEntry> _danger = new();

    internal string RegionId { get; }
    internal IReadOnlyList<WeatherFamilyClimateEntry> Weather => _weather;
    internal IReadOnlyList<ClimateChanceEntry> DangerTypes => _danger;

    internal RegionClimateProfile(string regionId)
    {
        RegionId = (regionId ?? string.Empty).Trim();
    }

    internal void AddWeather(WeatherFamilyClimateEntry entry)
    {
        if (entry != null)
        {
            _weather.Add(entry);
        }
    }

    internal void AddDanger(ClimateChanceEntry entry)
    {
        if (entry != null)
        {
            _danger.Add(entry);
        }
    }

    internal bool ContainsWeatherId(string id)
    {
        string normalized = Normalize(id);
        if (normalized.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < _weather.Count; i++)
        {
            WeatherFamilyClimateEntry family = _weather[i];
            if (Normalize(family.Id) == normalized)
            {
                return true;
            }
            for (int j = 0; j < family.Variants.Count; j++)
            {
                if (Normalize(family.Variants[j].Id) == normalized)
                {
                    return true;
                }
            }
        }
        return false;
    }

    internal bool ContainsDangerId(string id)
    {
        string normalized = Normalize(id);
        if (normalized.Length == 0)
        {
            return false;
        }
        for (int i = 0; i < _danger.Count; i++)
        {
            if (Normalize(_danger[i].Id) == normalized)
            {
                return true;
            }
        }
        return false;
    }

    private static string Normalize(string value) =>
        (value ?? string.Empty)
        .Trim()
        .Replace("_", string.Empty)
        .Replace("-", string.Empty)
        .ToUpperInvariant();
}

/// <summary>
/// Compatibility facade for code that consumes RegionClimateProfile. All data is
/// supplied by WeatherSpatial.json v2 through WeatherSpatialRegistry.
/// </summary>
internal static class RegionClimateRegistry
{
    internal static string LoadedPath => WeatherSpatialRegistry.LoadedPath;

    internal static void Reload()
    {
        WeatherSpatialRegistry.Reload();
    }

    internal static bool TryGetProfile(string regionId, out RegionClimateProfile profile)
    {
        return WeatherSpatialRegistry.TryGetScheduleProfile(regionId, out profile);
    }

    internal static bool RegionCanUseWeather(string regionId, string weatherId)
    {
        return TryGetProfile(regionId, out RegionClimateProfile profile) &&
               profile.ContainsWeatherId(weatherId);
    }

    internal static bool RegionCanUseDanger(string regionId, string dangerId)
    {
        return TryGetProfile(regionId, out RegionClimateProfile profile) &&
               profile.ContainsDangerId(dangerId);
    }
}
