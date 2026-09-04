using System;
using System.Reflection;

namespace DryCycle.Weather.Scheduling;

/// <summary>
/// Invalidates the concrete per-game weather schedule after destructive DevUI
/// configuration changes. WeatherScheduleRuntime intentionally owns its state table;
/// this bridge keeps the destructive editor action isolated without restarting the
/// whole weather runtime (which would also tear down Weather Zones DevUI hooks).
/// </summary>
internal static class WeatherScheduleCacheInvalidation
{
    private static readonly FieldInfo StatesField = typeof(WeatherScheduleRuntime).GetField(
        "_states",
        BindingFlags.Static | BindingFlags.NonPublic);

    internal static void InvalidateAll()
    {
        try
        {
            if (StatesField == null)
            {
                Plugin.Logger?.LogWarning(
                    "DryCycle weather schedule cache invalidation could not find WeatherScheduleRuntime._states.");
            }
            else
            {
                object replacement = Activator.CreateInstance(StatesField.FieldType);
                StatesField.SetValue(null, replacement);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                "DryCycle weather schedule cache invalidation failed: " + ex.Message);
        }

        // HUD forecast data is stored separately from the concrete runtime schedule.
        // Clear it even if the state-table reset above ever fails so deleted regional
        // weather never remains visible as a stale forecast.
        WeatherForecastTimeline.Reset();
    }
}
