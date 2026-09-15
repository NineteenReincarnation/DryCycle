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

/// <summary>Shared by the real weapon, charge planner and deterministic geometry/balance tests.</summary>
internal static class LanceCombatMath
{
    internal const float DefaultLength = 80f;
    internal const float GripFraction = 0.34f;
    internal const float StandardThrustMaxDamage = 0.42f;
    internal const float PlayerThrustMaxDamage = 1.35f;
    internal const float PreviousChargeMaxDamage = 1.35f;
    internal const float ChargeMaxDamage = PreviousChargeMaxDamage * 2.75f;
    internal const float LanceScavengerCloseThrustMaxDamage = ChargeMaxDamage * 0.20f;

    // Bone-nail geometry. The narrow rear section is a handle; almost the entire forward
    // section is a broad tapered damaging blade. Width decreases monotonically toward the tip.
    internal const float BladeStartForwardFraction = 0.16f;
    internal const float BladeShoulderHalfWidth = 6.4f;
    internal const float BladeTipHalfWidth = 0.65f;
    internal const int BladeSweepSamples = 8;

    internal static float ValidLength(float value) => float.IsNaN(value) || float.IsInfinity(value)
        ? DefaultLength : Math.Max(75f, Math.Min(90f, value));

    internal static float ForwardLength(float length) => length * (1f - GripFraction);
    internal static float BladeRootDistance(float length) => ForwardLength(length) * BladeStartForwardFraction;

    internal static float BladeHalfWidth(float bladeT)
    {
        float t = Mathf.Clamp01(bladeT);
        return Mathf.Lerp(BladeShoulderHalfWidth, BladeTipHalfWidth, Mathf.Pow(t, 0.82f));
    }

    internal static float BladeDamageMultiplier(float bladeT) =>
        Mathf.Lerp(0.82f, 1f, Mathf.Pow(Mathf.Clamp01(bladeT), 0.72f));

    internal static Vector2 BladePoint(Vector2 grip, Vector2 direction, float forwardLength, float bladeT)
    {
        Vector2 dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector2.right;
        float distance = Mathf.Lerp(forwardLength * BladeStartForwardFraction, forwardLength, Mathf.Clamp01(bladeT));
        return grip + dir * distance;
    }

    internal static float CounterSweepChance(AbstractCreature.Personality personality)
    {
        float commitment = Mathf.Clamp01(personality.energy * 0.40f +
            personality.aggression * 0.35f + personality.bravery * 0.25f);
        return Mathf.Lerp(0.20f, 0.55f, commitment);
    }

    internal static LanceImpact Impact(float speed, float alignment, float holderMass,
        float targetMass, bool charging, float runUp, bool thrusting,
        float thrustMaxDamage = StandardThrustMaxDamage)
    {
        float facing = Mathf.InverseLerp(0.55f, 0.98f, alignment);
        float momentum = Mathf.InverseLerp(3f, 12f, speed);
        float full = charging ? momentum * Mathf.InverseLerp(25f, 80f, runUp) : 0f;
        float ordinary = thrusting ? Mathf.Lerp(0.12f, Mathf.Max(0.12f, thrustMaxDamage), facing) : 0f;
        float damage = Mathf.Max(ordinary, charging ? Mathf.Lerp(0.12f, ChargeMaxDamage, full) * facing : 0f);
        if (speed < 2f || alignment < 0.55f) damage = 0f;
        float ratio = Mathf.Max(0.05f, targetMass) / Mathf.Max(0.1f, holderMass);
        return new LanceImpact(damage, damage * Mathf.Lerp(12f, 25f, full),
            damage > 0f ? Mathf.Min(11f, speed * holderMass * (0.2f + 0.5f * full)) : 0f,
            Mathf.Clamp(1f / (1f + ratio * 0.6f), 0.12f, 0.9f));
    }

    internal static LanceImpact CounterSweepImpact(float holderMass, float targetMass, float speed)
    {
        float resolvedSpeed = Mathf.Max(12f, Mathf.Abs(speed));
        return Impact(resolvedSpeed, 1f, holderMass, targetMass, true, 80f, true, ChargeMaxDamage);
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

    internal static bool SweepBlade(Vector2 oldGrip, Vector2 oldDirection, Vector2 grip, Vector2 direction,
        float forwardLength, Vector2 oldTarget, Vector2 target, float targetRadius, float padding,
        out float fraction, out float bladeT)
    {
        fraction = float.MaxValue;
        bladeT = 1f;
        bool found = false;
        Vector2 oldDir = oldDirection.sqrMagnitude > 0.0001f ? oldDirection.normalized : Vector2.right;
        Vector2 newDir = direction.sqrMagnitude > 0.0001f ? direction.normalized : oldDir;

        for (int i = 0; i < BladeSweepSamples; i++)
        {
            float t = BladeSweepSamples == 1 ? 1f : (float)i / (BladeSweepSamples - 1);
            Vector2 oldPoint = BladePoint(oldGrip, oldDir, forwardLength, t);
            Vector2 point = BladePoint(grip, newDir, forwardLength, t);
            float radius = Mathf.Max(0f, targetRadius) + BladeHalfWidth(t) + Mathf.Max(0f, padding);
            if (!SweepTip(oldPoint, point, oldTarget, target, radius, out float hit) || hit > fraction)
                continue;
            fraction = hit;
            bladeT = t;
            found = true;
        }

        if (!found) fraction = 0f;
        return found;
    }
}
