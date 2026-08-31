using System;
using System.Collections.Generic;

namespace DryCycle.Weather.Scheduling;

/// <summary>
/// Builds one independent day OR night schedule on a RainMeter-pip grid.
///
/// Hard rules:
/// - one pip = 1200 ticks = 30 seconds;
/// - day: at most 2 Weather + 1 DangerType;
/// - night: at most 1 Weather + 1 DangerType;
/// - Weather duration: weighted 2..5 pips, alpha 0.68;
/// - DangerType duration: weighted 2..4 pips, alpha 0.5;
/// - DangerType may not start in the first 4 pips of the phase;
/// - DeathRain additionally owns a 15-second pre-roll and a 15-second tail outside
///   its authored 2..4 full pips. Its full-strength cells therefore begin only after
///   that transition can fit beyond the protected opening, and its tail must finish
///   before the phase ends;
/// - no events overlap;
/// - every pair of neighboring effects must have at least 2 completely empty pips
///   between them. Because DeathRain extends half a pip outside its authored cells,
///   any nominal gap adjacent to DeathRain is raised to 3 full pips so the actual
///   post-transition empty time is still at least 2 pips;
/// - if the selected events cannot fit, randomly cancel one and retry until the
///   remaining set has a legal layout.
/// </summary>
internal static class WeatherPhaseScheduler
{
    internal const int PipTicks = 1200;
    internal const int MinimumGapPips = 2;
    internal const int DangerProtectedOpeningPips = 4;
    internal const int DeathRainTransitionTicks = PipTicks / 2;

    internal const int DayMaxWeatherEvents = 2;
    internal const int DayMaxDangerEvents = 1;
    internal const int NightMaxWeatherEvents = 1;
    internal const int NightMaxDangerEvents = 1;

    private const int WeatherMinDurationPips = 2;
    private const int WeatherMaxDurationPips = 5;
    private const double WeatherDurationAlpha = 0.68;

    private const int DangerMinDurationPips = 2;
    private const int DangerMaxDurationPips = 4;
    private const double DangerDurationAlpha = 0.5;

    private sealed class PendingEvent
    {
        internal WeatherScheduleCandidate Candidate;
        internal int DurationPips;

        internal PendingEvent(WeatherScheduleCandidate candidate, int durationPips)
        {
            Candidate = candidate;
            DurationPips = durationPips;
        }
    }

    internal static WeatherPhaseSchedule BuildDay(
        int phasePipCount,
        IReadOnlyList<WeatherScheduleCandidate> candidates,
        Random random)
    {
        return Build(
            WeatherSchedulePhase.Day,
            phasePipCount,
            candidates,
            random);
    }

    internal static WeatherPhaseSchedule BuildNight(
        int phasePipCount,
        IReadOnlyList<WeatherScheduleCandidate> candidates,
        Random random)
    {
        return Build(
            WeatherSchedulePhase.Night,
            phasePipCount,
            candidates,
            random);
    }

    internal static WeatherPhaseSchedule Build(
        WeatherSchedulePhase phase,
        int phasePipCount,
        IReadOnlyList<WeatherScheduleCandidate> candidates,
        Random random)
    {
        random ??= new Random();
        phasePipCount = Math.Max(0, phasePipCount);

        List<WeatherScheduleCandidate> cancelled = new();
        List<WeatherScheduleCandidate> selected = SelectWithinPhaseCaps(
            phase,
            candidates,
            cancelled,
            random);

        List<PendingEvent> pending = new(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            WeatherScheduleCandidate candidate = selected[i];
            int duration = candidate.Kind == WeatherScheduleEventKind.DangerType
                ? ChooseDangerDurationPips(random)
                : ChooseWeatherDurationPips(random);

            pending.Add(new PendingEvent(candidate, duration));
        }

        while (pending.Count > 0)
        {
            if (TryPlaceAll(phasePipCount, pending, random, out List<ScheduledWeatherEvent> events))
            {
                events.Sort((a, b) => a.StartPip.CompareTo(b.StartPip));
                return new WeatherPhaseSchedule(phase, phasePipCount, events, cancelled);
            }

            // The rules are not weakened to force a fit. If there is no legal layout,
            // randomly remove one event exactly as specified and try the remaining set.
            int removeIndex = random.Next(pending.Count);
            cancelled.Add(pending[removeIndex].Candidate);
            pending.RemoveAt(removeIndex);
        }

        return new WeatherPhaseSchedule(
            phase,
            phasePipCount,
            new List<ScheduledWeatherEvent>(),
            cancelled);
    }

