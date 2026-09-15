using System;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal readonly struct LanceImpact
{
    internal LanceImpact(float damage, float stun, float impulse, float retainedSpeed)
    { Damage = damage; Stun = stun; Impulse = impulse; RetainedSpeed = retainedSpeed; }
    internal float Damage { get; }
    internal float Stun { get; }
    internal float Impulse { get; }
    internal float RetainedSpeed { get; }
}

/// <summary>Shared by the real weapon and deterministic geometry/balance tests.</summary>
internal static class LanceCombatMath
{
    internal const float DefaultLength = 80f;
    internal const float GripFraction = 0.34f;
    internal const float StandardThrustMaxDamage = 0.42f;
    internal const float PlayerThrustMaxDamage = 1.35f;
    internal const float PreviousChargeMaxDamage = 1.35f;
    internal const float ChargeMaxDamage = PreviousChargeMaxDamage * 2.75f;
    internal const float LanceScavengerCloseThrustMaxDamage = ChargeMaxDamage * 0.20f;

    internal static float ValidLength(float value) => float.IsNaN(value) || float.IsInfinity(value)
        ? DefaultLength : Math.Max(75f, Math.Min(90f, value));

    internal static LanceImpact Impact(float speed, float alignment, float holderMass,
        float targetMass, bool charging, float runUp, bool thrusting,
        float thrustMaxDamage = StandardThrustMaxDamage)
    {
        float facing = Mathf.InverseLerp(0.55f, 0.98f, alignment);
        float momentum = Mathf.InverseLerp(3f, 12f, speed);
        float full = charging ? momentum * Mathf.InverseLerp(25f, 80f, runUp) : 0f;
        float ordinary = thrusting ? Mathf.Lerp(0.12f, Mathf.Max(0.12f, thrustMaxDamage), facing) : 0f;
        // Full charge remains the highest-damage attack. Player thrust and the
        // lance-scavenger close poke use independent caps supplied by their callers.
        float damage = Mathf.Max(ordinary, charging ? Mathf.Lerp(0.12f, ChargeMaxDamage, full) * facing : 0f);
        if (speed < 2f || alignment < 0.55f) damage = 0f;
        float ratio = Mathf.Max(0.05f, targetMass) / Mathf.Max(0.1f, holderMass);
        return new LanceImpact(damage, damage * Mathf.Lerp(12f, 25f, full),
            damage > 0f ? Mathf.Min(11f, speed * holderMass * (0.2f + 0.5f * full)) : 0f,
            Mathf.Clamp(1f / (1f + ratio * 0.6f), 0.12f, 0.9f));
    }

    internal static Vector2 ClosestPoint(Vector2 a, Vector2 b, Vector2 point)
    {
        Vector2 delta = b - a;
        return a + delta * (delta.sqrMagnitude < 0.0001f ? 0f :
            Mathf.Clamp01(Vector2.Dot(point - a, delta) / delta.sqrMagnitude));
    }

    internal static bool SweepTip(Vector2 oldTip, Vector2 tip, Vector2 oldTarget,
        Vector2 target, float radius, out float fraction)
    {
        Vector2 start = oldTip - oldTarget;
        Vector2 delta = tip - target - start;
        float c = start.sqrMagnitude - radius * radius;
        fraction = 0f;
        if (c <= 0f) return true;
        float a = delta.sqrMagnitude;
        if (a < 0.00001f) return false;
        float b = Vector2.Dot(start, delta);
        float discriminant = b * b - a * c;
        if (discriminant < 0f) return false;
        fraction = (-b - Mathf.Sqrt(discriminant)) / a;
        return fraction >= 0f && fraction <= 1f;
    }
}
