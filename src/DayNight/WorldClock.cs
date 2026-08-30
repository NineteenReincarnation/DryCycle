using System;
using UnityEngine;

namespace DryCycle.DayNight;

internal enum WorldClockPhase
{
    Dawn,
    Day,
    Dusk,
    Night,
    PreDawn
}

internal readonly struct SolarLightingState
{
    public readonly float SunElevation;
    public readonly float DirectLight;
    public readonly float AmbientLight;
    public readonly float SunWarmth;
    public readonly float AmbientCoolness;
    public readonly float NightFactor;
    public readonly float DawnFactor;
    public readonly float DuskFactor;
    public readonly float BlueHourFactor;
    public readonly float Saturation;

    public SolarLightingState(
        float sunElevation,
        float directLight,
        float ambientLight,
        float sunWarmth,
        float ambientCoolness,
        float nightFactor,
        float dawnFactor,
        float duskFactor,
        float blueHourFactor,
        float saturation)
    {
        SunElevation = sunElevation;
        DirectLight = directLight;
        AmbientLight = ambientLight;
        SunWarmth = sunWarmth;
        AmbientCoolness = ambientCoolness;
        NightFactor = nightFactor;
        DawnFactor = dawnFactor;
        DuskFactor = duskFactor;
        BlueHourFactor = blueHourFactor;
        Saturation = saturation;
    }

    public static SolarLightingState FromDayProgress(float dayProgress)
    {
        dayProgress = Mathf.Repeat(dayProgress, 1f);

        // Dawn = 0.0, noon = 0.25, dusk = 0.5, midnight = 0.75.
        float sunElevation = Mathf.Sin(dayProgress * Mathf.PI * 2f);
        float daylight = SmoothStep(-0.16f, 0.20f, sunElevation);
        float directLight = Mathf.Pow(Mathf.Clamp01(sunElevation), 0.58f);
        float nightFactor = 1f - SmoothStep(-0.45f, 0.06f, sunElevation);

        float dawnFactor = WrappedBell(dayProgress, 0f, 0.075f);
        float duskFactor = WrappedBell(dayProgress, 0.5f, 0.085f);
        float horizonFactor = Mathf.Max(dawnFactor, duskFactor);
        float blueHourFactor = horizonFactor * (1f - directLight) * 0.95f;

        // The sun warms strongly only while it is near the horizon. Dusk is allowed
        // to be a little warmer than dawn, but both remain relative corrections;
        // the room palette still owns the region's actual hue identity.
        float sunWarmth = Mathf.Clamp01(dawnFactor * 0.55f + duskFactor * 0.90f) * daylight;
        float ambientCoolness = Mathf.Clamp01(nightFactor * 0.72f + blueHourFactor * 0.48f);

        // Preserve readable silhouettes at night. Ambient light never reaches zero;
        // the palette lighting layer will still deepen the room through darkness.
        float ambientLight = Mathf.Lerp(0.43f, 1f, daylight);
        ambientLight += horizonFactor * 0.05f;
        ambientLight = Mathf.Clamp01(ambientLight);

        float saturation = Mathf.Lerp(0.80f, 1f, daylight);
        saturation = Mathf.Lerp(saturation, 0.94f, horizonFactor * 0.35f);

        return new SolarLightingState(
            sunElevation,
            directLight,
            ambientLight,
            sunWarmth,
            ambientCoolness,
            nightFactor,
            dawnFactor,
            duskFactor,
            blueHourFactor,
            saturation);
    }

    private static float SmoothStep(float from, float to, float value)
    {
        float t = Mathf.InverseLerp(from, to, value);
        return t * t * (3f - 2f * t);
    }

    private static float WrappedBell(float value, float center, float width)
    {
        float delta = Mathf.Abs(value - center);
        delta = Mathf.Min(delta, 1f - delta);
        float t = Mathf.Clamp01(delta / Mathf.Max(width, 0.0001f));
        t = 1f - t;
        return t * t * (3f - 2f * t);
    }
}

