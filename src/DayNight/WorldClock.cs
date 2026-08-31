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

        float sunElevation = Mathf.Sin(dayProgress * Mathf.PI * 2f);
        float daylight = SmoothStep(-0.16f, 0.20f, sunElevation);
        float directLight = Mathf.Pow(Mathf.Clamp01(sunElevation), 0.58f);
        float nightFactor = 1f - SmoothStep(-0.45f, 0.06f, sunElevation);

        float dawnFactor = WrappedBell(dayProgress, 0f, 0.075f);
        float duskFactor = WrappedBell(dayProgress, 0.5f, 0.085f);
        float horizonFactor = Mathf.Max(dawnFactor, duskFactor);
        float blueHourFactor = horizonFactor * (1f - directLight) * 0.95f;

        float sunWarmth = Mathf.Clamp01(dawnFactor * 0.55f + duskFactor * 0.90f) * daylight;
        float ambientCoolness = Mathf.Clamp01(nightFactor * 0.72f + blueHourFactor * 0.48f);

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

    public float DayProgress => (_nightHalf ? 0.5f : 0f) + HalfProgress * 0.5f;

    public SolarLightingState Lighting => SolarLightingState.FromDayProgress(DayProgress);

    public WorldClockPhase Phase
    {
        get
        {
            float p = DayProgress;

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

    /// <summary>
    /// Initializes a newly-created DryCycle clock from elapsed vanilla RainCycle time.
    /// This is used when the player spent time in a region where DryCycle was disabled
    /// before entering/enabling a DryCycle region. The source vanilla cycle has no
    /// DryCycle night half, so the imported position is intentionally daytime.
    /// </summary>
    public void AlignToDayElapsedTicks(long elapsedTicks)
    {
        _nightHalf = false;
        _ticksInHalf = Math.Max(0L, Math.Min(elapsedTicks, _dayCycleLength - 1L));
        _absoluteTicks = Math.Max(_absoluteTicks, _ticksInHalf);
    }

    /// <summary>
    /// A successful shelter sleep always starts a brand-new DryCycle round at the
    /// first tick of daytime, regardless of whether the previous round ended during
    /// day, dusk, night or pre-dawn. AbsoluteTicks remains monotonic bookkeeping;
    /// phase-local time and the scheduling day index are the values that restart.
    /// </summary>
    public void ResetToDayStart(bool advanceDayIndex = true)
    {
        _ticksInHalf = 0;
        _nightHalf = false;
        if (advanceDayIndex)
        {
            _dayIndex++;
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
