using System.Collections.Generic;
using DryCycle.Framework.KarmicManipulation;
using UnityEngine;
using Watcher;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// Timed one-way karmic barrier created when an active Karma Spear is nailed into terrain.
/// Deployment consumes one stored Karma point immediately. Its radius uses the pre-consumption
/// level and its lifetime is that level multiplied by five seconds.
/// The boundary is communicated by expanding pulses rather than a persistent fixed circle.
/// Creatures that begin inside may leave, but once outside they cannot re-enter.
/// </summary>
internal sealed class KarmaSpearField : UpdatableAndDeletable, IDrawable
{
    private const int InitialFormationFrames = 28;

    private readonly KarmaSpear _source;
    private readonly int _activationKarmaLevel;
    private readonly int _duration;
    private readonly float _radius;
    private readonly StaticSoundLoop _soundLoop;
    private readonly Dictionary<Creature, int> _creatureSoundAges = new();
    private readonly Dictionary<Creature, CreatureBarrierState> _creatureStates = new();

    private int _age;

    internal KarmaSpearField(
        KarmaSpear source,
        int activationKarmaLevel,
        int duration)
    {
        _source = source;
        _activationKarmaLevel = Mathf.Clamp(activationKarmaLevel, 1, 10);
        _duration = Mathf.Max(1, duration);

        // Preserve the existing deployed-size formula, evaluated with the level that was
        // present immediately before this deployment consumed one stored point.
        _radius = (58f + _activationKarmaLevel * 4f) * 4.5f;

        _soundLoop = new StaticSoundLoop(
            WatcherEnums.WatcherSoundID.Warp_Point_Ripple_Idle_LOOP,
            source.firstChunk.pos,
            source.room,
            0.25f,
            1.10f)
        {
            fadeOutOnDestroyFrames = 8,
            randomStartPosition = true
        };
    }

    public override void Update(bool eu)
    {
        base.Update(eu);
        _age++;

        // A last stored point is allowed to power the whole deployment even though the spear
        // itself becomes spent as soon as that point is consumed.
        if (_source == null ||
            _source.slatedForDeletetion ||
            _source.room != room ||
            _source.mode != Weapon.Mode.StuckInWall)
        {
            Destroy();
            return;
        }

        Vector2 center = _source.firstChunk.pos;

        EnforceBarrier(center);
        UpdateSound(center);
        CleanupTrackedCreatures();

        // The expanding pulse is the only range visualization. There is intentionally no
        // persistent ring left on screen between pulses.
        if (_age == 1 || _age % 28 == 0)
        {
            KarmicVisualEffects.SpawnFieldPulse(_source, _radius);
        }

        if (_age % 96 == 0)
        {
            room.PlaySound(
                WatcherEnums.WatcherSoundID.Templar_Shield_Tick_6,
                center,
                0.28f,
                1.16f);
        }

        if (_age >= _duration)
        {
            Destroy();
        }
    }

    public override void Destroy()
    {
        StopSound();
        base.Destroy();
    }

