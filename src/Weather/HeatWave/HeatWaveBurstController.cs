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
    private bool _debugTriggerRequested;

    internal float Instability => Mathf.Clamp01(_instability);
    internal float Stillness { get; private set; }
    internal float BurstStrength { get; private set; }
    internal float BurstKick { get; private set; }
    internal float BuoyancyScale { get; private set; } = 1f;
    internal float TurbulenceScale { get; private set; } = 1f;
    internal float HeatStorageScale { get; private set; } = 1f;
    internal string PhaseName => _phase.ToString();

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
        BurstKick = 0f;
        BuoyancyScale = 1f;
        TurbulenceScale = 1f;
        HeatStorageScale = 1f;

        if (_debugTriggerRequested && heat > 0.025f)
        {
            _debugTriggerRequested = false;
            _instability = 1f;
            if (_phase == Phase.Accumulating || _phase == Phase.Recovery)
            {
                EnterCharging(shortDebugCharge: true);
            }
        }

        if (heat <= 0.025f)
        {
            _instability = Mathf.Max(0f, _instability - dt * 0.18f);
            _phase = Phase.Accumulating;
            _phaseClock = 0f;
            _debugTriggerRequested = false;
            return;
        }

        _phaseClock += dt;
        switch (_phase)
        {
            case Phase.Accumulating:
            {
                // Strong weather builds instability on a tens-of-seconds timescale.
                // Solar energy accelerates it, but a HeatWave can still burst in a
                // hot shaded room because the weather itself drives the boundary layer.
                float growth = Mathf.Lerp(0.0045f, 0.022f, heat) *
                               Mathf.Lerp(0.80f, 1.18f, solar);
                _instability = Mathf.Clamp01(_instability + growth * dt);

                if (_instability >= _triggerThreshold && heat >= 0.55f)
                {
                    EnterCharging(shortDebugCharge: false);
                }
                break;
            }

            case Phase.Charging:
            {
                float t = Smooth01(_phaseClock / Mathf.Max(0.01f, _phaseDuration));
                Stillness = t;
                BuoyancyScale = Mathf.Lerp(1f, 0.13f, t);
                TurbulenceScale = Mathf.Lerp(1f, 0.17f, t);
                HeatStorageScale = Mathf.Lerp(1f, 2.55f, t);
                _instability = Mathf.Clamp01(_instability + dt * 0.035f * heat);

                if (_phaseClock >= _phaseDuration)
                {
                    _phase = Phase.Release;
                    _phaseClock = 0f;
                    _phaseDuration = Next(1.8f, 3.1f);
                }
                break;
            }

            case Phase.Release:
            {
                float t = Mathf.Clamp01(_phaseClock / Mathf.Max(0.01f, _phaseDuration));
                float envelope = Mathf.Sin(t * Mathf.PI);
                BurstStrength = Mathf.Pow(Mathf.Clamp01(envelope), 0.70f);

                // The first half-second is a separate physical kick: stored boundary
                // heat detaches abruptly before the longer turbulence envelope peaks.
                // This creates the visual "layer tears free" moment without a ring.
                BurstKick = Mathf.Exp(-_phaseClock * 5.4f) *
                            Smooth01(Mathf.InverseLerp(0f, 0.08f, _phaseClock));

                BuoyancyScale = Mathf.Lerp(1.45f, 4.25f, BurstStrength);
                TurbulenceScale = Mathf.Lerp(1.35f, 4.85f, BurstStrength);
                HeatStorageScale = Mathf.Lerp(1.15f, 0.42f, t);
                _instability = Mathf.Max(
                    0f,
                    _instability - dt * (0.16f + BurstStrength * 0.44f + BurstKick * 0.18f));

                if (_phaseClock >= _phaseDuration)
                {
                    _phase = Phase.Recovery;
                    _phaseClock = 0f;
                    _phaseDuration = Next(7.0f, 12.5f);
                }
                break;
            }

            case Phase.Recovery:
            {
                float t = Smooth01(_phaseClock / Mathf.Max(0.01f, _phaseDuration));
                float tail = 1f - t;
                BurstStrength = tail * tail * 0.30f;
                BuoyancyScale = Mathf.Lerp(1f, 1.42f, tail);
                TurbulenceScale = Mathf.Lerp(1f, 1.90f, tail);
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

    internal void DebugTrigger()
    {
        _debugTriggerRequested = true;
    }

    private void EnterCharging(bool shortDebugCharge)
    {
        _phase = Phase.Charging;
        _phaseClock = 0f;
        _phaseDuration = shortDebugCharge
            ? 0.75f
            : Next(2.1f, 4.5f);
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
