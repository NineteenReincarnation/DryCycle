using DryCycle.Framework.KarmicManipulation;
using UnityEngine;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// Freezes a struck creature at the exact physical pose it had when karmic time stop began.
/// No spring, tether or per-frame opposing impulse is used: body chunks are simply restored
/// to their captured pose after the creature's normal update, preventing feedback with native
/// locomotion while keeping the effect local to this creature.
/// </summary>
internal sealed class KarmicTimeStopEffect : UpdatableAndDeletable
{
    private readonly KarmaSpear _source;
    private readonly Creature _target;
    private readonly KarmicTargetProfile _profile;
    private readonly int _chargeGeneration;
    private readonly int _initialKarmaLevel;
    private readonly int _duration;
    private readonly Vector2[] _frozenPositions;
    private readonly Vector2[] _frozenLastPositions;
    private readonly Vector2[] _resumeVelocities;
    private int _displayedKarmaLevel;
    private int _age;

    internal KarmicTimeStopEffect(
        KarmaSpear source,
        Creature target,
        KarmicTargetProfile profile)
    {
        _source = source;
        _target = target;
        _profile = profile;
        _chargeGeneration = source.ChargeGeneration;
        _initialKarmaLevel = Mathf.Clamp(source.KarmaLevel, 1, 10);
        _displayedKarmaLevel = _initialKarmaLevel;
        _duration = DurationFor(profile.ResponseClass, _initialKarmaLevel);

        int chunkCount = target?.bodyChunks?.Length ?? 0;
        _frozenPositions = new Vector2[chunkCount];
        _frozenLastPositions = new Vector2[chunkCount];
        _resumeVelocities = new Vector2[chunkCount];

        for (int i = 0; i < chunkCount; i++)
        {
            BodyChunk chunk = target.bodyChunks[i];
            _frozenPositions[i] = chunk.pos;
            _frozenLastPositions[i] = chunk.lastPos;
            _resumeVelocities[i] = Vector2.ClampMagnitude(
                chunk.vel,
                ResumeSpeedCap(profile.ResponseClass));
        }

        // This suppresses most creature actions while the physical pose lock below handles
        // the authoritative movement freeze. It is intentionally short and refreshed each
        // frame rather than using a giant stun counter that can leak past the time stop.
        target?.Stun(2);
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
            Finish(restoreMotion: false);
            return;
        }

        UpdateRemainingKarma();
        FreezePose();

        if (_age == 1 || _age % 20 == 0)
        {
            KarmicVisualEffects.SpawnFieldPulse(
                _source,
                40f + _displayedKarmaLevel * 2.5f);
        }

        if (_age >= _duration)
        {
            Finish(restoreMotion: true);
        }
    }

    private void FreezePose()
    {
        if (_target?.bodyChunks == null)
        {
            return;
        }

        int count = Mathf.Min(_target.bodyChunks.Length, _frozenPositions.Length);
        for (int i = 0; i < count; i++)
        {
            BodyChunk chunk = _target.bodyChunks[i];
            chunk.pos = _frozenPositions[i];
            chunk.lastPos = _frozenLastPositions[i];
            chunk.vel = Vector2.zero;
        }

        _target.Stun(2);
    }

    private void RestoreMotion()
    {
        if (_target?.bodyChunks == null)
        {
            return;
        }

        int count = Mathf.Min(_target.bodyChunks.Length, _resumeVelocities.Length);
        float cap = ResumeSpeedCap(_profile.ResponseClass);

        for (int i = 0; i < count; i++)
        {
            BodyChunk chunk = _target.bodyChunks[i];
            chunk.pos = _frozenPositions[i];
            chunk.lastPos = _frozenPositions[i];
            chunk.vel = Vector2.ClampMagnitude(_resumeVelocities[i], cap);
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
            30f + remainingLevel * 2f);
    }

    private void Finish(bool restoreMotion)
    {
        if (slatedForDeletetion)
        {
            return;
        }

        if (restoreMotion)
        {
            RestoreMotion();
        }

        _source?.MarkSpent(_chargeGeneration);
        Destroy();
    }

    private static int DurationFor(KarmicResponseClass responseClass, int karmaLevel)
    {
        return responseClass switch
        {
            KarmicResponseClass.Light => 76 + karmaLevel * 5,
            KarmicResponseClass.Standard => 88 + karmaLevel * 5,
            KarmicResponseClass.Heavy => 104 + karmaLevel * 6,
            KarmicResponseClass.Colossal => 58 + karmaLevel * 3,
            KarmicResponseClass.Segmented => 82 + karmaLevel * 4,
            KarmicResponseClass.Anchored => 82 + karmaLevel * 4,
            KarmicResponseClass.Special => 88 + karmaLevel * 5,
            _ => 88 + karmaLevel * 5
        };
    }

    private static float ResumeSpeedCap(KarmicResponseClass responseClass)
    {
        return responseClass switch
        {
            KarmicResponseClass.Light => 8f,
            KarmicResponseClass.Standard => 9f,
            KarmicResponseClass.Heavy => 10f,
            KarmicResponseClass.Colossal => 7f,
            KarmicResponseClass.Segmented => 8f,
            KarmicResponseClass.Anchored => 5f,
            KarmicResponseClass.Special => 9f,
            _ => 9f
        };
    }
}
