using DryCycle.Framework.KarmicManipulation;
using UnityEngine;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// Pins a creature around the physical configuration it had at impact instead of
/// replacing the interaction with a long vanilla stun. Chunks may still writhe and
/// limbs/graphics keep animating, but locomotion cannot immediately carry the body away.
/// </summary>
internal sealed class KarmicBindingEffect : UpdatableAndDeletable
{
    private readonly KarmaSpear _source;
    private readonly Creature _target;
    private readonly Vector2[] _relativeChunkOffsets;
    private readonly Vector2 _anchor;
    private readonly int _duration;
    private readonly bool _sourceMustRemainLodged;
    private readonly float _spring;
    private readonly float _velocityDamping;
    private readonly int _chargeGeneration;
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
        _anchor = anchor;
        _sourceMustRemainLodged = sourceMustRemainLodged;
        _chargeGeneration = source.ChargeGeneration;
        _duration = (largeTarget ? 128 : 88) + source.KarmaLevel * (largeTarget ? 7 : 5);
        _spring = largeTarget ? 0.012f : 0.024f;
        _velocityDamping = largeTarget ? 0.958f : 0.925f;

        _relativeChunkOffsets = new Vector2[target.bodyChunks.Length];
        Vector2 center = target.mainBodyChunk.pos;
        for (int i = 0; i < target.bodyChunks.Length; i++)
        {
            _relativeChunkOffsets[i] = target.bodyChunks[i].pos - center;
        }

        target.Stun(largeTarget ? 24 : 34);

        // Large fliers and runners should visibly lose their current action without
        // being hard-frozen for the entire bind duration.
        if (largeTarget)
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

        Vector2 targetCenter = _target.mainBodyChunk.pos;
        Vector2 anchorCorrection = _anchor - targetCenter;
        if (anchorCorrection.magnitude > 90f)
        {
            anchorCorrection = anchorCorrection.normalized * 90f;
        }

        for (int i = 0; i < _target.bodyChunks.Length; i++)
        {
            BodyChunk chunk = _target.bodyChunks[i];
            Vector2 desired = _anchor + _relativeChunkOffsets[i];
            Vector2 correction = desired - chunk.pos;
            if (correction.magnitude > 45f)
            {
                correction = correction.normalized * 45f;
            }

            chunk.vel += correction * _spring;
            chunk.vel += anchorCorrection * (_spring * 0.3f);
            chunk.vel *= _velocityDamping;
        }

        if (_age % 24 == 0)
        {
            KarmicVisualEffects.SpawnFieldPulse(_source, 42f + _source.KarmaLevel * 2f);
        }

        if (_age >= _duration)
        {
            Finish();
        }
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
