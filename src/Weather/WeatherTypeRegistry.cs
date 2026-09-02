using System;
using System.Collections.Generic;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather;

/// <summary>
/// Authoritative list of schedule IDs that currently have a DryCycle runtime owner.
/// Climate files may describe future weather types, but unsupported IDs must never
/// consume a schedule slot until their runtime exists.
/// </summary>
internal static class WeatherTypeRegistry
{
    private static readonly HashSet<string> WarnedUnsupported =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsSchedulable(
        string id,
        WeatherScheduleEventKind kind)
    {
        string normalized = Normalize(id);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (kind == WeatherScheduleEventKind.Weather)
        {
            if (normalized == "LIGHTRAIN" ||
                normalized == "HEAVYRAIN" ||
                normalized == "FOG" ||
                normalized == "DENSEFOG" ||
                normalized == "HEATWAVE")
            {
                return true;
            }

            if (normalized == "SANDSTORM")
            {
                return ModManager.Watcher;
            }

            return false;
        }

        if (normalized == "DEATHRAIN" || normalized == "RAIN")
        {
            return true;
        }

        if (normalized == "SANDSTORM" || normalized == "DEATHSANDSTORM")
        {
            return ModManager.Watcher;
        }

        return false;
    }

    internal static void WarnUnsupported(
        string regionId,
        string id,
        WeatherScheduleEventKind kind)
    {
        string key = $"{regionId?.Trim()}|{kind}|{Normalize(id)}";
        if (!WarnedUnsupported.Add(key))
        {
            return;
        }

        Plugin.Logger?.LogWarning(
            $"DryCycle skipped unsupported scheduled {kind} '{id}' in region '{regionId}'. " +
            "The climate entry remains valid data, but it will not consume a schedule slot until a runtime is registered.");
    }

    internal static void ResetWarnings()
    {
        WarnedUnsupported.Clear();
    }

    private static string Normalize(string id)
    {
        return (id ?? string.Empty)
            .Trim()
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .ToUpperInvariant();
    }
}
