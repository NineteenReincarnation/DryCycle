using System.Collections.Generic;
using DryCycle.TerrainExt.QuicksandZone;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DesertBatflyEmergence
{
    private readonly DesertBatfly fly;
    private Vector2 surface, normal;
    private int age;
    internal bool Active { get; private set; }
    internal float Progress => !Active ? 1f : Mathf.Clamp01((age - 12f) / (DesertBatflyTuning.EmergenceTicks - 12f));

    internal DesertBatflyEmergence(DesertBatfly fly) { this.fly = fly; }

    internal void Begin(Vector2 point, Vector2 outward)
    {
        surface = point;
        normal = outward.normalized;
        age = 0;
        Active = true;
        fly.mainBodyChunk.HardSetPosition(surface - normal * 12f);
        fly.mainBodyChunk.vel = Vector2.zero;
        fly.CollideWithTerrain = false;
        fly.CollideWithObjects = false;
        fly.graphicsModule?.Reset();
    }

    internal void Cancel()
    {
        if (!Active) return;
        Active = false;
        fly.CollideWithTerrain = true;
        fly.CollideWithObjects = true;
    }

    internal void Update(bool eu)
    {
        if (!Active) return;
        if (fly.dead || fly.grabbedBy.Count > 0 || fly.room == null) { Cancel(); return; }
        age++;
        fly.CollideWithTerrain = false;
        fly.CollideWithObjects = Progress > 0.5f;
        fly.mainBodyChunk.MoveFromOutsideMyUpdate(eu, surface + normal * Mathf.Lerp(-12f, 30f, Mathf.SmoothStep(0f, 1f, Progress)));
        fly.mainBodyChunk.vel = Vector2.zero;
        fly.dir = normal;
        if (age >= DesertBatflyTuning.EmergenceTicks)
        {
            Cancel();
            fly.mainBodyChunk.vel = normal * 5f;
            fly.AI.localGoal = surface + normal * 100f;
        }
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
        for (int i = 0; i < DesertBatflyTuning.CurveAttempts; i++)
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
                if (QuicksandSurface.TryGetContact(test, DesertBatflyTuning.SandMargin, zone.surface, zone.bottom, out _)) return false;
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
