using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

/// <summary>
/// Materializes weather owners for an explicit editor Preview in rooms that were
/// already loaded before Preview was enabled. Normal schedule ownership remains in
/// each weather runtime; this class only repairs the developer-preview lifecycle.
/// </summary>
internal static class WeatherSpatialPreviewProvisioning
{
    internal static void Ensure(
        World world,
        WeatherScheduleEventKind kind,
        string weatherId)
    {
        if (world?.game?.cameras == null || string.IsNullOrWhiteSpace(weatherId))
        {
            return;
        }

        string normalized = WeatherSpatialCatalog.NormalizeId(weatherId);
        for (int i = 0; i < world.game.cameras.Length; i++)
        {
            Room room = world.game.cameras[i]?.room;
            if (room?.world != world)
            {
                continue;
            }

            if (kind == WeatherScheduleEventKind.Weather &&
                (normalized == "FOG" || normalized == "DENSEFOG"))
            {
                FogWeatherRuntime.EnsurePreviewController(room);
                continue;
            }

            if ((kind == WeatherScheduleEventKind.Weather && normalized == "SANDSTORM") ||
                (kind == WeatherScheduleEventKind.DangerType &&
                 (normalized == "SANDSTORM" || normalized == "DEATHSANDSTORM")))
            {
                SandstormWeatherRuntime.EnsurePreviewStorm(room);
                continue;
            }

            if (kind == WeatherScheduleEventKind.DangerType && normalized == "DEATHRAIN")
            {
                RainWeatherRuntime.EnsurePreviewCarrier(room);
            }
        }
    }
}
