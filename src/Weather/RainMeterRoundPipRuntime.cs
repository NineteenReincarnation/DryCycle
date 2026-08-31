using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Final RainMeter presentation pass for DryCycle.
///
/// It has two responsibilities:
/// 1) keep fully-solid timer pips on Rain World's procedural circular path instead of
///    snapping to the tiny Circle4 atlas graphic;
/// 2) turn every scheduled weather/danger pip into a white hollow time ring so the
///    colored forecast sprite drawn immediately behind it is actually visible.
///
/// This pass also synchronizes the current region schedule before the normal RainMeter
/// draw chain runs. That makes shelter wake-up, HUD reconstruction and region swaps read
/// the live WeatherScheduleRuntime directly rather than depending on a stale forecast
/// cache from a previous frame/region.
/// </summary>
internal static class RainMeterRoundPipRuntime
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.HUD.RainMeter.Draw += RainMeter_Draw;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.HUD.RainMeter.Draw -= RainMeter_Draw;
        _enabled = false;
    }

    private static void RainMeter_Draw(
        On.HUD.RainMeter.orig_Draw orig,
        global::HUD.RainMeter self,
        float timeStacker)
    {
        Player player = self?.hud?.owner as Player;
        World world = player?.abstractCreature?.world;

        WeatherPhaseSchedule schedule = null;
        WorldClock clock = null;

        if (self?.circles != null &&
            world?.game != null &&
            world.game.IsStorySession &&
            RegionDayNightOptions.IsEnabled(world) &&
            WorldClockHooks.TryGetClock(world, out clock))
        {
            // Do this before orig. WeatherForecastHudRuntime is inside this hook in the
            // current enable order, so its draw pass sees the freshly synchronized
            // timeline during the same frame rather than one frame later.
            WeatherScheduleRuntime.Synchronize(world);
            if (WeatherScheduleRuntime.TryGetCurrentSchedule(world, out WeatherPhaseSchedule current) &&
                current != null &&
                current.Phase == (clock.IsNight
                    ? WeatherSchedulePhase.Night
                    : WeatherSchedulePhase.Day))
            {
                schedule = current;
                WeatherForecastTimeline.SetPhaseSchedule(world.game, current);
            }
        }

        orig(self, timeStacker);

        if (self?.circles == null ||
            world == null ||
            clock == null ||
            !RegionDayNightOptions.IsEnabled(world))
        {
            return;
        }

        int count = self.circles.Length;
        float hudFade = Mathf.Clamp01(Mathf.Lerp(self.lastFade, self.fade, timeStacker));
        float sizeFade = hudFade * hudFade;

        for (int chronologicalPip = 1; chronologicalPip <= count; chronologicalPip++)
        {
            int index = clock.IsNight
                ? chronologicalPip - 1
                : count - chronologicalPip;
            if (index < 0 || index >= count)
            {
                continue;
            }

            global::HUD.HUDCircle circle = self.circles[index];
            if (circle?.sprite == null || !circle.sprite.isVisible)
            {
                continue;
            }

            if (HasForecastMarker(schedule, chronologicalPip))
            {
                // The colored forecast fill is a separate sprite immediately behind
                // this HUDCircle. A solid timer pip would cover it completely, which
                // was the reason scheduled weather appeared to have no marker. Keep
                // the time boundary white and hollow exactly like the original fixed
                // sandstorm prototype so the colored center remains readable.
                circle.snapGraphic = global::HUD.HUDCircle.SnapToGraphic.smallEmptyCircle;
                circle.snapRad = 3f;
                circle.snapThickness = 1f;
                circle.rad = 3f * sizeFade;
                circle.thickness = 1f * sizeFade;
                circle.forceColor = Color.white;
                circle.Draw(timeStacker);
                continue;
            }

            if (circle.snapGraphic != global::HUD.HUDCircle.SnapToGraphic.Circle4)
            {
                continue;
            }

            // Non-weather solid pips still use the ordinary time state; only prevent
            // the fully-solid state from snapping to the low-resolution Circle4.
            circle.snapGraphic = global::HUD.HUDCircle.SnapToGraphic.None;
            circle.snapRad = -1f;
            circle.snapThickness = -1f;
            circle.Draw(timeStacker);
        }
    }

    private static bool HasForecastMarker(
        WeatherPhaseSchedule schedule,
        int chronologicalPip)
    {
        if (schedule == null || chronologicalPip < 1)
        {
            return false;
        }

        int zeroBasedPip = chronologicalPip - 1;
        for (int i = 0; i < schedule.Events.Count; i++)
        {
            ScheduledWeatherEvent scheduled = schedule.Events[i];
            if (scheduled?.Candidate == null ||
                zeroBasedPip < scheduled.StartPip ||
                zeroBasedPip >= scheduled.EndPipExclusive)
            {
                continue;
            }

            // Unknown future weather IDs must not punch a hollow hole in the meter
            // until they have an authored visual language.
            if (WeatherForecastVisualCatalog.TryResolve(
                    scheduled.Candidate.Id,
                    scheduled.Candidate.Kind,
                    out WeatherForecastVisualKind kind) &&
                kind != WeatherForecastVisualKind.None)
            {
                return true;
            }
        }

        return false;
    }
}