    private void UpdateSound(Vector2 center)
    {
        float breath = 0.5f + 0.5f * Mathf.Sin(_age * 0.030f);
        float levelStrength = Mathf.Lerp(0.72f, 1f, _activationKarmaLevel / 10f);
        _soundLoop.pos = center;
        _soundLoop.volume = Mathf.Lerp(0.20f, 0.30f, breath) * levelStrength;
        _soundLoop.pitch = Mathf.Lerp(1.06f, 1.14f, breath);
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

    private void EnforceBarrier(Vector2 center)
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

                if (obj is Weapon weapon && weapon.mode == Weapon.Mode.Thrown)
                {
                    TryReflectProjectile(weapon, center);
                    continue;
                }

                if (obj is Creature creature &&
                    !object.ReferenceEquals(creature, _source.thrownBy) &&
                    !IsWatcherScavengerBarrierExempt(creature))
                {
                    TryBlockCreature(creature, center);
                }
            }
        }
    }

    private static bool IsWatcherScavengerBarrierExempt(Creature creature)
    {
        string typeName = creature?.Template?.type?.value ?? string.Empty;
        return typeName == "ScavengerTemplar" || typeName == "ScavengerDisciple";
    }

    private void TryReflectProjectile(Weapon weapon, Vector2 center)
    {
        BodyChunk chunk = weapon.firstChunk;
        float collisionRadius = _radius + Mathf.Max(1f, chunk.rad);
        Vector2 from = chunk.lastPos;
        Vector2 to = chunk.pos;

        // Projectiles already inside may leave freely. Only outside -> inside travel is blocked.
        if ((from - center).sqrMagnitude <= collisionRadius * collisionRadius)
        {
            return;
        }

        if (!TryGetCircleEntry(from, to, center, collisionRadius, out Vector2 hitPoint, out Vector2 normal))
        {
            return;
        }

        Vector2 travel = to - from;
        if (Vector2.Dot(travel, normal) >= 0f)
        {
            return;
        }

        Vector2 targetPos = hitPoint + normal * 2.5f;
        ShiftPhysicalObject(weapon, targetPos - chunk.pos);

        for (int i = 0; i < weapon.bodyChunks.Length; i++)
        {
            BodyChunk bodyChunk = weapon.bodyChunks[i];
            float inwardSpeed = Vector2.Dot(bodyChunk.vel, normal);
            if (inwardSpeed < 0f)
            {
                bodyChunk.vel -= normal * (2f * inwardSpeed);
                bodyChunk.vel *= 0.84f;
                bodyChunk.vel += normal * 0.75f;
            }
        }

        room.PlaySound(
            WatcherEnums.WatcherSoundID.Templar_Shield_Tick_9,
            hitPoint,
            0.72f,
            1.14f);
        KarmicVisualEffects.SpawnSparks(room, hitPoint, 4, 3.8f);
    }

    private void TryBlockCreature(Creature creature, Vector2 center)
    {
        if (creature?.mainBodyChunk == null || creature.bodyChunks == null || creature.bodyChunks.Length == 0)
        {
            return;
        }

        if (!_creatureStates.TryGetValue(creature, out CreatureBarrierState state))
        {
            // Classify by the body centre only once. A creature that genuinely began inside is
            // allowed to leave through the one-way barrier. An outside creature is locked out
            // immediately even if one long limb/chunk already overlaps the boundary.
            bool beganInside = (creature.mainBodyChunk.pos - center).sqrMagnitude < _radius * _radius;
            state = new CreatureBarrierState(beganInside);
            _creatureStates.Add(creature, state);
        }

        if (state.AllowUntilFullyOutside)
        {
            if (!IsCreatureFullyOutside(creature, center))
            {
                return;
            }

            // Once an initially-inside creature has completely left, it becomes an outside
            // creature for the remainder of this field and cannot re-enter.
            state.AllowUntilFullyOutside = false;
        }

        if (!TryFindCreatureBoundaryCorrection(
                creature,
                center,
                out Vector2 correction,
                out Vector2 contactNormal,
                out Vector2 hitPoint))
        {
            return;
        }

        // Move the whole creature by one common correction. Never push individual body chunks
        // independently: doing so fights BodyChunkConnections and can create lizard acceleration.
        ShiftPhysicalObject(creature, correction);

        // Remove only velocity aimed into the protected area. Tangential/outward motion remains,
        // so creatures slide along the boundary rather than being stunned or launched.
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk bodyChunk = creature.bodyChunks[i];
            float inwardSpeed = Vector2.Dot(bodyChunk.vel, contactNormal);
            if (inwardSpeed < 0f)
            {
                bodyChunk.vel -= contactNormal * inwardSpeed;
            }
        }

        if (!_creatureSoundAges.TryGetValue(creature, out int lastSoundAge) || _age - lastSoundAge >= 12)
        {
            _creatureSoundAges[creature] = _age;
            room.PlaySound(
                WatcherEnums.WatcherSoundID.Templar_Shield_Tick_8,
                hitPoint,
                0.48f,
                1.10f);
            KarmicVisualEffects.SpawnSparks(room, hitPoint, 3, 2.8f);
        }
    }

    private bool TryFindCreatureBoundaryCorrection(
        Creature creature,
        Vector2 center,
        out Vector2 correction,
        out Vector2 contactNormal,
        out Vector2 hitPoint)
    {
        correction = Vector2.zero;
        contactNormal = Vector2.zero;
        hitPoint = Vector2.zero;

        float bestCorrectionSqr = 0f;
        bool blocked = false;

        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            float collisionRadius = _radius + Mathf.Max(3f, chunk.rad);
            Vector2 radial = chunk.pos - center;
            float distance = radial.magnitude;

            // Normal case: a body chunk currently overlaps the protected side of the circle.
            // Push the entire creature outward far enough to place this chunk back outside.
            if (distance < collisionRadius)
            {
                Vector2 normal = distance > 0.001f
                    ? radial / distance
                    : SafeOutwardNormal(chunk.lastPos - center);
                Vector2 candidate = normal * (collisionRadius - distance + 1.5f);

                if (!blocked || candidate.sqrMagnitude > bestCorrectionSqr)
                {
                    blocked = true;
                    bestCorrectionSqr = candidate.sqrMagnitude;
                    correction = candidate;
                    contactNormal = normal;
                    hitPoint = center + normal * _radius;
                }

                continue;
            }

            // Swept test closes the high-speed/tunnelling case: both endpoints can be outside
            // while the body chunk crossed through the circle during the frame.
            Vector2 from = chunk.lastPos;
            if ((from - center).sqrMagnitude <= collisionRadius * collisionRadius ||
                !TryGetCircleEntry(from, chunk.pos, center, collisionRadius, out Vector2 entryPoint, out Vector2 entryNormal) ||
                Vector2.Dot(chunk.pos - from, entryNormal) >= 0f)
            {
                continue;
            }

            Vector2 targetPos = entryPoint + entryNormal * 1.5f;
            Vector2 sweptCorrection = targetPos - chunk.pos;
            if (!blocked || sweptCorrection.sqrMagnitude > bestCorrectionSqr)
            {
                blocked = true;
                bestCorrectionSqr = sweptCorrection.sqrMagnitude;
                correction = sweptCorrection;
                contactNormal = entryNormal;
                hitPoint = center + entryNormal * _radius;
            }
        }

        return blocked;
    }

    private bool IsCreatureFullyOutside(Creature creature, Vector2 center)
    {
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            float outsideRadius = _radius + Mathf.Max(3f, chunk.rad) + 2f;
            if ((chunk.pos - center).sqrMagnitude <= outsideRadius * outsideRadius)
            {
                return false;
            }
        }

        return true;
    }

    private static Vector2 SafeOutwardNormal(Vector2 radial)
    {
        return radial.sqrMagnitude > 0.000001f ? radial.normalized : Vector2.up;
    }

    private static bool TryGetCircleEntry(
        Vector2 from,
        Vector2 to,
        Vector2 center,
        float radius,
        out Vector2 hitPoint,
        out Vector2 normal)
    {
        hitPoint = Vector2.zero;
        normal = Vector2.zero;

        Vector2 delta = to - from;
        float a = Vector2.Dot(delta, delta);
        if (a <= 0.000001f)
        {
            return false;
        }

        Vector2 offset = from - center;
        float b = 2f * Vector2.Dot(offset, delta);
        float c = Vector2.Dot(offset, offset) - radius * radius;
        float discriminant = b * b - 4f * a * c;
        if (discriminant < 0f)
        {
            return false;
        }

        float sqrt = Mathf.Sqrt(discriminant);
        float inv = 1f / (2f * a);
        float t0 = (-b - sqrt) * inv;
        float t1 = (-b + sqrt) * inv;
        float t = float.PositiveInfinity;

        if (t0 >= 0f && t0 <= 1f)
        {
            t = t0;
        }
        else if (t1 >= 0f && t1 <= 1f)
        {
            t = t1;
        }

        if (float.IsPositiveInfinity(t))
        {
            return false;
        }

        hitPoint = from + delta * t;
        Vector2 radial = hitPoint - center;
        if (radial.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        normal = radial.normalized;
        return true;
    }

    private static void ShiftPhysicalObject(PhysicalObject obj, Vector2 delta)
    {
        if (obj?.bodyChunks == null || delta.sqrMagnitude <= 0.000001f)
        {
            return;
        }

        for (int i = 0; i < obj.bodyChunks.Length; i++)
        {
            BodyChunk chunk = obj.bodyChunks[i];
            chunk.pos += delta;
            chunk.lastPos += delta;
        }
    }

    private void CleanupTrackedCreatures()
    {
        if (_age % 120 != 0)
        {
            return;
        }

        List<Creature> remove = new();
        foreach (KeyValuePair<Creature, CreatureBarrierState> pair in _creatureStates)
        {
            if (pair.Key == null || pair.Key.slatedForDeletetion || pair.Key.room != room)
            {
                remove.Add(pair.Key);
            }
        }

        for (int i = 0; i < remove.Count; i++)
        {
            Creature creature = remove[i];
            if (creature == null)
            {
                continue;
            }

            _creatureStates.Remove(creature);
            _creatureSoundAges.Remove(creature);
        }
    }

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        int glyphValue = Mathf.Clamp(_activationKarmaLevel - 1, 0, 9);
        sLeaser.sprites = new[]
        {
            new FSprite(
                global::HUD.KarmaMeter.KarmaSymbolSprite(
                    small: false,
                    new IntVector2(glyphValue, glyphValue)))
        };

        AddToContainer(sLeaser, rCam, rCam.ReturnFContainer("Foreground"));
    }

    public void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        if (_source == null || _source.slatedForDeletetion || slatedForDeletetion || room != rCam.room)
        {
            sLeaser.CleanSpritesAndRemove();
            return;
        }

        // The barrier itself has no persistent sprite. Only the first formation pulse carries
        // the activation Karma glyph; all later range information comes from expanding pulses.
        FSprite glyph = sLeaser.sprites[0];
        float formationT = Mathf.Clamp01((_age + timeStacker) / InitialFormationFrames);
        bool showInitialGlyph = _age < InitialFormationFrames;
        glyph.isVisible = showInitialGlyph;
        if (!showInitialGlyph)
        {
            return;
        }

        Vector2 center = Vector2.Lerp(
            _source.firstChunk.lastPos,
            _source.firstChunk.pos,
            timeStacker);
        Vector2 drawPos = center - camPos;
        float envelope = Mathf.Sin(formationT * Mathf.PI);
        float levelStrength = Mathf.Lerp(0.45f, 1f, _activationKarmaLevel / 10f);

        glyph.x = drawPos.x;
        glyph.y = drawPos.y;
        glyph.scale = Mathf.Lerp(0.58f, 1.05f, formationT);
        glyph.alpha = envelope * Mathf.Lerp(0.72f, 0.96f, levelStrength);
        glyph.color = Color.Lerp(Color.white, KarmicVisualEffects.Gold, 0.58f);
    }

    public void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
    }

    public void AddToContainer(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        FContainer newContainer)
    {
        newContainer ??= rCam.ReturnFContainer("Foreground");
        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            newContainer.AddChild(sLeaser.sprites[i]);
        }
    }

    private sealed class CreatureBarrierState
    {
        internal CreatureBarrierState(bool allowUntilFullyOutside)
        {
            AllowUntilFullyOutside = allowUntilFullyOutside;
        }

        internal bool AllowUntilFullyOutside { get; set; }
    }
}
