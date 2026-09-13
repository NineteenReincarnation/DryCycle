using UnityEngine;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// Non-binding Karma Spear response for body plans that should not be tethered to a point.
/// It only removes or redistributes existing motion; apart from the one-time vulture drop,
/// it never adds a positional spring or an unbounded acceleration source.
/// </summary>
internal sealed class KarmicDisruptionEffect : UpdatableAndDeletable
{
    private readonly KarmaSpear _source;
    private readonly Creature _target;
    private readonly KarmicTargetProfile _profile;
    private readonly bool _sourceMustRemainLodged;
    private readonly int _chargeGeneration;
    private readonly int _initialKarmaLevel;
    private readonly int _duration;
    private int _displayedKarmaLevel;
    private int _age;

    internal KarmicDisruptionEffect(
        KarmaSpear source,
        Creature target,
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
        _duration = DurationFor(profile, _initialKarmaLevel);

        ApplyInitialInterruption();
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
        ApplyOngoingDisruption();

        if (_age % 24 == 0)
        {
            KarmicVisualEffects.SpawnFieldPulse(
                _source,
                38f + _displayedKarmaLevel * 2f);
        }

        if (_age >= _duration)
        {
            Finish();
        }
    }

    private void ApplyInitialInterruption()
    {
        if (_target?.bodyChunks == null)
        {
            return;
        }

        switch (_profile.Special)
        {
            case KarmicSpecialResponse.FlightInterrupt:
                _target.Stun(34);
                for (int i = 0; i < _target.bodyChunks.Length; i++)
                {
                    BodyChunk chunk = _target.bodyChunks[i];
                    chunk.vel *= 0.48f;
                    chunk.vel.y = Mathf.Min(chunk.vel.y, -1.25f);
                }
                break;

            case KarmicSpecialResponse.MirosGaitInterrupt:
                _target.Stun(48);
                ScaleAllVelocity(0.38f);
                break;

            case KarmicSpecialResponse.RotDisruption:
                _target.Stun(24);
                for (int i = 0; i < _target.bodyChunks.Length; i++)
                {
                    _target.bodyChunks[i].vel *= i % 2 == 0 ? 0.42f : 0.68f;
                }
                break;

            case KarmicSpecialResponse.SegmentedDisruption:
                _target.Stun(18);
                for (int i = 0; i < _target.bodyChunks.Length; i++)
                {
                    _target.bodyChunks[i].vel *= i % 2 == 0 ? 0.48f : 0.72f;
                }
                break;

            case KarmicSpecialResponse.AnchoredSuppression:
                _target.Stun(50);
                _target.LoseAllGrasps();
                ScaleAllVelocity(0.25f);
                break;

            case KarmicSpecialResponse.ColossalInterrupt:
                _target.Stun(24);
                ScaleAllVelocity(0.55f);
                break;
        }
    }

    private void ApplyOngoingDisruption()
    {
        if (_target?.bodyChunks == null)
        {
            return;
        }

        float strength = _initialKarmaLevel <= 1
            ? 1f
            : Mathf.InverseLerp(1f, _initialKarmaLevel, _displayedKarmaLevel);

        switch (_profile.Special)
        {
            case KarmicSpecialResponse.FlightInterrupt:
                // Suppress regained lift without steering the creature horizontally.
                for (int i = 0; i < _target.bodyChunks.Length; i++)
                {
                    BodyChunk chunk = _target.bodyChunks[i];
                    chunk.vel.x *= Mathf.Lerp(0.992f, 0.972f, strength);
                    if (chunk.vel.y > 0f)
                    {
                        chunk.vel.y *= Mathf.Lerp(0.94f, 0.72f, strength);
                    }
                }
                break;

            case KarmicSpecialResponse.MirosGaitInterrupt:
                ScaleAllVelocity(Mathf.Lerp(0.992f, 0.965f, strength));
                if (_age % 18 == 0)
                {
                    _target.Stun(5);
                }
                break;

            case KarmicSpecialResponse.RotDisruption:
                // Alternate which body phase is damped more strongly. This creates visible
                // desynchronization without injecting opposite impulses into the body chain.
                for (int i = 0; i < _target.bodyChunks.Length; i++)
                {
                    bool suppressedPhase = ((i + _age / 8) & 1) == 0;
                    float factor = suppressedPhase
                        ? Mathf.Lerp(0.990f, 0.955f, strength)
                        : Mathf.Lerp(0.996f, 0.982f, strength);
                    _target.bodyChunks[i].vel *= factor;
                }
                break;

            case KarmicSpecialResponse.SegmentedDisruption:
                for (int i = 0; i < _target.bodyChunks.Length; i++)
                {
                    bool suppressedPhase = ((i + _age / 6) & 1) == 0;
                    float factor = suppressedPhase
                        ? Mathf.Lerp(0.988f, 0.945f, strength)
                        : Mathf.Lerp(0.996f, 0.978f, strength);
                    _target.bodyChunks[i].vel *= factor;
                }
                if (_age % 26 == 0)
                {
                    _target.Stun(4);
                }
                break;

            case KarmicSpecialResponse.AnchoredSuppression:
                ScaleAllVelocity(Mathf.Lerp(0.990f, 0.955f, strength));
                if (_age % 15 == 0)
                {
                    _target.Stun(8);
                }
                break;

            case KarmicSpecialResponse.ColossalInterrupt:
                // Colossal creatures are never spatially pinned. The effect simply keeps
                // their current action from immediately rebuilding full momentum.
                ScaleAllVelocity(Mathf.Lerp(0.997f, 0.982f, strength));
                if (_age % 22 == 0)
                {
                    _target.Stun(4);
                }
                break;
        }
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
            28f + remainingLevel * 2f);
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

    private void Finish()
    {
        if (slatedForDeletetion)
        {
            return;
        }

        _source?.MarkSpent(_chargeGeneration);
        Destroy();
    }

    private static int DurationFor(KarmicTargetProfile profile, int karmaLevel)
    {
        return profile.Special switch
        {
            KarmicSpecialResponse.FlightInterrupt => 86 + karmaLevel * 5,
            KarmicSpecialResponse.MirosGaitInterrupt => 96 + karmaLevel * 6,
            KarmicSpecialResponse.RotDisruption => 112 + karmaLevel * 6,
            KarmicSpecialResponse.SegmentedDisruption => 104 + karmaLevel * 6,
            KarmicSpecialResponse.AnchoredSuppression => 108 + karmaLevel * 5,
            KarmicSpecialResponse.ColossalInterrupt => 72 + karmaLevel * 4,
            _ => 88 + karmaLevel * 5
        };
    }
}
