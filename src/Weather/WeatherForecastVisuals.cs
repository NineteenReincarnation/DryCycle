using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Visual language used by the RainMeter forecast layer. These are presentation IDs,
/// not weather simulation IDs: the scheduler/runtime can map its concrete event ID
/// into one of these styles without making the HUD own weather logic.
/// </summary>
internal enum WeatherForecastVisualKind
{
    None,
    SandStorm,
    DeathSandStorm,
    LightRain,
    HeavyRain,
    DeathRain,
    Fog,
    DenseFog,
    HeatWave
}

internal enum WeatherForecastAnimation
{
    Static,
    Drip,
    FastDrip,
    VerticalShake
}

internal readonly struct WeatherForecastVisualStyle
{
    internal readonly Color FillColor;
    internal readonly Color DropColor;
    internal readonly WeatherForecastAnimation Animation;
    internal readonly int DripCount;
    internal readonly float DripCyclesPerSecond;
    internal readonly float DripTravelPixels;
    internal readonly float ShakeAmplitudePixels;

    internal WeatherForecastVisualStyle(
        Color fillColor,
        Color dropColor,
        WeatherForecastAnimation animation,
        int dripCount = 0,
        float dripCyclesPerSecond = 0f,
        float dripTravelPixels = 0f,
        float shakeAmplitudePixels = 0f)
    {
        FillColor = fillColor;
        DropColor = dropColor;
        Animation = animation;
        DripCount = Math.Max(0, dripCount);
        DripCyclesPerSecond = Math.Max(0f, dripCyclesPerSecond);
        DripTravelPixels = Math.Max(0f, dripTravelPixels);
        ShakeAmplitudePixels = Math.Max(0f, shakeAmplitudePixels);
    }
}

/// <summary>
/// Centralized colors and animation parameters for forecast pips. Keeping this data
/// out of WorldClockHooks prevents visual tuning from leaking into clock logic.
/// </summary>
internal static class WeatherForecastVisualCatalog
{
    internal static readonly Color SandStormColor = new(0.90f, 0.76f, 0.42f);
    internal static readonly Color DeathSandStormColor = new(0.66f, 0.44f, 0.16f);

    internal static readonly Color LightRainColor = new(0.30f, 0.62f, 0.92f);
    internal static readonly Color HeavyRainColor = new(0.08f, 0.25f, 0.57f);
    internal static readonly Color RainDropColor = new(0.62f, 0.86f, 1.00f);

    internal static readonly Color FogColor = new(168f / 255f, 186f / 255f, 189f / 255f);
    internal static readonly Color DenseFogColor = new(82f / 255f, 99f / 255f, 102f / 255f);

    // HeatWave is intentionally pale rather than orange. The weather's identity is
    // white-hot midday bleaching, not sunset coloration.
    internal static readonly Color HeatWaveColor = new(1.00f, 0.86f, 0.57f);

    internal static WeatherForecastVisualStyle Get(WeatherForecastVisualKind kind)
    {
        return kind switch
        {
            WeatherForecastVisualKind.SandStorm => new WeatherForecastVisualStyle(
                SandStormColor,
                SandStormColor,
                WeatherForecastAnimation.Static),

            WeatherForecastVisualKind.DeathSandStorm => new WeatherForecastVisualStyle(
                DeathSandStormColor,
                DeathSandStormColor,
                WeatherForecastAnimation.Static),

            WeatherForecastVisualKind.LightRain => new WeatherForecastVisualStyle(
                LightRainColor,
                RainDropColor,
                WeatherForecastAnimation.Static),

            WeatherForecastVisualKind.HeavyRain => new WeatherForecastVisualStyle(
                HeavyRainColor,
                RainDropColor,
                WeatherForecastAnimation.Drip,
                dripCount: 3,
                dripCyclesPerSecond: 0.96f,
                dripTravelPixels: 9.8f),

            WeatherForecastVisualKind.DeathRain => new WeatherForecastVisualStyle(
                HeavyRainColor,
                RainDropColor,
                WeatherForecastAnimation.VerticalShake,
                shakeAmplitudePixels: 1.35f),

            WeatherForecastVisualKind.Fog => new WeatherForecastVisualStyle(
                FogColor,
                FogColor,
                WeatherForecastAnimation.Static),

            WeatherForecastVisualKind.DenseFog => new WeatherForecastVisualStyle(
                DenseFogColor,
                DenseFogColor,
                WeatherForecastAnimation.Static),

            WeatherForecastVisualKind.HeatWave => new WeatherForecastVisualStyle(
                HeatWaveColor,
                HeatWaveColor,
                WeatherForecastAnimation.Static),

            _ => new WeatherForecastVisualStyle(
                Color.clear,
                Color.clear,
                WeatherForecastAnimation.Static)
        };
    }

