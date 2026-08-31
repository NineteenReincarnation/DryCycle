using System;
using System.Runtime.CompilerServices;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.DayNight;

/// <summary>
/// Keeps DryCycle's game-wide clock continuous across World/Region replacements.
/// Enabled regions use the DryCycle clock directly. Disabled regions keep vanilla
/// RainCycle behavior, while a previously-created DryCycle clock shadow-advances from
/// the vanilla timer so elapsed time is not lost when the player crosses back.
/// </summary>
internal static class WorldClockRegionContinuityRuntime
{
    private sealed class RainCycleGateState
    {
        internal bool WasDisabled;
    }

    private static ConditionalWeakTable<RainCycle, RainCycleGateState> _gateStates = new();
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
        _gateStates = new ConditionalWeakTable<RainCycle, RainCycleGateState>();
        _enabled = false;
    }

    private static void RainCycle_Update(
        On.RainCycle.orig_Update orig,
        RainCycle self)
    {
        if (self?.world?.game == null || !self.world.game.IsStorySession)
        {
            orig(self);
            return;
        }

        bool disabled = !RegionDayNightOptions.IsEnabled(self.world);
        bool hasClock = WorldClockHooks.TryGetClock(self.world.game, out WorldClock clock);
        RainCycleGateState gateState = _gateStates.GetOrCreateValue(self);

        // If a running RainCycle changes from DryCycle ownership to vanilla ownership
        // (region gate or live Remix toggle), seed vanilla with the SAME phase progress
        // instead of exposing DryCycle's deliberately fixed safe facade timer.
        if (disabled && hasClock && !gateState.WasDisabled)
        {
            AlignVanillaTimerToClock(self, clock);
        }

        int beforeTimer = disabled && hasClock ? self.timer : 0;
        orig(self);

        gateState.WasDisabled = disabled;

        if (!disabled || !hasClock || clock == null)
        {
            return;
        }

        // Match the enabled-region loading rule: gate/world loading must not consume
        // hidden DryCycle time while the player is not actually in live gameplay.
        if (!WorldClockHooks.HasLiveGameplay(self.world.game))
        {
            return;
        }

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
        if (destination?.game == null ||
            !destination.game.IsStorySession ||
            destination.rainCycle == null)
        {
            return;
        }

        if (RegionDayNightOptions.IsEnabled(destination))
        {
            // A destination world may be the first DryCycle-enabled region visited in
            // this RainWorldGame. Ensure a clock exists and import the copied vanilla
            // timer if necessary; GetOrCreate handles that bootstrap path.
            if (WorldClockHooks.TryEnsureClock(destination.rainCycle, out WorldClock clock))
            {
                clock.SetCycleLength(Math.Max(1, destination.rainCycle.cycleLength));
            }

            WeatherScheduleRuntime.Synchronize(destination);
            return;
        }

        // Enabled -> disabled is the inverse boundary. Vanilla must receive the actual
        // elapsed phase position, not the safe midpoint RainCycle value that DryCycle
        // keeps while it owns the source region.
        if (WorldClockHooks.TryGetClock(destination.game, out WorldClock hiddenClock))
        {
            hiddenClock.SetCycleLength(Math.Max(1, destination.rainCycle.cycleLength));
            AlignVanillaTimerToClock(destination.rainCycle, hiddenClock);
        }
    }

    private static void AlignVanillaTimerToClock(RainCycle rainCycle, WorldClock clock)
    {
        if (rainCycle == null || clock == null)
        {
            return;
        }

        int length = Math.Max(1, rainCycle.cycleLength);
        rainCycle.timer = Mathf.Clamp(clock.VirtualRainTimer(length), 0, Math.Max(0, length - 1));
        rainCycle.deathRainHasHit = false;

        // Vanilla's own dayNightCounter normally does not begin until after its rain
        // timer reaches sunset/death-rain territory. Do not leak DryCycle's night-half
        // 0..10000 compatibility counter into a region whose switch is disabled.
        rainCycle.dayNightCounter = 0;
    }
}
