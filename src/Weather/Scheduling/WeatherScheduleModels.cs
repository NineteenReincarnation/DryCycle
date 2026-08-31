using System;
using System.Collections.Generic;

namespace DryCycle.Weather.Scheduling;

/// <summary>
/// Day and night are scheduled independently. A phase never means a complete
/// day+night pair.
/// </summary>
internal enum WeatherSchedulePhase
{
    Day,
    Night
}

internal enum WeatherScheduleEventKind
{
    Weather,
    DangerType
}

/// <summary>
/// A candidate has already passed the climate/registration probability checks.
/// The scheduler only decides whether it survives the per-phase count/space limits
/// and where it is placed on the RainMeter-pip timeline.
/// </summary>
internal sealed class WeatherScheduleCandidate
{
    internal string Id { get; }
    internal WeatherScheduleEventKind Kind { get; }

    internal WeatherScheduleCandidate(string id, WeatherScheduleEventKind kind)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Weather schedule candidate ID cannot be empty.", nameof(id));
        }

        Id = id.Trim();
        Kind = kind;
    }

    public override string ToString() => $"{Kind}:{Id}";
}

internal sealed class ScheduledWeatherEvent
{
    internal WeatherScheduleCandidate Candidate { get; }
    internal int StartPip { get; }
    internal int DurationPips { get; }
    internal int EndPipExclusive => StartPip + DurationPips;

    internal ScheduledWeatherEvent(
        WeatherScheduleCandidate candidate,
        int startPip,
        int durationPips)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        StartPip = startPip;
        DurationPips = durationPips;
    }

    public override string ToString()
    {
        return $"{Candidate} [{StartPip}, {EndPipExclusive}) ({DurationPips} pips)";
    }
}

internal sealed class WeatherPhaseSchedule
{
    private readonly List<ScheduledWeatherEvent> _events;
    private readonly List<WeatherScheduleCandidate> _cancelled;

    internal WeatherSchedulePhase Phase { get; }
    internal int PhasePipCount { get; }
    internal IReadOnlyList<ScheduledWeatherEvent> Events => _events;
    internal IReadOnlyList<WeatherScheduleCandidate> CancelledCandidates => _cancelled;

    internal WeatherPhaseSchedule(
        WeatherSchedulePhase phase,
        int phasePipCount,
        List<ScheduledWeatherEvent> events,
        List<WeatherScheduleCandidate> cancelled)
    {
        Phase = phase;
        PhasePipCount = Math.Max(0, phasePipCount);
        _events = events ?? new List<ScheduledWeatherEvent>();
        _cancelled = cancelled ?? new List<WeatherScheduleCandidate>();
    }
}
