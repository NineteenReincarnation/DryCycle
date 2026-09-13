using System.Collections.Generic;
using DryCycle.Framework.KarmicManipulation;
using UnityEngine;
using Watcher;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// Timed one-way karmic barrier created when an active Karma Spear is nailed into terrain.
/// Deployment consumes one stored Karma point immediately. Its radius uses the pre-consumption
/// level and its lifetime is that level multiplied by five seconds.
/// </summary>
internal sealed class KarmaSpearField : UpdatableAndDeletable, IDrawable
{
    private const int ArcSegments = 96;
    private const int VisibilityRefreshFrames = 12;

    private readonly KarmaSpear _source;
    private readonly int _activationKarmaLevel;
    private readonly int _duration;
    private readonly float _radius;
    private readonly StaticSoundLoop _soundLoop;
    private readonly bool[] _arcVisible = new bool[ArcSegments];
    private readonly Dictionary<Creature, int> _creatureSoundAges = new();

    private Vector2 _visibilityOrigin;
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

        if (_age == 1 || _age % VisibilityRefreshFrames == 0)
        {
            RefreshArcVisibility(center);
        }

        EnforceBarrier(center);
        UpdateSound(center);
        CleanupCreatureSoundAges();

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

        if ((from - center).sqrMagnitude <= collisionRadius * collisionRadius)
        {
            return;
        }

        if (!TryGetCircleEntry(from, to, center, collisionRadius, out Vector2 hitPoint, out Vector2 normal))
        {
            return;
        }

        if (!BarrierDirectionVisible(center, normal))
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

        BodyChunk main = creature.mainBodyChunk;
        float collisionRadius = _radius + Mathf.Max(3f, main.rad);
        Vector2 from = main.lastPos;
        Vector2 to = main.pos;

        if ((from - center).sqrMagnitude <= collisionRadius * collisionRadius)
        {
            return;
        }

        if (!TryGetCircleEntry(from, to, center, collisionRadius, out Vector2 hitPoint, out Vector2 normal))
        {
            return;
        }

        if (!BarrierDirectionVisible(center, normal) || Vector2.Dot(to - from, normal) >= 0f)
        {
            return;
        }

        Vector2 targetMainPos = hitPoint + normal * 1.5f;
        ShiftPhysicalObject(creature, targetMainPos - main.pos);

        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk bodyChunk = creature.bodyChunks[i];
            float inwardSpeed = Vector2.Dot(bodyChunk.vel, normal);
            if (inwardSpeed < 0f)
            {
                bodyChunk.vel -= normal * inwardSpeed;
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

    private void RefreshArcVisibility(Vector2 center)
    {
        _visibilityOrigin = FindOpenVisibilityOrigin(center);

        for (int i = 0; i < ArcSegments; i++)
        {
            float angle = ((i + 0.5f) / ArcSegments) * Mathf.PI * 2f;
            Vector2 direction = new(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 point = center + direction * _radius;
            _arcVisible[i] = room != null && room.VisualContact(_visibilityOrigin, point);
        }
    }

    private Vector2 FindOpenVisibilityOrigin(Vector2 center)
    {
        if (room == null)
        {
            return center;
        }

        Vector2 spearDirection = _source.rotation.sqrMagnitude > 0.001f
            ? _source.rotation.normalized
            : Vector2.right;

        Vector2 openSide = -spearDirection;
        Vector2 perpendicular = new(-openSide.y, openSide.x);
        Vector2[] directions =
        {
            openSide,
            perpendicular,
            -perpendicular,
            spearDirection
        };
        float[] distances = { 18f, 28f, 38f, 52f };

        for (int d = 0; d < distances.Length; d++)
        {
            for (int i = 0; i < directions.Length; i++)
            {
                Vector2 candidate = center + directions[i] * distances[d];
                if (!room.GetTile(candidate).Solid)
                {
                    return candidate;
                }
            }
        }

        return center;
    }

    private bool BarrierDirectionVisible(Vector2 center, Vector2 normal)
    {
        if (room == null)
        {
            return false;
        }

        Vector2 point = center + normal * _radius;
        return room.VisualContact(_visibilityOrigin, point);
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

    private void CleanupCreatureSoundAges()
    {
        if (_creatureSoundAges.Count == 0 || _age % 120 != 0)
        {
            return;
        }

        List<Creature> remove = new();
        foreach (KeyValuePair<Creature, int> pair in _creatureSoundAges)
        {
            if (pair.Key == null || pair.Key.slatedForDeletetion || pair.Key.room != room || _age - pair.Value > 240)
            {
                remove.Add(pair.Key);
            }
        }

        for (int i = 0; i < remove.Count; i++)
        {
            if (remove[i] != null)
            {
                _creatureSoundAges.Remove(remove[i]);
            }
        }
    }

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[ArcSegments];
        for (int i = 0; i < ArcSegments; i++)
        {
            sLeaser.sprites[i] = new FSprite("pixel")
            {
                anchorX = 0.5f,
                anchorY = 0.5f
            };
        }

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

        Vector2 center = Vector2.Lerp(
            _source.firstChunk.lastPos,
            _source.firstChunk.pos,
            timeStacker);
        float pulse = 0.5f + 0.5f * Mathf.Sin((_age + timeStacker) * 0.065f);
        float levelStrength = Mathf.Lerp(0.45f, 1f, _activationKarmaLevel / 10f);
        float thickness = Mathf.Lerp(1.05f, 1.45f, pulse);
        float alpha = Mathf.Lerp(0.42f, 0.66f, pulse) * levelStrength;
        float angleStep = Mathf.PI * 2f / ArcSegments;
        float chordLength = 2f * _radius * Mathf.Sin(angleStep * 0.5f) + 1.25f;
        Color color = Color.Lerp(KarmicVisualEffects.Gold, Color.white, 0.18f + pulse * 0.12f);

        for (int i = 0; i < ArcSegments; i++)
        {
            FSprite segment = sLeaser.sprites[i];
            if (!_arcVisible[i])
            {
                segment.isVisible = false;
                continue;
            }

            segment.isVisible = true;
            float angle = ((i + 0.5f) / ArcSegments) * Mathf.PI * 2f;
            Vector2 radial = new(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 tangent = new(-radial.y, radial.x);
            Vector2 midpoint = center + radial * _radius - camPos;

            segment.x = midpoint.x;
            segment.y = midpoint.y;
            segment.rotation = Mathf.Atan2(tangent.y, tangent.x) * Mathf.Rad2Deg;
            segment.scaleX = chordLength;
            segment.scaleY = thickness;
            segment.alpha = alpha;
            segment.color = color;
        }
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
}
