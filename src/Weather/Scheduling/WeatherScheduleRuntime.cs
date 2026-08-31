using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Climate;

namespace DryCycle.Weather.Scheduling;

/// <summary>
/// Converts a RegionClimate profile into one concrete schedule for the current
/// day OR night phase. Schedules are regenerated when the region changes, while the
/// WorldClock itself is never reset; entering a new region therefore observes that
/// region's schedule at the already-elapsed global time.
/// </summary>
internal static class WeatherScheduleRuntime
{
    private sealed class GameState
    {
        internal string RegionId;
        internal int DayIndex = int.MinValue;
        internal WeatherSchedulePhase Phase;
        internal int PhasePipCount = -1;
        internal WeatherPhaseSchedule Schedule;
    }

    private static ConditionalWeakTable<RainWorldGame, GameState> _states = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RainCycle.Update += RainCycle_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RainCycle.Update -= RainCycle_Update;
        _states = new ConditionalWeakTable<RainWorldGame, GameState>();
        WeatherForecastTimeline.Reset();
        _enabled = false;
    }

    internal static void Synchronize(World world)
    {
        if (world?.game == null ||
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            return;
        }

        Synchronize(world, clock);
    }

    internal static bool TryGetCurrentSchedule(
        World world,
        out WeatherPhaseSchedule schedule)
    {
        schedule = null;
        if (world?.game == null ||
            !_states.TryGetValue(world.game, out GameState state))
        {
            return false;
        }

        schedule = state.Schedule;
        return schedule != null;
    }

    internal static float GetIntensity(
        World world,
        WorldClock clock,
        WeatherScheduleEventKind kind,
        params string[] ids)
    {
        if (world?.game == null ||
            clock == null ||
            ids == null ||
            ids.Length == 0 ||
            !_states.TryGetValue(world.game, out GameState state) ||
            state.Schedule == null)
        {
            return 0f;
        }

        WeatherSchedulePhase expectedPhase = clock.IsNight
            ? WeatherSchedulePhase.Night
            : WeatherSchedulePhase.Day;
        if (state.Schedule.Phase != expectedPhase)
        {
            return 0f;
        }

        long phaseTicks = CurrentPhaseTicks(clock);
        for (int i = 0; i < state.Schedule.Events.Count; i++)
        {
            ScheduledWeatherEvent scheduled = state.Schedule.Events[i];
            if (scheduled?.Candidate == null || scheduled.Candidate.Kind != kind)
            {
                continue;
            }

            bool idMatch = false;
            for (int idIndex = 0; idIndex < ids.Length; idIndex++)
            {
                if (string.Equals(
                        scheduled.Candidate.Id,
                        ids[idIndex],
                        StringComparison.OrdinalIgnoreCase))
                {
                    idMatch = true;
                    break;
                }
            }

            if (!idMatch)
            {
                continue;
            }

            float intensity = EventEnvelope(scheduled, phaseTicks);
            if (intensity > 0f)
            {
                return intensity;
            }
        }

        return 0f;
    }

    private static void RainCycle_Update(
        On.RainCycle.orig_Update orig,
        RainCycle self)
    {
        // WorldClockHooks is registered before this runtime. Calling orig first lets
        // its clock advance for this tick; scheduling then observes the new phase/time.
        orig(self);

        if (self?.world?.game == null ||
            !self.world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(self.world) ||
            !WorldClockHooks.TryGetClock(self.world, out WorldClock clock))
        {
            return;
        }

        Synchronize(self.world, clock);
    }

    private static void Synchronize(World world, WorldClock clock)
    {
        string regionId = world.region?.name?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(regionId))
        {
            return;
        }

        WeatherSchedulePhase phase = clock.IsNight
            ? WeatherSchedulePhase.Night
            : WeatherSchedulePhase.Day;
        int phasePipCount = WeatherPhaseScheduler.FullPipsFromTicks(clock.CurrentHalfLength);

        GameState state = _states.GetOrCreateValue(world.game);
        if (string.Equals(state.RegionId, regionId, StringComparison.OrdinalIgnoreCase) &&
            state.DayIndex == clock.DayIndex &&
            state.Phase == phase &&
            state.PhasePipCount == phasePipCount &&
            state.Schedule != null)
        {
            return;
        }

        WeatherPhaseSchedule schedule = BuildSchedule(
            world.game,
            regionId,
            clock.DayIndex,
            phase,
            phasePipCount);

        state.RegionId = regionId;
        state.DayIndex = clock.DayIndex;
        state.Phase = phase;
        state.PhasePipCount = phasePipCount;
        state.Schedule = schedule;

        WeatherForecastTimeline.SetPhaseSchedule(world.game, schedule);
        LogSchedule(regionId, schedule, CurrentPhaseTicks(clock));
    }

    private static WeatherPhaseSchedule BuildSchedule(
        RainWorldGame game,
        string regionId,
        int dayIndex,
        WeatherSchedulePhase phase,
        int phasePipCount)
    {
        Random random = new(BuildSeed(game, regionId, dayIndex, phase));
        List<WeatherScheduleCandidate> candidates = RollCandidates(regionId, random);

        return phase == WeatherSchedulePhase.Day
            ? WeatherPhaseScheduler.BuildDay(phasePipCount, candidates, random)
            : WeatherPhaseScheduler.BuildNight(phasePipCount, candidates, random);
    }

    private static List<WeatherScheduleCandidate> RollCandidates(
        string regionId,
        Random random)
    {
        List<WeatherScheduleCandidate> result = new();
        if (!RegionClimateRegistry.TryGetProfile(regionId, out RegionClimateProfile profile))
        {
            return result;
        }

        for (int i = 0; i < profile.Weather.Count; i++)
        {
            WeatherFamilyClimateEntry family = profile.Weather[i];
            if (!Passes(family.ChancePercent, random))
            {
                continue;
            }

            if (family.Variants.Count == 0)
            {
                result.Add(new WeatherScheduleCandidate(
                    family.Id,
                    WeatherScheduleEventKind.Weather));
                continue;
            }

            // Variant percentages are independent probabilities, never normalized
            // weights. Multiple variants may pass; the phase scheduler later applies
            // the day/night event count and spacing limits.
            for (int variantIndex = 0; variantIndex < family.Variants.Count; variantIndex++)
            {
                ClimateChanceEntry variant = family.Variants[variantIndex];
                if (Passes(variant.ChancePercent, random))
                {
                    result.Add(new WeatherScheduleCandidate(
                        variant.Id,
                        WeatherScheduleEventKind.Weather));
                }
            }
        }

        for (int i = 0; i < profile.DangerTypes.Count; i++)
        {
            ClimateChanceEntry danger = profile.DangerTypes[i];
            if (Passes(danger.ChancePercent, random))
            {
                result.Add(new WeatherScheduleCandidate(
                    danger.Id,
                    WeatherScheduleEventKind.DangerType));
            }
        }

        return result;
    }

    private static bool Passes(float chancePercent, Random random)
    {
        if (chancePercent <= 0f)
        {
            return false;
        }

        if (chancePercent >= 100f)
        {
            return true;
        }

        return random.NextDouble() * 100d < chancePercent;
    }

    private static float EventEnvelope(ScheduledWeatherEvent scheduled, long phaseTicks)
    {
        long start = (long)scheduled.StartPip * WeatherPhaseScheduler.PipTicks;
        long end = (long)scheduled.EndPipExclusive * WeatherPhaseScheduler.PipTicks;
        if (phaseTicks < start || phaseTicks >= end)
        {
            return 0f;
        }

        long local = phaseTicks - start;
        long duration = end - start;
        long fadeTicks = Math.Min(WeatherPhaseScheduler.PipTicks / 2, duration / 2);
        if (fadeTicks <= 0)
        {
            return 1f;
        }

        float fadeIn = Math.Min(1f, (float)local / fadeTicks);
        float fadeOut = Math.Min(1f, (float)(duration - local) / fadeTicks);
        float t = Math.Max(0f, Math.Min(fadeIn, fadeOut));
        return t * t * (3f - 2f * t);
    }

    private static long CurrentPhaseTicks(WorldClock clock)
    {
        if (clock == null)
        {
            return 0;
        }

        return (long)Math.Round(
            Math.Max(0f, Math.Min(1f, clock.HalfProgress)) * clock.CurrentHalfLength);
    }

    private static int BuildSeed(
        RainWorldGame game,
        string regionId,
        int dayIndex,
        WeatherSchedulePhase phase)
    {
        unchecked
        {
            uint hash = 2166136261u;
            int saveSeed = 0;
            try
            {
                if (game?.GetStorySession?.saveState != null)
                {
                    saveSeed = game.GetStorySession.saveState.seed;
                }
            }
            catch
            {
                saveSeed = 0;
            }

            AddInt(ref hash, saveSeed);
            AddInt(ref hash, dayIndex);
            AddInt(ref hash, (int)phase);

            string normalized = regionId ?? string.Empty;
            for (int i = 0; i < normalized.Length; i++)
            {
                hash ^= char.ToUpperInvariant(normalized[i]);
                hash *= 16777619u;
            }

            return (int)(hash & 0x7FFFFFFF);
        }
    }

    private static void AddInt(ref uint hash, int value)
    {
        unchecked
        {
            hash ^= (byte)value;
            hash *= 16777619u;
            hash ^= (byte)(value >> 8);
            hash *= 16777619u;
            hash ^= (byte)(value >> 16);
            hash *= 16777619u;
            hash ^= (byte)(value >> 24);
            hash *= 16777619u;
        }
    }

    private static void LogSchedule(
        string regionId,
        WeatherPhaseSchedule schedule,
        long currentPhaseTicks)
    {
        if (schedule == null)
        {
            return;
        }

        Plugin.Logger?.LogInfo(
            $"DryCycle weather schedule {regionId} {schedule.Phase}: " +
            $"phase={schedule.PhasePipCount} pips, elapsed={currentPhaseTicks / 1200f:0.00} pips, " +
            $"events={schedule.Events.Count}, cancelled={schedule.CancelledCandidates.Count}.");

        for (int i = 0; i < schedule.Events.Count; i++)
        {
            Plugin.Logger?.LogInfo($"  {schedule.Events[i]}");
        }
    }
}
