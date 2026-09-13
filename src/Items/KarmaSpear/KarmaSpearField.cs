using DryCycle.Framework.KarmicManipulation;
using UnityEngine;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// Temporary control volume created when an active Karma Spear is nailed into terrain.
/// It does not deal damage: it suppresses momentum, slows incoming thrown weapons and
/// occasionally helps a grabbed player break a dangerous grasp.
/// </summary>
internal sealed class KarmaSpearField : UpdatableAndDeletable
{
    private readonly KarmaSpear _source;
    private readonly float _radius;
    private readonly int _duration;
    private int _age;

    internal KarmaSpearField(KarmaSpear source)
    {
        _source = source;
        _radius = 58f + source.KarmaLevel * 4f;
        _duration = 145 + source.KarmaLevel * 7;
    }

    public override void Update(bool eu)
    {
        base.Update(eu);
        _age++;

        if (_source == null ||
            _source.slatedForDeletetion ||
            _source.room != room ||
            _source.IsSpent ||
            _source.mode != Weapon.Mode.StuckInWall)
        {
            Finish();
            return;
        }

        Vector2 center = _source.firstChunk.pos;
        SuppressMotion(center);

        if (_age == 1 || _age % 18 == 0)
        {
            KarmicVisualEffects.SpawnFieldPulse(_source, _radius);
        }

        if (_age % 30 == 0)
        {
            TryHelpDangerGrasps(center);
        }

        if (_age >= _duration)
        {
            Finish();
        }
    }

    private void SuppressMotion(Vector2 center)
    {
        if (room?.physicalObjects == null)
        {
            return;
        }

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            List<PhysicalObject> objects = room.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                PhysicalObject obj = objects[i];
                if (obj == null || object.ReferenceEquals(obj, _source) || obj.slatedForDeletetion)
                {
                    continue;
                }

                float distance = Vector2.Distance(center, obj.firstChunk.pos);
                if (distance >= _radius)
                {
                    continue;
                }

                float influence = 1f - distance / _radius;

                if (obj is Weapon weapon && weapon.mode == Weapon.Mode.Thrown)
                {
                    float factor = Mathf.Lerp(1f, 0.84f, influence);
                    for (int c = 0; c < weapon.bodyChunks.Length; c++)
                    {
                        weapon.bodyChunks[c].vel *= factor;
                    }
                    continue;
                }

                if (obj is Creature creature && !object.ReferenceEquals(creature, _source.thrownBy))
                {
                    float factor = Mathf.Lerp(1f, 0.955f, influence);
                    for (int c = 0; c < creature.bodyChunks.Length; c++)
                    {
                        creature.bodyChunks[c].vel *= factor;
                    }
                }
            }
        }
    }

    private void TryHelpDangerGrasps(Vector2 center)
    {
        if (room?.abstractRoom?.creatures == null)
        {
            return;
        }

        float releaseChance = Mathf.Lerp(0.11f, 0.24f, (_source.KarmaLevel - 1) / 9f);
        foreach (AbstractCreature abstractCreature in room.abstractRoom.creatures)
        {
            if (abstractCreature?.realizedCreature is not Player player ||
                player.dangerGrasp == null ||
                Vector2.Distance(center, player.mainBodyChunk.pos) > _radius ||
                Random.value >= releaseChance)
            {
                continue;
            }

            player.dangerGrasp.Release();
            KarmicVisualEffects.SpawnImpactPulse(
                _source,
                player.mainBodyChunk.pos,
                _source.KarmaLevel,
                52f);
        }
    }

    private void Finish()
    {
        if (!slatedForDeletetion)
        {
            _source?.MarkSpent();
            Destroy();
        }
    }
}
