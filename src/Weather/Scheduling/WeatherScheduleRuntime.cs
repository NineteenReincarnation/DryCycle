using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Spatial;

namespace DryCycle.Weather.Scheduling;

/// <summary>
/// Converts a RegionClimate profile into one concrete schedule for the current
/// day or night phase, then exposes room-local intensity through WeatherSpatialRuntime.
/// </summary>
internal static class WeatherScheduleRuntime
{
    private const float HeavyRainMaxIntensity = 0.70f;

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

        WeatherSpatialRuntime.Enable();
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
        WeatherSpatialRuntime.Disable();
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
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world))
        {
            return 0f;
        }

        // Preview is an explicit developer force state. Once it matches this weather,
        // bypass the random schedule and room spatial Allow/Forbidden rules entirely.
        // That keeps Weather and DangerType previews consistent and lets developers
        // inspect effects such as IntenseHeat in any room without authoring a test rule.
        if (WeatherSpatialPreview.TryGetIntensity(
                world,
                kind,
                ids,
                out float previewIntensity,
                out _))
        {
            return previewIntensity;
        }

        float bestIntensity = 0f;

        // GetIntensity is called from many owners, not only RainCycle.Update. Keep the
        // schedule synchronized here so a world/region replacement cannot leak the old
        // region schedule for one frame.
        Synchronize(world, clock);

        if (!_states.TryGetValue(world.game, out GameState state) || state.Schedule == null)
        {
            return bestIntensity;
        }

        string regionId = world.region?.name?.Trim().ToUpperInvariant();
        WeatherSchedulePhase expectedPhase = clock.IsNight
            ? WeatherSchedulePhase.Night
            : WeatherSchedulePhase.Day;
        int expectedPips = WeatherPhaseScheduler.FullPipsFromTicks(clock.CurrentHalfLength);

        if (string.IsNullOrEmpty(regionId) ||
            !string.Equals(state.RegionId, regionId, StringComparison.OrdinalIgnoreCase) ||
            state.DayIndex != clock.DayIndex ||
            state.Phase != expectedPhase ||
            state.PhasePipCount != expectedPips ||
            state.Schedule.Phase != expectedPhase ||
            state.Schedule.PhasePipCount != expectedPips)
        {
            return bestIntensity;
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
            if (intensity > 0f && IsScheduledHeavyRain(scheduled))
            {
                intensity *= HeavyRainMaxIntensity;
            }
            if (intensity <= 0f)
            {
                continue;
            }

            float localIntensity = WeatherSpatialRuntime.ApplyIntensity(
                world,
                kind,
                scheduled.Candidate.Id,
                intensity);
            if (localIntensity > bestIntensity)
            {
                bestIntensity = localIntensity;
            }
        }

        return bestIntensity;
    }

    private static void RainCycle_Update(On.RainCycle.orig_Update orig, RainCycle self)
    {
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
        if (world?.game == null || clock == null)
        {
            return;
        }

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
            if (family == null || IsRemovedScheduledWeather(family.Id))
            {
                continue;
            }

            if (family.Variants.Count == 0)
            {
                if (!WeatherTypeRegistry.IsSchedulable(
                        family.Id,
                        WeatherScheduleEventKind.Weather))
                {
                    WeatherTypeRegistry.WarnUnsupported(
                        regionId,
                        family.Id,
                        WeatherScheduleEventKind.Weather);
                    continue;
                }

                if (Passes(family.ChancePercent, random))
                {
                    result.Add(new WeatherScheduleCandidate(
                        family.Id,
                        WeatherScheduleEventKind.Weather));
                }
                continue;
            }

            bool hasSchedulableVariant = false;
            for (int variantIndex = 0; variantIndex < family.Variants.Count; variantIndex++)
            {
                ClimateChanceEntry variant = family.Variants[variantIndex];
                if (variant == null || IsRemovedScheduledWeather(variant.Id))
                {
                    continue;
                }

                if (WeatherTypeRegistry.IsSchedulable(
                        variant.Id,
                        WeatherScheduleEventKind.Weather))
                {
                    hasSchedulableVariant = true;
                }
                else
                {
                    WeatherTypeRegistry.WarnUnsupported(
                        regionId,
                        variant.Id,
                        WeatherScheduleEventKind.Weather);
                }
            }

            if (!hasSchedulableVariant || !Passes(family.ChancePercent, random))
            {
                continue;
            }

            for (int variantIndex = 0; variantIndex < family.Variants.Count; variantIndex++)
            {
                ClimateChanceEntry variant = family.Variants[variantIndex];
                if (variant == null ||
                    IsRemovedScheduledWeather(variant.Id) ||
                    !WeatherTypeRegistry.IsSchedulable(
                        variant.Id,
                        WeatherScheduleEventKind.Weather))
                {
                    continue;
                }

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
            if (danger == null)
            {
                continue;
            }

            if (!WeatherTypeRegistry.IsSchedulable(
                    danger.Id,
                    WeatherScheduleEventKind.DangerType))
            {
                WeatherTypeRegistry.WarnUnsupported(
                    regionId,
                    danger.Id,
                    WeatherScheduleEventKind.DangerType);
                continue;
            }

            if (Passes(danger.ChancePercent, random))
            {
                result.Add(new WeatherScheduleCandidate(
                    danger.Id,
                    WeatherScheduleEventKind.DangerType));
            }
        }

        return result;
    }

    private static bool IsRemovedScheduledWeather(string id)
    {
        return string.Equals(
            NormalizeWeatherId(id),
            "BULLETRAIN",
            StringComparison.Ordinal);
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
        if (scheduled == null)
        {
            return 0f;
        }

        long mainStart = (long)scheduled.StartPip * WeatherPhaseScheduler.PipTicks;
        long mainEnd = (long)scheduled.EndPipExclusive * WeatherPhaseScheduler.PipTicks;
        long transition = WeatherPhaseScheduler.EventTransitionTicks;
        long effectStart = mainStart - transition;
        long effectEnd = mainEnd + transition;

        if (phaseTicks < effectStart || phaseTicks >= effectEnd)
        {
            return 0f;
        }
        if (phaseTicks < mainStart)
        {
            float t = transition <= 0
                ? 1f
                : (phaseTicks - effectStart) / (float)transition;
            return Smooth01(t);
        }
        if (phaseTicks < mainEnd)
        {
            return 1f;
        }

        float tail = transition <= 0
            ? 0f
            : (effectEnd - phaseTicks) / (float)transition;
        return Smooth01(tail);
    }

    private static bool IsScheduledHeavyRain(ScheduledWeatherEvent scheduled)
    {
        return scheduled?.Candidate != null &&
               scheduled.Candidate.Kind == WeatherScheduleEventKind.Weather &&
               string.Equals(
                   NormalizeWeatherId(scheduled.Candidate.Id),
                   "HEAVYRAIN",
                   StringComparison.Ordinal);
    }

    private static string NormalizeWeatherId(string id)
    {
        return (id ?? string.Empty)
            .Trim()
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .ToUpperInvariant();
    }

    private static float Smooth01(float value)
    {
        float t = Math.Max(0f, Math.Min(1f, value));
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
            int saveCycle = 0;
            try
            {
                if (game?.GetStorySession?.saveState != null)
                {
                    saveSeed = game.GetStorySession.saveState.seed;
                    saveCycle = game.GetStorySession.saveState.cycleNumber;
                }
            }
            catch
            {
                saveSeed = 0;
                saveCycle = 0;
            }

            AddInt(ref hash, saveSeed);
            AddInt(ref hash, saveCycle);
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
            ScheduledWeatherEvent scheduled = schedule.Events[i];
            Plugin.Logger?.LogInfo($"  {scheduled}");
            Plugin.Logger?.LogInfo(
                "    Transition: -15s fade-in, authored pips use full base envelope, +15s fade-out (30s excluded from duration).");
        }
    }
}
