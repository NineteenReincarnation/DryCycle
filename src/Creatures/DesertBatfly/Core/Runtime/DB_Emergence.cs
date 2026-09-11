using System.Collections.Generic;
using DryCycle.TerrainExt.QuicksandZone;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DB_Emergence
{
    private enum EmergenceKind
    {
        None,
        Terrain,
        HiveDeparture
    }

    private readonly DB_Creature fly;
    private Vector2 surface, normal;
    private Vector2 hiveDepartureGoal;
    private int age;
    private EmergenceKind kind;
    internal bool Active { get; private set; }
    internal float Progress => !Active ? 1f : Mathf.Clamp01((age - 12f) / (DB_Tuning.EmergenceTicks - 12f));

    internal DB_Emergence(DB_Creature fly) { this.fly = fly; }

    internal void Begin(Vector2 point, Vector2 outward)
    {
        surface = point;
        normal = outward.normalized;
        age = 0;
        kind = EmergenceKind.Terrain;
        Active = true;
        fly.mainBodyChunk.HardSetPosition(surface - normal * 12f);
        fly.mainBodyChunk.vel = Vector2.zero;
        fly.CollideWithTerrain = false;
        fly.CollideWithObjects = false;
        fly.graphicsModule?.Reset();
    }

    /// <summary>
    /// Starts an exclusive post-hive departure corridor. While active, the behavior arbiter's
    /// existing Emergence owner suppresses ordinary, social, roost and combat decisions, so a
    /// newly released bat cannot immediately form a stationary flock around the BatHive tile.
    /// </summary>
    internal void BeginHiveDeparture()
    {
        if (fly?.room == null || fly.mainBodyChunk == null) return;

        surface = fly.mainBodyChunk.pos;
        normal = ChooseHiveOutward(fly.room, surface);
        hiveDepartureGoal = ChooseHiveDepartureGoal(fly.room, surface, normal);
        age = 0;
        kind = EmergenceKind.HiveDeparture;
        Active = true;

        fly.DesertAI.CancelAttack();
        DB_SocialRuntime.CancelForPriority(fly, "hive departure");
        DB_NeutralBehaviorRuntime.Forget(fly);
        fly.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        fly.AI.leaveRoomDijkstra = -1;
        fly.AI.followingDijkstraMap = -1;
        fly.AI.noSwarmCounter = Mathf.Max(fly.AI.noSwarmCounter, 180);
        fly.burrowOrHangSpot = null;
        fly.movMode = Fly.MovementMode.BatFlight;
        fly.CollideWithTerrain = true;
        fly.CollideWithObjects = true;
        fly.mainBodyChunk.vel = normal * 5.5f;
        fly.AI.localGoal = hiveDepartureGoal;
    }

    internal void Cancel()
    {
        if (!Active) return;
        Active = false;
        kind = EmergenceKind.None;
        fly.CollideWithTerrain = true;
        fly.CollideWithObjects = true;
    }

    internal void Update(bool eu)
    {
        if (!Active) return;
        if (fly.dead || fly.grabbedBy.Count > 0 || fly.room == null) { Cancel(); return; }

        if (kind == EmergenceKind.HiveDeparture)
        {
            UpdateHiveDeparture();
            return;
        }

        age++;
        fly.CollideWithTerrain = false;
        fly.CollideWithObjects = Progress > 0.5f;
        fly.mainBodyChunk.MoveFromOutsideMyUpdate(eu, surface + normal * Mathf.Lerp(-12f, 30f, Mathf.SmoothStep(0f, 1f, Progress)));
        fly.mainBodyChunk.vel = Vector2.zero;
        fly.dir = normal;
        if (age >= DB_Tuning.EmergenceTicks)
        {
            Cancel();
            fly.mainBodyChunk.vel = normal * 5f;
            fly.AI.localGoal = surface + normal * 100f;
        }
    }

    private void UpdateHiveDeparture()
    {
        age++;

        Vector2 away = fly.mainBodyChunk.pos - surface;
        float distance = away.magnitude;
        bool cleared = distance >= 145f && age >= 34;
        bool timedOut = age >= 150;
        if (cleared || timedOut)
        {
            Vector2 exitDirection = distance > 1f ? away / distance : normal;
            Cancel();
            fly.AI.ChangeBehavior(FlyAI.Behavior.Idle);
            fly.AI.noSwarmCounter = Mathf.Max(fly.AI.noSwarmCounter, 90);
            fly.mainBodyChunk.vel += exitDirection * 2.5f;
            fly.AI.localGoal = fly.mainBodyChunk.pos + exitDirection * 90f;
            return;
        }

        if ((hiveDepartureGoal - fly.mainBodyChunk.pos).sqrMagnitude < 42f * 42f ||
            !fly.room.VisualContact(fly.mainBodyChunk.pos, hiveDepartureGoal))
        {
            normal = ChooseHiveOutward(fly.room, fly.mainBodyChunk.pos);
            hiveDepartureGoal = ChooseHiveDepartureGoal(fly.room, fly.mainBodyChunk.pos, normal);
        }

        fly.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        fly.AI.leaveRoomDijkstra = -1;
        fly.AI.followingDijkstraMap = -1;
        fly.AI.noSwarmCounter = Mathf.Max(fly.AI.noSwarmCounter, 90);
        fly.burrowOrHangSpot = null;
        fly.movMode = Fly.MovementMode.BatFlight;
        fly.AI.localGoal = hiveDepartureGoal;

        Vector2 desired = Custom.DirVec(fly.mainBodyChunk.pos, hiveDepartureGoal);
        if (desired == Vector2.zero) desired = normal;
        fly.dir = desired;
        fly.mainBodyChunk.vel *= 0.90f;
        fly.mainBodyChunk.vel += desired * 1.15f;
        fly.mainBodyChunk.vel += normal * 0.35f;
        float speed = fly.mainBodyChunk.vel.magnitude;
        if (speed > 8.4f)
            fly.mainBodyChunk.vel *= 8.4f / speed;
    }

    private static Vector2 ChooseHiveOutward(Room room, Vector2 origin)
    {
        if (room == null) return Vector2.up;

        Vector2 best = Vector2.up;
        float bestScore = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            float angle = 90f + (i - 7.5f) * 22.5f;
            Vector2 dir = Custom.DegToVec(angle);
            float clear = 0f;
            for (float d = 24f; d <= 170f; d += 18f)
            {
                Vector2 sample = origin + dir * d;
                if (sample.x < 18f || sample.y < 18f ||
                    sample.x > room.PixelWidth - 18f || sample.y > room.PixelHeight - 18f ||
                    room.GetTile(sample).Solid || room.PointSubmerged(sample))
                    break;
                clear = d;
            }

            float score = clear + dir.y * 24f;
            if (score <= bestScore) continue;
            bestScore = score;
            best = dir;
        }
        return best.normalized;
    }

    private static Vector2 ChooseHiveDepartureGoal(Room room, Vector2 origin, Vector2 outward)
    {
        Vector2 tangent = new(-outward.y, outward.x);
        float side = Random.value < 0.5f ? -1f : 1f;
        Vector2 goal = origin + outward * 185f + tangent * side * Random.Range(34f, 74f);
        return Custom.RestrictInRect(
            goal,
            new FloatRect(22f, 22f, room.PixelWidth - 22f, room.PixelHeight - 22f));
    }

    // No persistent candidate or nest cache: sample the current collision surfaces
    // on every request. ITerrain supplies actual geometry and its outward normal.
    internal static bool TryChoose(Room room, out Vector2 point, out Vector2 normal)
    {
        point = normal = Vector2.zero;
        if (room.terrain?.terrainList == null) return false;
        var candidates = new List<TerrainManager.ITerrain>();
        foreach (var terrain in room.terrain.terrainList)
        {
            if (terrain is QuicksandZone) continue;
            if (terrain is TerrainCurve curve && curve.segments >= 2 && curve.collisionPoints?.Length >= curve.segments)
                candidates.Add(terrain);
            else if (terrain is CurvedSlope slope && slope.segments >= 2 && slope.collisionPoints?.Length >= slope.segments)
                candidates.Add(terrain);
        }
        if (candidates.Count == 0) return false;
        var sand = SampleSand(room);
        for (int i = 0; i < DB_Tuning.CurveAttempts; i++)
        {
            var terrain = candidates[Random.Range(0, candidates.Count)];
            Vector2 sample = new(Random.Range(20f, room.PixelWidth - 20f), Random.Range(20f, room.PixelHeight - 20f));
            Vector2 snapped = terrain.SnapToTerrain(sample, 0f, out Vector2 outward);
            if (!Finite(outward.x) || !Finite(outward.y) || outward.sqrMagnitude < 0.1f ||
                !Finite(snapped.x) || !Finite(snapped.y) || snapped == sample) continue;
            outward.Normalize();
            if (!ValidPath(room, sand, snapped, outward)) continue;
            point = snapped;
            normal = outward;
            return true;
        }
        return false;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool ValidPath(Room room, List<(Vector2[] surface, Vector2[] bottom)> sand, Vector2 point, Vector2 normal)
    {
        // Include the hidden body, surface, collision radius and full escape path.
        for (float offset = -14f; offset <= 62f; offset += 4f)
        {
            Vector2 test = point + normal * offset;
            if (test.x < 12f || test.y < 12f || test.x > room.PixelWidth - 12f || test.y > room.PixelHeight - 12f) return false;
            foreach (var zone in sand)
                if (QuicksandSurface.TryGetContact(test, DB_Tuning.SandMargin, zone.surface, zone.bottom, out _)) return false;
            if (offset >= 12f && (room.GetTile(test).Solid || room.terrain.Contains(test) || room.PointSubmerged(test))) return false;
        }
        return true;
    }

    private static List<(Vector2[] surface, Vector2[] bottom)> SampleSand(Room room)
    {
        var result = new List<(Vector2[], Vector2[])>();
        // Read placed-object data too: it exists even before a zone's render/update
        // object is created and covers overlapping curves and effective edges.
        foreach (PlacedObject obj in room.roomSettings.placedObjects)
        {
            if (!obj.active || obj.data is not QuicksandZoneData data) continue;
            var surface = new Vector2[129];
            var bottom = new Vector2[129];
            QuicksandSurface.SampleZone(obj, data, surface, bottom);
            result.Add((surface, bottom));
        }
        return result;
    }
}
