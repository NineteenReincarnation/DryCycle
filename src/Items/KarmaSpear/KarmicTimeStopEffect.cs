using DryCycle.Framework.KarmicManipulation;
using UnityEngine;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// Freezes a struck creature at the exact physical pose it had when karmic time stop began.
/// One stored Karma point is consumed before this object is created; the duration is based on
/// the spear's level before that consumption: level x 5 seconds. The stop remains active only
/// while the Karma Spear is physically embedded in the creature.
/// </summary>
internal sealed class KarmicTimeStopEffect : UpdatableAndDeletable
{
    private readonly KarmaSpear _source;
    private readonly Creature _target;
    private readonly KarmicTargetProfile _profile;
    private readonly int _chargeGeneration;
    private readonly int _activationKarmaLevel;
    private readonly int _duration;
    private readonly Vector2[] _frozenPositions;
    private readonly Vector2[] _frozenLastPositions;
    private readonly Vector2[] _resumeVelocities;
    private int _age;

    internal KarmicTimeStopEffect(
        KarmaSpear source,
        Creature target,
        KarmicTargetProfile profile,
        int activationKarmaLevel,
        int chargeGeneration)
    {
        _source = source;
        _target = target;
        _profile = profile;
        _chargeGeneration = chargeGeneration;
        _activationKarmaLevel = Mathf.Clamp(activationKarmaLevel, 1, 10);
        _duration = KarmaSpear.DurationTicksForStoredKarma(_activationKarmaLevel);

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

        // Time stop is physically anchored by the spear. Pulling the spear out switches it
        // away from StuckInCreature, so the target resumes immediately instead of waiting for
        // the original Karma-derived duration to expire. The charge was already spent at hit.
        if (_source.mode != Weapon.Mode.StuckInCreature)
        {
            Finish(restoreMotion: true);
            return;
        }

        FreezePose();

        if (_age == 1 || _age % 20 == 0)
        {
            KarmicVisualEffects.SpawnFieldPulse(
                _source,
                40f + _activationKarmaLevel * 2.5f);
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

        // Time stop consumes its single charge at activation, not at completion. Completion
        // only releases the effect. If the spear is still embedded because the timer expired,
        // CompleteCreatureEffect dislodges it; if it was manually pulled out, this is a no-op
        // for spear mode and only clears the active-effect latch.
        _source?.CompleteCreatureEffect(
            _chargeGeneration,
            _target,
            dropFromCreature: true);
        Destroy();
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
