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
/// - every scheduled event owns an additional 15-second fade-in before its authored
///   cells and an additional 15-second fade-out after them. These 30 seconds are not
///   counted in DurationPips; every authored pip is full schedule intensity;
/// - DangerType has no influence during the first 4 pips of a phase, including its
///   external fade-in;
/// - no effects overlap;
/// - neighboring effects retain at least 2 completely empty pips after both events'
///   external half-pip transitions are accounted for. On the integer-pip placement
///   grid this means authored blocks need a nominal gap of at least 3 pips;
/// - if the selected events cannot fit, randomly cancel one and retry until the
///   remaining set has a legal layout.
/// </summary>
internal static class WeatherPhaseScheduler
{
    internal const int PipTicks = 1200;
    internal const int MinimumGapPips = 2;
    internal const int DangerProtectedOpeningPips = 4;

    // All weather and danger events now use the same external transition window.
    internal const int EventTransitionTicks = PipTicks / 2;

    // Kept as an alias so older internal call sites or development branches referring
    // to the former DeathRain-only transition continue to compile.
    internal const int DeathRainTransitionTicks = EventTransitionTicks;

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

            // Never weaken duration, transition, protection, or spacing rules merely
            // to force a layout. If no complete layout exists, remove one random event
            // and exhaustively try the smaller set again.
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

    internal static bool HasExternalTransition(WeatherScheduleCandidate candidate)
    {
        return candidate != null;
    }

    // Compatibility helper retained for code that still needs to recognize the
    // DeathRain ID specifically. Transition behavior itself is no longer special.
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

        // Search order is randomized, but recursion is exhaustive for that order. An
        // event is cancelled only when no legal arrangement exists on this pip grid.
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

        // Every event needs a complete 15-second lead-in before its first authored
        // full-intensity pip. Starts remain integer pip boundaries, so one full leading
        // cell is reserved; the actual transition occupies only its latter half.
        int earliestStart = 1;

        if (pending.Candidate.Kind == WeatherScheduleEventKind.DangerType)
        {
            // The first four pips must contain zero DangerType influence. A main start
            // at pip 4 would begin its lead-in at 3.5, so the first legal integer main
            // start is pip 5.
            earliestStart = Math.Max(
                earliestStart,
                DangerProtectedOpeningPips + 1);
        }

        // Every event also needs its complete 15-second tail before the phase ends.
        // One integer cell is conservatively reserved; only its first half is used.
        const int trailingReservePips = 1;
        int latestStart = phasePipCount - pending.DurationPips - trailingReservePips;

        if (latestStart < earliestStart)
        {
            return false;
        }

        List<int> starts = new(latestStart - earliestStart + 1);
        for (int start = earliestStart; start <= latestStart; start++)
        {
            if (CanPlace(start, pending.DurationPips, placed))
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
        int startPip,
        int durationPips,
        List<ScheduledWeatherEvent> placed)
    {
        int endPipExclusive = startPip + durationPips;

        for (int i = 0; i < placed.Count; i++)
        {
            ScheduledWeatherEvent other = placed[i];
            if (other == null)
            {
                continue;
            }

            // Each event has a half-pip tail and the next event a half-pip lead-in.
            // A nominal 3-pip authored gap therefore leaves exactly 2 completely empty
            // pips between the actual weather effects.
            int requiredGap = MinimumGapPips + 1;

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

            return false;
        }

        return true;
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
