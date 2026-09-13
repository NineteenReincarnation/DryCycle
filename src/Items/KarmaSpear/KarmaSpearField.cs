using System.Collections.Generic;
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
    private readonly StaticSoundLoop _soundLoop;
    private readonly Dictionary<Weapon, ProjectileTimeState> _projectileStates = new();
    private readonly HashSet<Weapon> _projectilesInside = new();
    private readonly List<Weapon> _projectileCleanup = new();
    private int _age;

    internal KarmaSpearField(KarmaSpear source)
    {
        _source = source;

        // The deployed radius is 4.5x the original Karma Field design.
        _radius = (58f + source.KarmaLevel * 4f) * 4.5f;

        // This is a stationary world-space field after the spear has entered StuckInWall.
        // Watcher's own warp-point ambience uses StaticSoundLoop for this exact style of
        // persistent positional loop.
        _soundLoop = new StaticSoundLoop(
            WatcherEnums.WatcherSoundID.Warp_Point_Ripple_Idle_LOOP,
            source.firstChunk.pos,
            source.room,
            0.34f,
            0.88f)
        {
            fadeOutOnDestroyFrames = 10,
            randomStartPosition = true
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
        UpdateSound(center);

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
        RestoreAllProjectiles();
        StopSound();
        base.Destroy();
    }

    private void UpdateSound(Vector2 center)
    {
        float breath = 0.5f + 0.5f * Mathf.Sin(_age * 0.025f);
        _soundLoop.pos = center;
        _soundLoop.volume = Mathf.Lerp(0.28f, 0.42f, breath);
        _soundLoop.pitch = Mathf.Lerp(0.84f, 0.92f, breath);
        _soundLoop.Update();
    }

    private void StopSound()
    {
        if (_soundLoop == null)
        {
            return;
        }

        _soundLoop.volume = 0f;
        _soundLoop.Update();
    }

    private void SuppressMotion(Vector2 center)
    {
        if (room?.physicalObjects == null)
        {
            RestoreAllProjectiles();
            return;
        }

        _projectilesInside.Clear();

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
                    ApplyProjectileBulletTime(weapon, influence);
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

        RestoreProjectilesThatLeftField();
    }

    private void ApplyProjectileBulletTime(Weapon weapon, float influence)
    {
        _projectilesInside.Add(weapon);

        if (!_projectileStates.TryGetValue(weapon, out ProjectileTimeState state))
        {
            state = new ProjectileTimeState(weapon);
            _projectileStates.Add(weapon, state);
        }

        // This is temporal slowdown, not drag. Velocity direction is frozen to the entry
        // trajectory and speed is derived from the stored entry speed every frame, so the
        // field never bends a projectile and never compounds it toward zero. Deeper inside
        // the field time runs slower; leaving restores the stored speed along the same path.
        float shapedInfluence = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(influence));
        float timeScale = Mathf.Lerp(0.72f, 0.18f, shapedInfluence);
        state.Apply(weapon, timeScale);
    }

    private void RestoreProjectilesThatLeftField()
    {
        _projectileCleanup.Clear();

        foreach (KeyValuePair<Weapon, ProjectileTimeState> pair in _projectileStates)
        {
            Weapon weapon = pair.Key;
            if (weapon == null || weapon.slatedForDeletetion || weapon.mode != Weapon.Mode.Thrown)
            {
                _projectileCleanup.Add(weapon);
                continue;
            }

            if (_projectilesInside.Contains(weapon))
            {
                continue;
            }

            pair.Value.Restore(weapon);
            _projectileCleanup.Add(weapon);
        }

        for (int i = 0; i < _projectileCleanup.Count; i++)
        {
            Weapon weapon = _projectileCleanup[i];
            if (weapon != null)
            {
                _projectileStates.Remove(weapon);
            }
        }
    }

    private void RestoreAllProjectiles()
    {
        foreach (KeyValuePair<Weapon, ProjectileTimeState> pair in _projectileStates)
        {
            Weapon weapon = pair.Key;
            if (weapon != null && !weapon.slatedForDeletetion && weapon.mode == Weapon.Mode.Thrown)
            {
                pair.Value.Restore(weapon);
            }
        }

        _projectileStates.Clear();
        _projectilesInside.Clear();
        _projectileCleanup.Clear();
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

    private sealed class ProjectileTimeState
    {
        private readonly Vector2[] _entryDirections;
        private readonly float[] _entrySpeeds;

        internal ProjectileTimeState(Weapon weapon)
        {
            _entryDirections = new Vector2[weapon.bodyChunks.Length];
            _entrySpeeds = new float[weapon.bodyChunks.Length];

            for (int i = 0; i < weapon.bodyChunks.Length; i++)
            {
                Vector2 velocity = weapon.bodyChunks[i].vel;
                float speed = velocity.magnitude;
                _entrySpeeds[i] = speed;
                _entryDirections[i] = speed > 0.001f ? velocity / speed : Vector2.zero;
            }
        }

        internal void Apply(Weapon weapon, float timeScale)
        {
            int count = Mathf.Min(weapon.bodyChunks.Length, _entrySpeeds.Length);
            for (int i = 0; i < count; i++)
            {
                weapon.bodyChunks[i].vel = _entryDirections[i] * (_entrySpeeds[i] * timeScale);
            }
        }

        internal void Restore(Weapon weapon)
        {
            int count = Mathf.Min(weapon.bodyChunks.Length, _entrySpeeds.Length);
            for (int i = 0; i < count; i++)
            {
                weapon.bodyChunks[i].vel = _entryDirections[i] * _entrySpeeds[i];
            }
        }
    }
}
