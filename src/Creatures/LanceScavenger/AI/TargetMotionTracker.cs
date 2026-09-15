using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

/// <summary>
/// Short-horizon motion estimate shared by charge aiming and the airborne counter-sweep.
/// Rain World body chunks jitter noticeably from animation and constraints, so tactical
/// prediction should not use one raw velocity sample as if it were a stable trajectory.
/// </summary>
internal sealed class TargetMotionTracker
{
    private const float VelocityBlend = 0.36f;
    private const float JitterBlend = 0.24f;
    private const float MaximumLeadDistance = 65f;

    private struct Sample
    {
        internal bool Initialized;
        internal Vector2 Velocity;
        internal float Jitter;
    }

    private readonly Dictionary<BodyChunk, Sample> _samples = new();
    private Creature _target;
    private Vector2 _previousMainVelocity;
    private bool _havePreviousMainVelocity;
    private float _dodgeSeverity;

    internal Creature Target => _target;
    internal float DodgeSeverity => _dodgeSeverity;

    internal void Reset()
    {
        _samples.Clear();
        _target = null;
        _previousMainVelocity = Vector2.zero;
        _havePreviousMainVelocity = false;
        _dodgeSeverity = 0f;
    }

    internal void Update(Creature target)
    {
        if (target != _target)
        {
            Reset();
            _target = target;
        }

        if (target?.bodyChunks == null || target.bodyChunks.Length == 0)
            return;

        foreach (BodyChunk chunk in target.bodyChunks)
        {
            Vector2 instant = chunk.pos - chunk.lastPos;
            _samples.TryGetValue(chunk, out Sample sample);
            if (!sample.Initialized)
            {
                sample.Initialized = true;
                sample.Velocity = instant;
                sample.Jitter = 0f;
            }
            else
            {
                float deviation = (instant - sample.Velocity).magnitude;
                sample.Velocity = Vector2.Lerp(sample.Velocity, instant, VelocityBlend);
                sample.Jitter = Mathf.Lerp(sample.Jitter, deviation, JitterBlend);
            }
            _samples[chunk] = sample;
        }

        Vector2 mainVelocity = SmoothedVelocity(target.mainBodyChunk);
        float reversal = 0f;
        float acceleration = 0f;
        if (_havePreviousMainVelocity)
        {
            float previousSpeed = _previousMainVelocity.magnitude;
            float currentSpeed = mainVelocity.magnitude;
            if (previousSpeed > 1.25f && currentSpeed > 1.25f)
                reversal = Mathf.InverseLerp(0.15f, -0.8f,
                    Vector2.Dot(_previousMainVelocity.normalized, mainVelocity.normalized));
            acceleration = Mathf.InverseLerp(2.5f, 8f, (mainVelocity - _previousMainVelocity).magnitude);
        }

        float jitter = 1f - Stability(target.mainBodyChunk);
        float rawSeverity = Mathf.Max(reversal, acceleration * 0.72f, jitter * 0.45f);
        _dodgeSeverity = Mathf.Lerp(_dodgeSeverity, rawSeverity, rawSeverity > _dodgeSeverity ? 0.6f : 0.18f);
        _previousMainVelocity = mainVelocity;
        _havePreviousMainVelocity = true;
    }

    internal Vector2 SmoothedVelocity(BodyChunk chunk)
    {
        if (chunk != null && _samples.TryGetValue(chunk, out Sample sample) && sample.Initialized)
            return sample.Velocity;
        return chunk?.vel ?? Vector2.zero;
    }

    internal float Stability(BodyChunk chunk)
    {
        if (chunk == null || !_samples.TryGetValue(chunk, out Sample sample) || !sample.Initialized)
            return 0.65f;
        return 1f - Mathf.InverseLerp(1.2f, 7f, sample.Jitter);
    }

    internal Vector2 Predict(BodyChunk chunk, int frames)
    {
        if (chunk == null) return Vector2.zero;
        Vector2 lead = SmoothedVelocity(chunk) * Mathf.Max(0, frames);
        return chunk.pos + Vector2.ClampMagnitude(lead, MaximumLeadDistance);
    }

    internal bool ReversedFrom(Vector2 referenceVelocity, BodyChunk chunk)
    {
        Vector2 current = SmoothedVelocity(chunk);
        if (referenceVelocity.magnitude < 1.5f || current.magnitude < 1.5f) return false;
        return Vector2.Dot(referenceVelocity.normalized, current.normalized) < -0.25f;
    }

    // Pure helpers kept internal so deterministic tests can lock the smoothing semantics.
    internal static Vector2 BlendVelocity(Vector2 previous, Vector2 instant) =>
        Vector2.Lerp(previous, instant, VelocityBlend);

    internal static float StabilityFromJitter(float jitter) =>
        1f - Mathf.InverseLerp(1.2f, 7f, jitter);
}