    internal static int ChooseWeatherDurationPips(Random random)
    {
        return ChooseWeightedDurationPips(
            WeatherMinDurationPips,
            WeatherMaxDurationPips,
            WeatherDurationAlpha,
            random);
    }

    internal static int ChooseDangerDurationPips(Random random)
    {
        return ChooseWeightedDurationPips(
            DangerMinDurationPips,
            DangerMaxDurationPips,
            DangerDurationAlpha,
            random);
    }

    internal static int FullPipsFromTicks(int ticks)
    {
        return Math.Max(0, ticks) / PipTicks;
    }

    internal static bool HasDeathRainTransition(WeatherScheduleCandidate candidate)
    {
        if (candidate == null || candidate.Kind != WeatherScheduleEventKind.DangerType)
        {
            return false;
        }

        string normalized = candidate.Id?.Trim()
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .ToUpperInvariant();

        return normalized == "DEATHRAIN" || normalized == "RAIN";
    }

    private static int ChooseWeightedDurationPips(
        int minPips,
        int maxPips,
        double alpha,
        Random random)
    {
        random ??= new Random();

        double totalWeight = 0d;
        for (int pips = minPips; pips <= maxPips; pips++)
        {
            totalWeight += Math.Pow(maxPips + 1 - pips, alpha);
        }

        double roll = random.NextDouble() * totalWeight;
        for (int pips = minPips; pips <= maxPips; pips++)
        {
            double weight = Math.Pow(maxPips + 1 - pips, alpha);
            if (roll < weight)
            {
                return pips;
            }

            roll -= weight;
        }

        return maxPips;
    }

