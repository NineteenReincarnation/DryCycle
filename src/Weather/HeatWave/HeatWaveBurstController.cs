using System;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Internal HeatWave instability model. Thermal Burst is not a second weather ID and
/// never emits a visible explosion ring. The controller first suppresses transport so
/// the boundary layer appears unnaturally still, then releases the stored energy into
/// buoyancy/turbulence and finally decays through a long optical/fluid recovery tail.
/// </summary>
internal sealed class HeatWaveBurstController
{
    private enum Phase
    {
        Accumulating,
        Charging,
        Release,
        Recovery
    }

    private readonly System.Random _random;
    private Phase _phase;
    private float _instability;
    private float _phaseClock;
    private float _phaseDuration;
    private float _triggerThreshold;

    internal float Instability => Mathf.Clamp01(_instability);
    internal float Stillness { get; private set; }
    internal float BurstStrength { get; private set; }
    internal float BuoyancyScale { get; private set; } = 1f;
    internal float TurbulenceScale { get; private set; } = 1f;
    internal float HeatStorageScale { get; private set; } = 1f;

    internal HeatWaveBurstController(Room room)
    {
        unchecked
        {
            int seed = 17;
            string name = room?.abstractRoom?.name ?? string.Empty;
            for (int i = 0; i < name.Length; i++)
            {
                seed = seed * 31 + name[i];
            }
            _random = new System.Random(seed);
        }

        ResetThreshold();
    }

    internal void Update(float deltaTime, float weatherIntensity, float solarIntensity)
    {
        float dt = Mathf.Clamp(deltaTime, 0f, 0.1f);
        float heat = Mathf.Clamp01(weatherIntensity);
        float solar = Mathf.Clamp01(solarIntensity);

        Stillness = 0f;
        BurstStrength = 0f;
        BuoyancyScale = 1f;
        TurbulenceScale = 1f;
        HeatStorageScale = 1f;

        if (heat <= 0.025f)
        {
            _instability = Mathf.Max(0f, _instability - dt * 0.18f);
            _phase = Phase.Accumulating;
            _phaseClock = 0f;
            return;
        }

        _phaseClock += dt;
        switch (_phase)
        {
            case Phase.Accumulating:
            {
                float growth = Mathf.Lerp(0.0045f, 0.022f, heat) *
                               Mathf.Lerp(0.72f, 1.18f, solar);
                _instability = Mathf.Clamp01(_instability + growth * dt);

                if (_instability >= _triggerThreshold && heat >= 0.55f)
                {
                    _phase = Phase.Charging;
                    _phaseClock = 0f;
                    _phaseDuration = Next(2.0f, 4.3f);
                }
                break;
            }

            case Phase.Charging:
            {
                float t = Smooth01(_phaseClock / Mathf.Max(0.01f, _phaseDuration));
                Stillness = t;
                BuoyancyScale = Mathf.Lerp(1f, 0.16f, t);
                TurbulenceScale = Mathf.Lerp(1f, 0.20f, t);
                HeatStorageScale = Mathf.Lerp(1f, 2.35f, t);
                _instability = Mathf.Clamp01(_instability + dt * 0.035f * heat);

                if (_phaseClock >= _phaseDuration)
                {
                    _phase = Phase.Release;
                    _phaseClock = 0f;
                    _phaseDuration = Next(1.7f, 3.0f);
                }
                break;
            }

            case Phase.Release:
            {
                float t = Mathf.Clamp01(_phaseClock / Mathf.Max(0.01f, _phaseDuration));
                float envelope = Mathf.Sin(t * Mathf.PI);
                BurstStrength = Mathf.Pow(Mathf.Clamp01(envelope), 0.72f);
                BuoyancyScale = Mathf.Lerp(1.45f, 4.10f, BurstStrength);
                TurbulenceScale = Mathf.Lerp(1.35f, 4.75f, BurstStrength);
                HeatStorageScale = Mathf.Lerp(1.25f, 0.48f, t);
                _instability = Mathf.Max(0f, _instability - dt * (0.16f + BurstStrength * 0.42f));

                if (_phaseClock >= _phaseDuration)
                {
                    _phase = Phase.Recovery;
                    _phaseClock = 0f;
                    _phaseDuration = Next(6.5f, 11.5f);
                }
                break;
            }

            case Phase.Recovery:
            {
                float t = Smooth01(_phaseClock / Mathf.Max(0.01f, _phaseDuration));
                float tail = 1f - t;
                BurstStrength = tail * tail * 0.30f;
                BuoyancyScale = Mathf.Lerp(1f, 1.42f, tail);
                TurbulenceScale = Mathf.Lerp(1f, 1.85f, tail);
                _instability = Mathf.Max(0f, _instability - dt * 0.045f);

                if (_phaseClock >= _phaseDuration)
                {
                    _phase = Phase.Accumulating;
                    _phaseClock = 0f;
                    _instability = Mathf.Min(_instability, 0.18f);
                    ResetThreshold();
                }
                break;
            }
        }
    }

    private void ResetThreshold()
    {
        _triggerThreshold = Next(0.68f, 0.96f);
    }

    private float Next(float min, float max)
    {
        return Mathf.Lerp(min, max, (float)_random.NextDouble());
    }

    private static float Smooth01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }
}
