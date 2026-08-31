using System;
using DryCycle.Weather.Scheduling;

namespace DryCycle.DayNight;

/// <summary>
/// Keeps DryCycle's game-wide clock continuous across World/Region replacements.
/// Rain World creates a new RainCycle for the destination World and later copies the
/// old RainCycle fields into it. DryCycle therefore never creates a second regional
/// clock: the existing RainWorldGame clock remains authoritative and the destination
/// region only swaps its climate schedule around the already-elapsed phase time.
///
/// If the player temporarily enters a region where DryCycle is disabled, vanilla owns
/// that region completely. When a DryCycle clock already exists we shadow-advance it
/// from vanilla RainCycle timer deltas without writing anything back to that region,
/// so re-entering an enabled region does not resume stale time.
/// </summary>
internal static class WorldClockRegionContinuityRuntime
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RainCycle.Update += RainCycle_Update;
        On.OverWorld.WorldLoaded += OverWorld_WorldLoaded;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RainCycle.Update -= RainCycle_Update;
        On.OverWorld.WorldLoaded -= OverWorld_WorldLoaded;
        _enabled = false;
    }

    private static void RainCycle_Update(
        On.RainCycle.orig_Update orig,
        RainCycle self)
    {
        WorldClock clock = null;
        bool shadowOnly = self?.world?.game != null &&
                          self.world.game.IsStorySession &&
                          !RegionDayNightOptions.IsEnabled(self.world) &&
                          WorldClockHooks.TryGetClock(self.world.game, out clock);

        int beforeTimer = shadowOnly ? self.timer : 0;
        orig(self);

        if (!shadowOnly || clock == null)
        {
            return;
        }

        // This is observation only: no RainCycle fields are changed, so the disabled
        // region keeps its exact vanilla/DLC/mod behavior. The hidden global clock
        // simply consumes the same forward gameplay ticks.
        int advanced = Math.Max(0, self.timer - beforeTimer);
        if (advanced <= 0)
        {
            return;
        }

        clock.SetCycleLength(Math.Max(1, self.cycleLength));
        clock.Advance(advanced);
    }

    private static void OverWorld_WorldLoaded(
        On.OverWorld.orig_WorldLoaded orig,
        OverWorld self,
        bool warpUsed)
    {
        orig(self, warpUsed);

        World destination = self?.activeWorld;
        if (destination?.game == null || !destination.game.IsStorySession)
        {
            return;
        }

        // Vanilla has now finished copying the source RainCycle fields into the
        // destination World. Re-apply that final cycle length to the existing global
        // clock before scheduling, while SetCycleLength preserves the elapsed phase.
        if (destination.rainCycle != null &&
            WorldClockHooks.TryGetClock(destination.game, out WorldClock clock))
        {
            clock.SetCycleLength(Math.Max(1, destination.rainCycle.cycleLength));
        }

        // Do not carry the previous region's weather table through a gate. The global
        // clock is intentionally retained, but the destination profile is scheduled
        // against the current HalfProgress immediately after the World swap.
        WeatherScheduleRuntime.Synchronize(destination);
    }
}