    internal static bool TryResolve(
        string id,
        WeatherScheduleEventKind eventKind,
        out WeatherForecastVisualKind visualKind)
    {
        visualKind = WeatherForecastVisualKind.None;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        string normalized = id.Trim().Replace("_", string.Empty).Replace("-", string.Empty)
            .ToUpperInvariant();

        switch (normalized)
        {
            case "LIGHTRAIN":
                visualKind = WeatherForecastVisualKind.LightRain;
                return true;

            case "HEAVYRAIN":
                visualKind = WeatherForecastVisualKind.HeavyRain;
                return true;

            case "DEATHRAIN":
                visualKind = WeatherForecastVisualKind.DeathRain;
                return true;

            case "FOG" when eventKind == WeatherScheduleEventKind.Weather:
                visualKind = WeatherForecastVisualKind.Fog;
                return true;

            case "DENSEFOG" when eventKind == WeatherScheduleEventKind.Weather:
                visualKind = WeatherForecastVisualKind.DenseFog;
                return true;

            case "HEATWAVE" when eventKind == WeatherScheduleEventKind.Weather:
                visualKind = WeatherForecastVisualKind.HeatWave;
                return true;

            case "SANDSTORM":
                visualKind = eventKind == WeatherScheduleEventKind.DangerType
                    ? WeatherForecastVisualKind.DeathSandStorm
                    : WeatherForecastVisualKind.SandStorm;
                return true;

            case "DEATHSANDSTORM":
                visualKind = WeatherForecastVisualKind.DeathSandStorm;
                return true;

            case "RAIN" when eventKind == WeatherScheduleEventKind.DangerType:
                visualKind = WeatherForecastVisualKind.DeathRain;
                return true;
        }

        return false;
    }
}

/// <summary>
/// Stores the already-generated forecast for a game. This is intentionally only a
/// display cache: scheduling and probability remain owned by WeatherPhaseScheduler
/// and the future climate loader.
/// </summary>
internal static class WeatherForecastTimeline
{
    private sealed class State
    {
        internal readonly Dictionary<int, WeatherForecastVisualKind> Day = new();
        internal readonly Dictionary<int, WeatherForecastVisualKind> Night = new();
    }

    private static ConditionalWeakTable<RainWorldGame, State> _states = new();

    internal static void SetPhaseSchedule(RainWorldGame game, WeatherPhaseSchedule schedule)
    {
        if (game == null || schedule == null)
        {
            return;
        }

        State state = _states.GetOrCreateValue(game);
        Dictionary<int, WeatherForecastVisualKind> target =
            schedule.Phase == WeatherSchedulePhase.Day ? state.Day : state.Night;
        target.Clear();

        for (int i = 0; i < schedule.Events.Count; i++)
        {
            ScheduledWeatherEvent scheduled = schedule.Events[i];
            if (scheduled?.Candidate == null ||
                !WeatherForecastVisualCatalog.TryResolve(
                    scheduled.Candidate.Id,
                    scheduled.Candidate.Kind,
                    out WeatherForecastVisualKind kind))
            {
                continue;
            }

            for (int pip = scheduled.StartPip; pip < scheduled.EndPipExclusive; pip++)
            {
                target[pip + 1] = kind;
            }
        }
    }

    internal static bool TryGet(
        RainWorldGame game,
        WeatherSchedulePhase phase,
        int chronologicalPip,
        out WeatherForecastVisualKind kind)
    {
        kind = WeatherForecastVisualKind.None;
        if (game == null || chronologicalPip < 1 || !_states.TryGetValue(game, out State state))
        {
            return false;
        }

        Dictionary<int, WeatherForecastVisualKind> source =
            phase == WeatherSchedulePhase.Day ? state.Day : state.Night;
        return source.TryGetValue(chronologicalPip, out kind) &&
               kind != WeatherForecastVisualKind.None;
    }

    internal static void Clear(RainWorldGame game)
    {
        if (game != null)
        {
            _states.Remove(game);
        }
    }

    internal static void Reset()
    {
        _states = new ConditionalWeakTable<RainWorldGame, State>();
    }
}