    private static List<WeatherScheduleCandidate> SelectWithinPhaseCaps(
        WeatherSchedulePhase phase,
        IReadOnlyList<WeatherScheduleCandidate> candidates,
        List<WeatherScheduleCandidate> cancelled,
        Random random)
    {
        List<WeatherScheduleCandidate> weather = new();
        List<WeatherScheduleCandidate> danger = new();

        if (candidates != null)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                WeatherScheduleCandidate candidate = candidates[i];
                if (candidate == null)
                {
                    continue;
                }

                if (candidate.Kind == WeatherScheduleEventKind.DangerType)
                {
                    danger.Add(candidate);
                }
                else
                {
                    weather.Add(candidate);
                }
            }
        }

        Shuffle(weather, random);
        Shuffle(danger, random);

        int weatherCap = phase == WeatherSchedulePhase.Day
            ? DayMaxWeatherEvents
            : NightMaxWeatherEvents;
        int dangerCap = phase == WeatherSchedulePhase.Day
            ? DayMaxDangerEvents
            : NightMaxDangerEvents;

        List<WeatherScheduleCandidate> selected = new(
            Math.Min(weather.Count, weatherCap) + Math.Min(danger.Count, dangerCap));

        KeepFirst(weather, weatherCap, selected, cancelled);
        KeepFirst(danger, dangerCap, selected, cancelled);
        Shuffle(selected, random);
        return selected;
    }

    private static void KeepFirst(
        List<WeatherScheduleCandidate> source,
        int cap,
        List<WeatherScheduleCandidate> selected,
        List<WeatherScheduleCandidate> cancelled)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (i < cap)
            {
                selected.Add(source[i]);
            }
            else
            {
                cancelled.Add(source[i]);
            }
        }
    }

    private static bool TryPlaceAll(
        int phasePipCount,
        List<PendingEvent> pending,
        Random random,
        out List<ScheduledWeatherEvent> events)
    {
        events = new List<ScheduledWeatherEvent>(pending.Count);
        if (pending.Count == 0)
        {
            return true;
        }

        // Search order is randomized, but the recursive search remains exhaustive for
        // that order. Therefore an event is cancelled only when no legal arrangement
        // exists, not merely because a greedy random placement happened to fail.
        List<PendingEvent> searchOrder = new(pending);
        Shuffle(searchOrder, random);

        return TryPlaceRecursive(
            phasePipCount,
            searchOrder,
            eventIndex: 0,
            placed: events,
            random);
    }

    private static bool TryPlaceRecursive(
        int phasePipCount,
        List<PendingEvent> searchOrder,
        int eventIndex,
        List<ScheduledWeatherEvent> placed,
        Random random)
    {
        if (eventIndex >= searchOrder.Count)
        {
            return true;
        }

        PendingEvent pending = searchOrder[eventIndex];
        bool deathRainTransition = HasDeathRainTransition(pending.Candidate);

        int earliestStart = pending.Candidate.Kind == WeatherScheduleEventKind.DangerType
            ? DangerProtectedOpeningPips
            : 0;

        // DeathRain begins fading in half a pip before its first full-strength cell.
        // Since starts are integer pip boundaries, moving the full-strength start one
        // additional pip later is the first placement that keeps the entire pre-roll
        // outside the protected first four pips.
        if (deathRainTransition)
        {
            earliestStart = Math.Max(
                earliestStart,
                DangerProtectedOpeningPips + 1);
        }

        // Reserve one trailing integer cell for DeathRain. Its actual fade-out only
        // consumes the first half of that cell, but this guarantees the complete
        // 15-second tail finishes before Day/Night switches phase.
        int trailingReservePips = deathRainTransition ? 1 : 0;
        int latestStart = phasePipCount - pending.DurationPips - trailingReservePips;

        if (latestStart < earliestStart)
        {
            return false;
        }

        List<int> starts = new(latestStart - earliestStart + 1);
        for (int start = earliestStart; start <= latestStart; start++)
        {
            if (CanPlace(
                    pending.Candidate,
                    start,
                    pending.DurationPips,
                    placed))
            {
                starts.Add(start);
            }
        }

        Shuffle(starts, random);

        for (int i = 0; i < starts.Count; i++)
        {
            ScheduledWeatherEvent scheduled = new(
                pending.Candidate,
                starts[i],
                pending.DurationPips);

            placed.Add(scheduled);
            if (TryPlaceRecursive(
                    phasePipCount,
                    searchOrder,
                    eventIndex + 1,
                    placed,
                    random))
            {
                return true;
            }

            placed.RemoveAt(placed.Count - 1);
        }

        return false;
    }

    private static bool CanPlace(
        WeatherScheduleCandidate candidate,
        int startPip,
        int durationPips,
        List<ScheduledWeatherEvent> placed)
    {
        int endPipExclusive = startPip + durationPips;

        for (int i = 0; i < placed.Count; i++)
        {
            ScheduledWeatherEvent other = placed[i];
            int requiredGap = RequiredNominalGapPips(candidate, other?.Candidate);

            if (endPipExclusive <= other.StartPip)
            {
                if (other.StartPip - endPipExclusive < requiredGap)
                {
                    return false;
                }

                continue;
            }

            if (other.EndPipExclusive <= startPip)
            {
                if (startPip - other.EndPipExclusive < requiredGap)
                {
                    return false;
                }

                continue;
            }

            // Intervals overlap.
            return false;
        }

        return true;
    }

    private static int RequiredNominalGapPips(
        WeatherScheduleCandidate a,
        WeatherScheduleCandidate b)
    {
        // A DeathRain transition occupies half a pip outside the authored block. With
        // integer placement, a nominal 3-pip gap is the smallest value that leaves at
        // least the required 2 complete pips after subtracting that half-pip tail or
        // pre-roll. Other event pairs keep the ordinary >=2-pip rule.
        return HasDeathRainTransition(a) || HasDeathRainTransition(b)
            ? MinimumGapPips + 1
            : MinimumGapPips;
    }

    private static void Shuffle<T>(List<T> list, Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            T temp = list[i];
            list[i] = list[j];
            list[j] = temp;
        }
    }
}
