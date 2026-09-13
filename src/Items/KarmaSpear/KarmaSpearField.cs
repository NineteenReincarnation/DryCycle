using DryCycle.Framework.KarmicManipulation;
using UnityEngine;
using Watcher;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// Persistent control volume created while an active Karma Spear is nailed into terrain.
/// Wall use does not consume the spear. The field exists for as long as the spear remains
/// stuck in the wall and disappears immediately when the spear is pulled free.
/// </summary>
internal sealed class KarmaSpearField : UpdatableAndDeletable
{
    private readonly KarmaSpear _source;
    private readonly float _radius;
    private readonly ChunkDynamicSoundLoop _soundLoop;
    private int _age;

    internal KarmaSpearField(KarmaSpear source)
    {
        _source = source;

        // The previous wall field already used a 3x radius. Increase that deployed
        // radius by another 50%, for a total multiplier of 4.5x over the original field.
        _radius = (58f + source.KarmaLevel * 4f) * 4.5f;

        // Keep the ambience spatially attached to the spear so distance attenuation,
        // room transitions and split-camera audio continue to use Rain World's normal path.
        _soundLoop = new ChunkDynamicSoundLoop(source.firstChunk)
        {
            sound = WatcherEnums.WatcherSoundID.Warp_Point_Ripple_Idle_LOOP,
            Volume = 0.11f,
            Pitch = 0.9f
        };
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
            Destroy();
            return;
        }

        Vector2 center = _source.firstChunk.pos;
        SuppressMotion(center);
        UpdateSound();

        // The field is permanent while anchored, so keep the large presentation pulse sparse.
        if (_age == 1 || _age % 28 == 0)
        {
            KarmicVisualEffects.SpawnFieldPulse(_source, _radius);
        }

        if (_age % 30 == 0)
        {
            TryHelpDangerGrasps(center);
        }
    }

    public override void Destroy()
    {
        _soundLoop?.Stop();
        base.Destroy();
    }

    private void UpdateSound()
    {
        // A very slow breathing modulation keeps the field alive without becoming a loud,
        // repetitive alarm. The loop remains anchored to the spear and stops on pull-out.
        float breath = 0.5f + 0.5f * Mathf.Sin(_age * 0.025f);
        _soundLoop.Volume = Mathf.Lerp(0.09f, 0.14f, breath);
        _soundLoop.Pitch = Mathf.Lerp(0.87f, 0.94f, breath);
        _soundLoop.Update();
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
}
