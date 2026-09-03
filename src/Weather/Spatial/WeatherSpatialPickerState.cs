using System;
using System.Collections.Generic;

namespace DryCycle.Weather.Spatial;

/// <summary>
/// Session-only UI state for the Weather Zones target picker. DevUI nodes are
/// destroyed when H mode closes, so fold and selection state must live outside
/// the popup itself if it should survive reopening DevUI.
/// </summary>
internal static class WeatherSpatialPickerState
{
    private static readonly HashSet<string> CollapsedFamilies =
        new(StringComparer.OrdinalIgnoreCase);

    private static string _selectedTargetKey;

    internal static string SelectedTargetKey => _selectedTargetKey;

    internal static bool IsCollapsed(string familyId)
    {
        return !string.IsNullOrWhiteSpace(familyId) &&
               CollapsedFamilies.Contains(familyId.Trim());
    }

    internal static void ToggleCollapsed(string familyId)
    {
        string key = (familyId ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return;
        }

        if (!CollapsedFamilies.Add(key))
        {
            CollapsedFamilies.Remove(key);
        }
    }

    internal static void RememberTarget(in WeatherSpatialTarget target)
    {
        _selectedTargetKey = target.Key;
    }

    internal static int FindRememberedTargetIndex()
    {
        if (string.IsNullOrEmpty(_selectedTargetKey))
        {
            return -1;
        }

        for (int i = 0; i < WeatherSpatialCatalog.AllTargets.Count; i++)
        {
            if (string.Equals(
                    WeatherSpatialCatalog.AllTargets[i].Key,
                    _selectedTargetKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
