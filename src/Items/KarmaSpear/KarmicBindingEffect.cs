using DryCycle.Framework.KarmicManipulation;
using UnityEngine;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// Holds a creature near the point where the karmic charge caught it without pinning
/// every BodyChunk to an absolute world-space pose. The creature may still writhe,
/// rotate and animate, while whole-body translation is constrained in a bounded way.
/// </summary>
internal sealed class KarmicBindingEffect : UpdatableAndDeletable
{
    private readonly KarmaSpear _source;
    private readonly Creature _target;
    private readonly Vector2 _anchor;
    private readonly int _duration;
    private readonly bool _sourceMustRemainLodged;
    private readonly bool _largeTarget;
    private readonly int _chargeGeneration;
    private readonly int _initialKarmaLevel;
    private int _displayedKarmaLevel;
    private int _age;

    internal KarmicBindingEffect(
        KarmaSpear source,
        Creature target,
        Vector2 anchor,
        bool largeTarget,
        bool sourceMustRemainLodged)
    {
        _source = source;
        _target = target;
        _sourceMustRemainLodged = sourceMustRemainLodged;
        _chargeGeneration = source.ChargeGeneration;
        _initialKarmaLevel = Mathf.Clamp(source.KarmaLevel, 1, 10);
        _displayedKarmaLevel = _initialKarmaLevel;

        // TotalMass is not a reliable proxy for lizard threat class. Green Lizards are
        // extremely heavy (7.5 total body mass) while Red Lizards are lighter (~3.1), so
        // the previous >= 3.4 rule accidentally treated Green as "large" and Red as
        // "medium". Ordinary lizards use the medium binding; Red Lizard keeps the large
        // crisis-interruption behavior.
        _largeTarget = target is Lizard lizard
            ? lizard.Template.type == CreatureTemplate.Type.RedLizard
            : largeTarget;

        // Anchor the whole body's mass centre at impact. The old implementation anchored
        // each chunk independently, which fought creature locomotion and body connections.
        // Keep the caller-supplied anchor only as a safe fallback for unusual zero-chunk
        // objects.
        _anchor = TryGetBodyState(target, out Vector2 bodyCenter, out _)
            ? bodyCenter
            : anchor;

        _duration = (_largeTarget ? 128 : 88) +
                    _initialKarmaLevel * (_largeTarget ? 7 : 5);

        target.Stun(_largeTarget ? 24 : 34);

        // Large fliers/runners lose the action they were performing at impact, but this
        // is only a one-time interruption. The persistent controller below never injects
        // an unbounded impulse.
        if (_largeTarget)
        {
            for (int i = 0; i < target.bodyChunks.Length; i++)
            {
                target.bodyChunks[i].vel.y -= 1.2f;
            }
        }
    }

    public override void Update(bool eu)
    {
        base.Update(eu);
        _age++;

        if (_target == null ||
            _target.slatedForDeletetion ||
            _target.room != room ||
            _target.dead ||
            _source == null ||
            _source.slatedForDeletetion ||
            _source.room != room)
        {
            Finish();
            return;
        }

        if (_sourceMustRemainLodged && _source.mode != Weapon.Mode.StuckInCreature)
        {
            Finish();
            return;
        }

        UpdateRemainingKarma();
        ApplyStableConstraint();

        if (_age % 24 == 0)
        {
            KarmicVisualEffects.SpawnFieldPulse(
                _source,
                42f + _displayedKarmaLevel * 2f);
        }

        if (_age >= _duration)
        {
            Finish();
        }
    }

