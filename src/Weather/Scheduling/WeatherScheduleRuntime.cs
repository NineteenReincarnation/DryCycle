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
    // Native GlobalRain uses a 60-tick (1.5 second at 40 Hz) linear ramp on both
    // sides of HeavyRainFlux's wet/dry plateaus.
    private const int NativeHeavyRainFluxRampTicks = 60;

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
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world))
        {
            return 0f;
        }

        // Intensity reads happen from GlobalRain, RoomRain, Player and Watcher hooks,
        // not only RainCycle.Update. A region/world replacement can therefore occur
        // before the next RainCycle tick. Synchronize here so no caller can observe the
        // previous region/day/phase schedule for even one frame.
        Synchronize(world, clock);

        if (!_states.TryGetValue(world.game, out GameState state) ||
            state.Schedule == null)
        {
            return 0f;
        }

        string regionId = world.region?.name?.Trim().ToUpperInvariant();
        WeatherSchedulePhase expectedPhase = clock.IsNight
            ? WeatherSchedulePhase.Night
            : WeatherSchedulePhase.Day;
        int expectedPips = WeatherPhaseScheduler.FullPipsFromTicks(clock.CurrentHalfLength);

        // Fail closed if synchronization could not establish a schedule for this exact
        // world state (for example during an incomplete region transition frame).
        if (string.IsNullOrEmpty(regionId) ||
            !string.Equals(state.RegionId, regionId, StringComparison.OrdinalIgnoreCase) ||
            state.DayIndex != clock.DayIndex ||
            state.Phase != expectedPhase ||
            state.PhasePipCount != expectedPips ||
            state.Schedule.Phase != expectedPhase ||
            state.Schedule.PhasePipCount != expectedPips)
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
            if (intensity > 0f && IsScheduledHeavyRain(scheduled))
            {
                intensity *= HeavyRainFluxMultiplier(scheduled, phaseTicks);
            }

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

        WeatherPhaseSchedule schedule = phase == WeatherSchedulePhase.Day
            ? WeatherPhaseScheduler.BuildDay(phasePipCount, candidates, random)
            : WeatherPhaseScheduler.BuildNight(phasePipCount, candidates, random);

        // Event-specific parameters are rolled only after placement. This keeps the
        // existing event selection/duration/position stream unchanged while making
        // the assigned Flux deterministic for the same save/region/day/phase seed.
        AssignEventParameters(schedule, random);
        return schedule;
    }

    private static void AssignEventParameters(
        WeatherPhaseSchedule schedule,
        Random random)
    {
        if (schedule?.Events == null || random == null)
        {
            return;
        }

        for (int i = 0; i < schedule.Events.Count; i++)
        {
            ScheduledWeatherEvent scheduled = schedule.Events[i];
            if (!IsScheduledHeavyRain(scheduled))
            {
                continue;
            }

            // The room-editor HeavyRainFlux slider is a 0..1 value. Use the same
            // complete range here; the value is fixed for this schedule event and is
            // never rerolled every frame.
            scheduled.HeavyRainFlux = (float)random.NextDouble();
        }
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

            // Validate child runtimes before the family probability roll. A family
            // whose variants are all future/unimplemented weather therefore consumes
            // neither a schedule slot nor RNG state used by implemented weather.
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

            // Variant percentages are independent probabilities, never normalized
            // weights. Multiple variants may pass; the phase scheduler later applies
            // the day/night event count and spacing limits. Removed or unsupported IDs
            // are skipped before rolling so stale/future climate entries cannot consume
            // a schedule slot or perturb the implemented variant RNG stream.
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

    /// <summary>
    /// Every scheduled Weather and DangerType uses the same external envelope:
    /// 15 seconds fade-in before the first authored pip, the authored pips use the
    /// full base schedule envelope, then 15 seconds fade-out after the final pip.
    /// Per-weather modulation such as HeavyRainFlux is multiplied on top separately.
    /// The transition time is deliberately outside DurationPips.
    /// </summary>
    private static float EventEnvelope(
        ScheduledWeatherEvent scheduled,
        long phaseTicks)
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

    /// <summary>
    /// Reproduces the native HeavyRainFlux waveform with an event-local phase.
    /// Native GlobalRain uses: 1.5 s ramp, 30*flux s wet plateau, 1.5 s ramp,
    /// 30*flux s dry plateau. DryCycle rotates that periodic waveform so the first
    /// authored HeavyRain pip starts on the wet plateau; the separate 15-second
    /// event pre-roll therefore reaches full intensity cleanly instead of snapping
    /// back to zero at the first authored pip.
    /// </summary>
    private static float HeavyRainFluxMultiplier(
        ScheduledWeatherEvent scheduled,
        long phaseTicks)
    {
        if (scheduled == null || !scheduled.HeavyRainFlux.HasValue)
        {
            return 1f;
        }

        float flux = Math.Max(0f, Math.Min(1f, scheduled.HeavyRainFlux.Value));
        if (flux <= 0.0001f)
        {
            // Native HeavyRainFlux == 0 disables flux and leaves HeavyRain steady.
            return 1f;
        }

        long mainStart = (long)scheduled.StartPip * WeatherPhaseScheduler.PipTicks;
        if (phaseTicks < mainStart)
        {
            // Universal DryCycle pre-roll remains a single clean 15-second fade-in.
            return 1f;
        }

        long plateauTicks = Math.Max(
            1L,
            (long)Math.Round(WeatherPhaseScheduler.PipTicks * flux));
        long rampTicks = NativeHeavyRainFluxRampTicks;
        long period = plateauTicks * 2L + rampTicks * 2L;
        long local = (phaseTicks - mainStart) % period;

        // Wet plateau: native HeavyRain strength.
        if (local < plateauTicks)
        {
            return 1f;
        }

        local -= plateauTicks;

        // Native 1.5-second linear fall.
        if (local < rampTicks)
        {
            return Math.Max(0f, 1f - local / (float)rampTicks);
        }

        local -= rampTicks;

        // Dry plateau.
        if (local < plateauTicks)
        {
            return 0f;
        }

        local -= plateauTicks;

        // Native 1.5-second linear rise back into the next wet plateau.
        return Math.Max(0f, Math.Min(1f, local / (float)rampTicks));
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

            if (scheduled?.HeavyRainFlux.HasValue == true)
            {
                float flux = scheduled.HeavyRainFlux.Value;
                Plugin.Logger?.LogInfo(
                    $"    HeavyRainFlux={flux:0.###}: wet={30f * flux:0.##}s, dry={30f * flux:0.##}s, native ramps=1.5s each.");
            }
        }
    }
}
