using DryCycle.Weather;
using DryCycle.Weather.Scheduling;

namespace DryCycle.DayNight;

/// <summary>
/// A successful shelter hibernation defines the boundary of a DryCycle round.
/// It does not matter whether the player entered the shelter during daytime or
/// nighttime: after survival is committed, the next round begins at Base/day 0.
///
/// RainWorldGame.Win is the authoritative success path used by ShelterDoor. We wait
/// until orig returns and verify that SaveState.cycleNumber actually advanced, so
/// merely entering a shelter, failing to hibernate, dying, or using unrelated process
/// transitions cannot reset the clock by accident.
/// </summary>
internal static class ShelterCycleResetRuntime
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RainWorldGame.Win += RainWorldGame_Win;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RainWorldGame.Win -= RainWorldGame_Win;
        _enabled = false;
    }

    private static void RainWorldGame_Win(
        On.RainWorldGame.orig_Win orig,
        RainWorldGame self,
        bool malnourished,
        bool fromWarpPoint)
    {
        int beforeCycle = GetStoryCycle(self);
        orig(self, malnourished, fromWarpPoint);

        // Warp-point saves are not shelter sleeps. A normal ShelterDoor.Win advances
        // SaveState.cycleNumber through SessionEnded/RainCycleTick; use that mutation
        // as the success acknowledgement rather than guessing from door state.
        if (fromWarpPoint ||
            self == null ||
            !self.IsStorySession ||
            GetStoryCycle(self) <= beforeCycle)
        {
            return;
        }

        RestartRound(self);
    }

    private static void RestartRound(RainWorldGame game)
    {
        if (!WorldClockHooks.TryGetClock(game, out WorldClock clock) || clock == null)
        {
            // A brand-new RainWorldGame will construct its WorldClock at day start
            // naturally. This branch mainly covers regions where DryCycle had never
            // created a clock during the just-finished cycle.
            return;
        }

        clock.ResetToDayStart(advanceDayIndex: true);

        RainCycle rainCycle = game.world?.rainCycle;
        if (rainCycle != null)
        {
            rainCycle.dayNightCounter = 0;
            rainCycle.deathRainHasHit = false;
        }

        // Never carry the previous day/night forecast through hibernation. Because
        // cycleNumber has already advanced and WorldClock.DayIndex advanced above,
        // Synchronize produces a genuinely new deterministic roll for this shelter
        // cycle at elapsed=0 rather than reusing the old table.
        WeatherForecastTimeline.Clear(game);
        if (game.world != null && RegionDayNightOptions.IsEnabled(game.world))
        {
            WeatherScheduleRuntime.Synchronize(game.world);
        }

        // If the current process remains visible for a frame while switching to the
        // sleep screen, force the authored Base palette immediately. The next Game
        // process also starts from the same zero-progress state.
        if (game.cameras != null)
        {
            for (int i = 0; i < game.cameras.Length; i++)
            {
                RoomCamera camera = game.cameras[i];
                if (camera?.room != null)
                {
                    PaletteLighting.ForceRefresh(camera);
                }
            }
        }

        Plugin.Logger?.LogInfo(
            $"DryCycle shelter cycle reset: cycle={GetStoryCycle(game)}, " +
            "phase=Day, progress=0, palette=Base; weather schedule regenerated.");
    }

    private static int GetStoryCycle(RainWorldGame game)
    {
        try
        {
            return game?.GetStorySession?.saveState?.cycleNumber ?? int.MinValue;
        }
        catch
        {
            return int.MinValue;
        }
    }
}
