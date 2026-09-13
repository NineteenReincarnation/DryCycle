using DryCycle.Framework.KarmicManipulation;
using UnityEngine;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// Stable whole-body bind for Light, Standard and Heavy targets. The effect constrains
/// centre-of-mass translation rather than pinning every BodyChunk to an absolute pose, so
/// native creature animation and body connections can continue without spring feedback.
/// </summary>
internal sealed class KarmicBindingEffect : UpdatableAndDeletable
{
    private readonly KarmaSpear _source;
    private readonly Creature _target;
    private readonly KarmicTargetProfile _profile;
    private readonly Vector2 _anchor;
    private readonly int _duration;
    private readonly bool _sourceMustRemainLodged;
    private readonly int _chargeGeneration;
    private readonly int _initialKarmaLevel;
    private int _displayedKarmaLevel;
    private int _age;

    internal KarmicBindingEffect(
        KarmaSpear source,
        Creature target,
        Vector2 anchor,
        KarmicTargetProfile profile,
        bool sourceMustRemainLodged)
    {
        _source = source;
        _target = target;
        _profile = profile;
        _sourceMustRemainLodged = sourceMustRemainLodged;
        _chargeGeneration = source.ChargeGeneration;
        _initialKarmaLevel = Mathf.Clamp(source.KarmaLevel, 1, 10);
        _displayedKarmaLevel = _initialKarmaLevel;

        _anchor = TryGetBodyState(target, out Vector2 bodyCenter, out _)
            ? bodyCenter
            : anchor;

        _duration = DurationFor(profile.ResponseClass, _initialKarmaLevel);
        target.Stun(InitialStunFor(profile.ResponseClass));

        // Signature burst movement is cancelled once at impact. Persistent restraint is
        // still handled by the bounded centre-of-mass controller below.
        if (profile.Special == KarmicSpecialResponse.RedLizardCrisis)
        {
            ScaleAllVelocity(0.30f);
        }
        else if (profile.Special == KarmicSpecialResponse.CyanLeapInterrupt)
        {
            ScaleAllVelocity(0.42f);
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

        BindingTuning tuning = BindingTuning.For(_profile.ResponseClass);

        // 1 = fresh/full charge, 0 = final remaining karma level.
        float strength = _initialKarmaLevel <= 1
            ? 1f
            : Mathf.InverseLerp(1f, _initialKarmaLevel, _displayedKarmaLevel);

        float leashRadius = Mathf.Lerp(tuning.LooseLeash, tuning.TightLeash, strength);
        float translationDamping = Mathf.Lerp(tuning.LooseDamping, tuning.TightDamping, strength);
        float maxTranslationRemoval = Mathf.Lerp(
            tuning.LooseTranslationCap,
            tuning.TightTranslationCap,
            strength);

        // First remove a bounded amount of whole-body translation. Applying one identical
        // delta to every chunk preserves relative head/tail/limb motion.
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

            // Only remove excess radial escape speed. This brake cannot create an
            // oscillation because it never accelerates an already-inward moving creature
            // farther outward.
            float outwardSpeedLimit = Mathf.Lerp(
                tuning.LooseOutwardSpeed,
                tuning.TightOutwardSpeed,
                strength);
            float inwardSpeedLimit = Mathf.Lerp(
                tuning.LooseInwardSpeed,
                tuning.TightInwardSpeed,
                strength);

            float radialSpeed = Vector2.Dot(estimatedVelocity, outward);
            float clampedRadialSpeed = Mathf.Clamp(
                radialSpeed,
                -inwardSpeedLimit,
                outwardSpeedLimit);
            float radialCorrectionAmount = radialSpeed - clampedRadialSpeed;
            float radialCap = Mathf.Lerp(tuning.LooseRadialCap, tuning.TightRadialCap, strength);
            Vector2 radialBrake = outward * Mathf.Clamp(
                radialCorrectionAmount,
                -radialCap,
                radialCap);

            ApplyUniformVelocityDelta(-radialBrake);
            estimatedVelocity -= radialBrake;

            // A very small bounded inward pull handles positional overshoot. Unlike the
            // old per-chunk spring, this term has an explicit acceleration ceiling.
            float tetherStiffness = Mathf.Lerp(
                tuning.LooseTetherStiffness,
                tuning.TightTetherStiffness,
                strength);
            float maxInwardAcceleration = Mathf.Lerp(
                tuning.LooseInwardAcceleration,
                tuning.TightInwardAcceleration,
                strength);
            float inwardAcceleration = Mathf.Min(
                maxInwardAcceleration,
                overshoot * tetherStiffness);

            Vector2 tetherDelta = -outward * inwardAcceleration;
            ApplyUniformVelocityDelta(tetherDelta);
            estimatedVelocity += tetherDelta;
        }

        // Final guard against a creature adding a large locomotion impulse in one frame.
        // Only centre-of-mass speed is limited; individual chunks remain free to animate.
        float maxCenterSpeed = Mathf.Lerp(
            tuning.LooseCenterSpeed,
            tuning.TightCenterSpeed,
            strength);

        if (estimatedVelocity.magnitude > maxCenterSpeed)
        {
            Vector2 excess = estimatedVelocity -
                             estimatedVelocity.normalized * maxCenterSpeed;
            Vector2 safetyRemoval = Vector2.ClampMagnitude(
                excess,
                Mathf.Lerp(tuning.LooseSafetyCap, tuning.TightSafetyCap, strength));
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

    private void ScaleAllVelocity(float factor)
    {
        if (_target?.bodyChunks == null)
        {
            return;
        }

        for (int i = 0; i < _target.bodyChunks.Length; i++)
        {
            _target.bodyChunks[i].vel *= factor;
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
        _source.SetActiveEffectKarmaLevel(_chargeGeneration, remainingLevel);

        KarmicVisualEffects.SpawnImpactPulse(
            _source,
            _source.firstChunk.pos,
            remainingLevel,
            30f + remainingLevel * 2f);
    }

    private void Finish()
    {
        if (slatedForDeletetion)
        {
            return;
        }

        _source?.MarkSpent(_chargeGeneration);
        Destroy();
    }

    private static int DurationFor(KarmicResponseClass responseClass, int karmaLevel)
    {
        return responseClass switch
        {
            KarmicResponseClass.Light => 82 + karmaLevel * 5,
            KarmicResponseClass.Standard => 92 + karmaLevel * 5,
            KarmicResponseClass.Heavy => 118 + karmaLevel * 7,
            _ => 92 + karmaLevel * 5
        };
    }

    private static int InitialStunFor(KarmicResponseClass responseClass)
    {
        return responseClass switch
        {
            KarmicResponseClass.Light => 38,
            KarmicResponseClass.Standard => 34,
            KarmicResponseClass.Heavy => 24,
            _ => 30
        };
    }

    private readonly struct BindingTuning
    {
        internal readonly float TightLeash;
        internal readonly float LooseLeash;
        internal readonly float TightDamping;
        internal readonly float LooseDamping;
        internal readonly float TightTranslationCap;
        internal readonly float LooseTranslationCap;
        internal readonly float TightOutwardSpeed;
        internal readonly float LooseOutwardSpeed;
        internal readonly float TightInwardSpeed;
        internal readonly float LooseInwardSpeed;
        internal readonly float TightRadialCap;
        internal readonly float LooseRadialCap;
        internal readonly float TightTetherStiffness;
        internal readonly float LooseTetherStiffness;
        internal readonly float TightInwardAcceleration;
        internal readonly float LooseInwardAcceleration;
        internal readonly float TightCenterSpeed;
        internal readonly float LooseCenterSpeed;
        internal readonly float TightSafetyCap;
        internal readonly float LooseSafetyCap;

        private BindingTuning(
            float tightLeash,
            float looseLeash,
            float tightDamping,
            float looseDamping,
            float tightTranslationCap,
            float looseTranslationCap,
            float tightOutwardSpeed,
            float looseOutwardSpeed,
            float tightInwardSpeed,
            float looseInwardSpeed,
            float tightRadialCap,
            float looseRadialCap,
            float tightTetherStiffness,
            float looseTetherStiffness,
            float tightInwardAcceleration,
            float looseInwardAcceleration,
            float tightCenterSpeed,
            float looseCenterSpeed,
            float tightSafetyCap,
            float looseSafetyCap)
        {
            TightLeash = tightLeash;
            LooseLeash = looseLeash;
            TightDamping = tightDamping;
            LooseDamping = looseDamping;
            TightTranslationCap = tightTranslationCap;
            LooseTranslationCap = looseTranslationCap;
            TightOutwardSpeed = tightOutwardSpeed;
            LooseOutwardSpeed = looseOutwardSpeed;
            TightInwardSpeed = tightInwardSpeed;
            LooseInwardSpeed = looseInwardSpeed;
            TightRadialCap = tightRadialCap;
            LooseRadialCap = looseRadialCap;
            TightTetherStiffness = tightTetherStiffness;
            LooseTetherStiffness = looseTetherStiffness;
            TightInwardAcceleration = tightInwardAcceleration;
            LooseInwardAcceleration = looseInwardAcceleration;
            TightCenterSpeed = tightCenterSpeed;
            LooseCenterSpeed = looseCenterSpeed;
            TightSafetyCap = tightSafetyCap;
            LooseSafetyCap = looseSafetyCap;
        }

        internal static BindingTuning For(KarmicResponseClass responseClass)
        {
            return responseClass switch
            {
                KarmicResponseClass.Light => new BindingTuning(
                    tightLeash: 7f,
                    looseLeash: 20f,
                    tightDamping: 0.30f,
                    looseDamping: 0.08f,
                    tightTranslationCap: 0.78f,
                    looseTranslationCap: 0.34f,
                    tightOutwardSpeed: 1.45f,
                    looseOutwardSpeed: 4.2f,
                    tightInwardSpeed: 3.0f,
                    looseInwardSpeed: 5.6f,
                    tightRadialCap: 1.00f,
                    looseRadialCap: 0.42f,
                    tightTetherStiffness: 0.038f,
                    looseTetherStiffness: 0.012f,
                    tightInwardAcceleration: 0.42f,
                    looseInwardAcceleration: 0.10f,
                    tightCenterSpeed: 3.0f,
                    looseCenterSpeed: 5.8f,
                    tightSafetyCap: 1.12f,
                    looseSafetyCap: 0.55f),

                KarmicResponseClass.Heavy => new BindingTuning(
                    tightLeash: 17f,
                    looseLeash: 44f,
                    tightDamping: 0.16f,
                    looseDamping: 0.05f,
                    tightTranslationCap: 0.48f,
                    looseTranslationCap: 0.24f,
                    tightOutwardSpeed: 2.8f,
                    looseOutwardSpeed: 6.2f,
                    tightInwardSpeed: 4.6f,
                    looseInwardSpeed: 7.5f,
                    tightRadialCap: 0.72f,
                    looseRadialCap: 0.32f,
                    tightTetherStiffness: 0.023f,
                    looseTetherStiffness: 0.008f,
                    tightInwardAcceleration: 0.27f,
                    looseInwardAcceleration: 0.08f,
                    tightCenterSpeed: 4.8f,
                    looseCenterSpeed: 8.2f,
                    tightSafetyCap: 0.82f,
                    looseSafetyCap: 0.44f),

                _ => new BindingTuning(
                    tightLeash: 10f,
                    looseLeash: 28f,
                    tightDamping: 0.24f,
                    looseDamping: 0.07f,
                    tightTranslationCap: 0.64f,
                    looseTranslationCap: 0.30f,
                    tightOutwardSpeed: 1.8f,
                    looseOutwardSpeed: 4.8f,
                    tightInwardSpeed: 3.6f,
                    looseInwardSpeed: 6.2f,
                    tightRadialCap: 0.95f,
                    looseRadialCap: 0.38f,
                    tightTetherStiffness: 0.034f,
                    looseTetherStiffness: 0.010f,
                    tightInwardAcceleration: 0.40f,
                    looseInwardAcceleration: 0.10f,
                    tightCenterSpeed: 3.6f,
                    looseCenterSpeed: 6.4f,
                    tightSafetyCap: 1.05f,
                    looseSafetyCap: 0.50f)
            };
        }
    }
}