    private void ApplyStableConstraint()
    {
        if (!TryGetBodyState(_target, out Vector2 bodyCenter, out Vector2 bodyVelocity))
        {
            return;
        }

        // 1 = freshly bound / strongest level, 0 = final remaining level. A spear that
        // started at Karma 1 remains at full restraint for its shorter lifetime.
        float strength = _initialKarmaLevel <= 1
            ? 1f
            : Mathf.InverseLerp(1f, _initialKarmaLevel, _displayedKarmaLevel);

        // Give the body a small cage in which it can visibly struggle. The cage opens as
        // karma drains instead of weakening a spring toward an unstable low-damping state.
        float leashRadius = _largeTarget
            ? Mathf.Lerp(36f, 16f, strength)
            : Mathf.Lerp(24f, 9f, strength);

        // Remove only whole-body translation. The same velocity delta is applied to every
        // chunk, so head/tail/limb-relative motion is preserved and body connections are not
        // forced to fight a separate spring for every chunk.
        float translationDamping = _largeTarget
            ? Mathf.Lerp(0.055f, 0.17f, strength)
            : Mathf.Lerp(0.08f, 0.24f, strength);
        float maxTranslationRemoval = _largeTarget ? 0.42f : 0.62f;

        Vector2 translationRemoval = Vector2.ClampMagnitude(
            bodyVelocity * translationDamping,
            maxTranslationRemoval);
        ApplyUniformVelocityDelta(-translationRemoval);

        Vector2 estimatedVelocity = bodyVelocity - translationRemoval;
        Vector2 fromAnchor = bodyCenter - _anchor;
        float distance = fromAnchor.magnitude;

        if (distance > leashRadius && distance > 0.001f)
        {
            Vector2 outward = fromAnchor / distance;
            float overshoot = distance - leashRadius;

            // Outside the cage, first remove excessive radial escape velocity. This is an
            // energy-removing brake only; it cannot reverse the projectile-like direction
            // into an ever-growing spring oscillation.
            float outwardSpeedLimit = _largeTarget
                ? Mathf.Lerp(5.8f, 2.8f, strength)
                : Mathf.Lerp(4.4f, 1.8f, strength);
            float inwardSpeedLimit = _largeTarget
                ? Mathf.Lerp(7.0f, 4.6f, strength)
                : Mathf.Lerp(5.8f, 3.6f, strength);

            float radialSpeed = Vector2.Dot(estimatedVelocity, outward);
            float clampedRadialSpeed = Mathf.Clamp(
                radialSpeed,
                -inwardSpeedLimit,
                outwardSpeedLimit);
            float radialCorrectionAmount = radialSpeed - clampedRadialSpeed;
            Vector2 radialBrake = outward * Mathf.Clamp(
                radialCorrectionAmount,
                -(_largeTarget ? 0.72f : 0.95f),
                _largeTarget ? 0.72f : 0.95f);

            ApplyUniformVelocityDelta(-radialBrake);
            estimatedVelocity -= radialBrake;

            // Then add a small bounded inward pull based only on overshoot. Unlike the old
            // per-chunk spring, this never grows beyond the explicit acceleration cap.
            float tetherStiffness = _largeTarget
                ? Mathf.Lerp(0.010f, 0.023f, strength)
                : Mathf.Lerp(0.015f, 0.034f, strength);
            float maxInwardAcceleration = _largeTarget
                ? Mathf.Lerp(0.10f, 0.27f, strength)
                : Mathf.Lerp(0.14f, 0.40f, strength);
            float inwardAcceleration = Mathf.Min(
                maxInwardAcceleration,
                overshoot * tetherStiffness);

            Vector2 tetherDelta = -outward * inwardAcceleration;
            ApplyUniformVelocityDelta(tetherDelta);
            estimatedVelocity += tetherDelta;
        }

        // Final safety net for creatures whose own locomotion can add very large impulses
        // in one frame (notably lizard lunges). This clamps only centre-of-mass translation;
        // it does not cap individual chunk motion, so visual struggling remains intact.
        float maxCenterSpeed = _largeTarget
            ? Mathf.Lerp(8.0f, 4.8f, strength)
            : Mathf.Lerp(6.2f, 3.6f, strength);

        if (estimatedVelocity.magnitude > maxCenterSpeed)
        {
            Vector2 excess = estimatedVelocity -
                             estimatedVelocity.normalized * maxCenterSpeed;
            Vector2 safetyRemoval = Vector2.ClampMagnitude(
                excess,
                _largeTarget ? 0.80f : 1.05f);
            ApplyUniformVelocityDelta(-safetyRemoval);
        }
    }

    private void ApplyUniformVelocityDelta(Vector2 delta)
    {
        if (delta.sqrMagnitude <= 0.000001f || _target?.bodyChunks == null)
        {
            return;
        }

        for (int i = 0; i < _target.bodyChunks.Length; i++)
        {
            _target.bodyChunks[i].vel += delta;
        }
    }

    private static bool TryGetBodyState(
        Creature creature,
        out Vector2 center,
        out Vector2 velocity)
    {
        center = Vector2.zero;
        velocity = Vector2.zero;

        if (creature?.bodyChunks == null || creature.bodyChunks.Length == 0)
        {
            return false;
        }

        float totalMass = 0f;
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            float mass = Mathf.Max(0.001f, chunk.mass);
            totalMass += mass;
            center += chunk.pos * mass;
            velocity += chunk.vel * mass;
        }

        if (totalMass <= 0.001f)
        {
            return false;
        }

        center /= totalMass;
        velocity /= totalMass;
        return true;
    }

    private void UpdateRemainingKarma()
    {
        int remainingFrames = Mathf.Max(0, _duration - _age);
        int remainingLevel = Mathf.Clamp(
            Mathf.CeilToInt(remainingFrames / (float)_duration * _initialKarmaLevel),
            1,
            _initialKarmaLevel);

        if (remainingLevel == _displayedKarmaLevel)
        {
            return;
        }

        _displayedKarmaLevel = remainingLevel;
        _source.SetBindingKarmaLevel(_chargeGeneration, remainingLevel);

        KarmicVisualEffects.SpawnImpactPulse(
            _source,
            _source.firstChunk.pos,
            remainingLevel,
            30f + remainingLevel * 2f);
    }

    private void Finish()
    {
        if (!slatedForDeletetion)
        {
            // Only the charge that created this binding may be consumed. If the spear
            // has already been recharged, this old effect must not drain the new charge.
            _source?.MarkSpent(_chargeGeneration);
            Destroy();
        }
    }
}