internal sealed class WorldClock
{
    private const float NightLengthRatio = 0.5f;

    private long _ticksInHalf;
    private int _dayCycleLength;
    private int _nightCycleLength;
    private bool _nightHalf;
    private long _absoluteTicks;
    private int _dayIndex;

    public WorldClock(int dayCycleLength)
    {
        SetCycleLengthInternal(dayCycleLength);
    }

    public int DayCycleLength => _dayCycleLength;

    public int NightCycleLength => _nightCycleLength;

    public int CurrentHalfLength => _nightHalf ? _nightCycleLength : _dayCycleLength;

    public bool IsNight => _nightHalf;

    public int DayIndex => _dayIndex;

    public long AbsoluteTicks => _absoluteTicks;

    public float HalfProgress => Mathf.Clamp01((float)_ticksInHalf / CurrentHalfLength);

    public float HalfRemaining => 1f - HalfProgress;

    // Day and night each still occupy half of normalized solar time. They do not
    // occupy equal real time: daytime uses 100% of vanilla cycleLength while night
    // uses 50%. This preserves all existing solar/palette curves while making the
    // night progress through its visual half twice as fast.
    public float DayProgress => (_nightHalf ? 0.5f : 0f) + HalfProgress * 0.5f;

    public SolarLightingState Lighting => SolarLightingState.FromDayProgress(DayProgress);

    public WorldClockPhase Phase
    {
        get
        {
            float p = DayProgress;
            if (p < 0.065f)
            {
                return WorldClockPhase.Dawn;
            }

            if (p < 0.42f)
            {
                return WorldClockPhase.Day;
            }

            if (p < 0.5f)
            {
                return WorldClockPhase.Dusk;
            }

            if (p < 0.92f)
            {
                return WorldClockPhase.Night;
            }

            return WorldClockPhase.PreDawn;
        }
    }

    // Vanilla night creatures begin leaving dens at ~600 and several vanilla night
    // lights use thresholds around 6000. Mapping the entire (shorter) night to
    // 0..10000 preserves the original thresholds without letting vanilla own time.
    public int LegacyDayNightCounter => _nightHalf
        ? Mathf.RoundToInt(HalfProgress * 10000f)
        : 0;

    public int VirtualRainTimer(int cycleLength)
    {
        cycleLength = Math.Max(1, cycleLength);
        return Mathf.Clamp(
            Mathf.RoundToInt(HalfProgress * cycleLength),
            0,
            cycleLength);
    }

    public void SetCycleLength(int dayCycleLength)
    {
        dayCycleLength = Math.Max(1, dayCycleLength);
        int nightCycleLength = CalculateNightLength(dayCycleLength);
        if (dayCycleLength == _dayCycleLength && nightCycleLength == _nightCycleLength)
        {
            return;
        }

        float progress = HalfProgress;
        SetCycleLengthInternal(dayCycleLength);
        _ticksInHalf = (long)Math.Round(progress * CurrentHalfLength);
        if (_ticksInHalf >= CurrentHalfLength)
        {
            _ticksInHalf = CurrentHalfLength - 1;
        }
    }

    public void Advance(long ticks)
    {
        if (ticks <= 0)
        {
            return;
        }

        _absoluteTicks += ticks;
        _ticksInHalf += ticks;

        while (_ticksInHalf >= CurrentHalfLength)
        {
            _ticksInHalf -= CurrentHalfLength;
            _nightHalf = !_nightHalf;
            if (!_nightHalf)
            {
                _dayIndex++;
            }
        }
    }

    private void SetCycleLengthInternal(int dayCycleLength)
    {
        _dayCycleLength = Math.Max(1, dayCycleLength);
        _nightCycleLength = CalculateNightLength(_dayCycleLength);
    }

    private static int CalculateNightLength(int dayCycleLength)
    {
        return Math.Max(1, Mathf.RoundToInt(dayCycleLength * NightLengthRatio));
    }
}
