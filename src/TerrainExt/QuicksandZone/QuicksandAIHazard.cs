using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Adds curve-aware quicksand avoidance to creature pathing and, when available,
/// feeds the same hazard into the creature's native ThreatTracker.
/// </summary>
internal static class QuicksandAIHazard
{
    private const float NearHeight = 40f;
    private const float SideMargin = 20f;
    private const float EnterDanger = 0.70f;
    private const float DangerEpsilon = 0.04f;
    private const float SampleSpacing = 10f;
    private const int MaxSamples = 24;

    private sealed class RoomCache
    {
        internal readonly List<QuicksandZone> Zones = new();
        internal int Countdown;
    }

    private sealed class FearState
    {
        internal ArtificialIntelligence AI;
        internal ThreatTracker Tracker;
        internal Room Room;
        internal readonly ThreatTracker.ThreatPoint[] Points = new ThreatTracker.ThreatPoint[3];
    }

    private static readonly ConditionalWeakTable<Room, RoomCache> RoomCaches = new();
    private static readonly ConditionalWeakTable<ArtificialIntelligence, FearState> FearStates = new();
    private static readonly List<FearState> LiveFearStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        On.PathFinder.CheckConnectionCost += PathFinder_CheckConnectionCost;
        On.ArtificialIntelligence.Update += ArtificialIntelligence_Update;
    }

    internal static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        On.PathFinder.CheckConnectionCost -= PathFinder_CheckConnectionCost;
        On.ArtificialIntelligence.Update -= ArtificialIntelligence_Update;

        for (int i = LiveFearStates.Count - 1; i >= 0; i--)
        {
            RemoveFearPoints(LiveFearStates[i]);
        }
        LiveFearStates.Clear();
    }

    private static PathCost PathFinder_CheckConnectionCost(
        On.PathFinder.orig_CheckConnectionCost orig,
        PathFinder self,
        PathFinder.PathingCell start,
        PathFinder.PathingCell goal,
        MovementConnection connection,
        bool followingPath)
    {
        PathCost cost = orig(self, start, goal, connection, followingPath);
        Room room = self?.realizedRoom;

        if (room == null ||
            self.creature?.realizedCreature == null ||
            cost.legality > PathCost.Legality.Unwanted ||
            !connection.startCoord.TileDefined ||
            !connection.destinationCoord.TileDefined ||
            connection.startCoord.room != room.abstractRoom.index ||
            connection.destinationCoord.room != room.abstractRoom.index)
        {
            return cost;
        }

        List<QuicksandZone> zones = Zones(room);
        if (zones.Count == 0) return cost;

        float clearance = BodyClearance(self.creature.realizedCreature);
        Vector2 a = room.MiddleOfTile(connection.StartTile);
        Vector2 b = room.MiddleOfTile(connection.DestTile);
        float startDanger = Danger(zones, a, clearance);
        float endDanger = Danger(zones, b, clearance);
        float segmentDanger = SegmentDanger(zones, a, b, clearance);

        // A creature already in quicksand must always be allowed to choose a route
        // that reduces danger. This prevents the aversion itself from trapping it.
        if (startDanger >= EnterDanger &&
            endDanger < startDanger - DangerEpsilon &&
            segmentDanger <= startDanger + DangerEpsilon)
        {
            return cost;
        }

        float deepest = Mathf.Max(endDanger, segmentDanger);
        if (deepest >= EnterDanger)
        {
            float worsening = Mathf.Max(0f, endDanger - startDanger);
            return cost + new PathCost(
                deepest * 90f + worsening * 60f,
                PathCost.Legality.Unwanted);
        }

        cost.resistance += deepest * 35f;
        return cost;
    }

    private static void ArtificialIntelligence_Update(
        On.ArtificialIntelligence.orig_Update orig,
        ArtificialIntelligence self)
    {
        UpdateFear(self);
        orig(self);
    }

    private static void UpdateFear(ArtificialIntelligence ai)
    {
        if (ai?.threatTracker == null || ai.creature?.realizedCreature?.room == null)
        {
            if (ai != null && FearStates.TryGetValue(ai, out FearState stale))
            {
                RemoveFearPoints(stale);
            }
            return;
        }

        Room room = ai.creature.realizedCreature.room;
        FearState state = FearStates.GetValue(ai, key =>
        {
            FearState created = new FearState { AI = key };
            LiveFearStates.Add(created);
            return created;
        });

        if (state.Tracker != ai.threatTracker || state.Room != room)
        {
            RemoveFearPoints(state);
            state.Tracker = ai.threatTracker;
            state.Room = room;
        }

        EnsureFearPoints(state);
        List<QuicksandZone> zones = Zones(room);
        Vector2 creaturePos = CreaturePoint(ai.creature.realizedCreature, room);

        if (!NearestSurface(zones, creaturePos, out QuicksandZone zone, out Vector2 surface, out float distance))
        {
            SetSeverity(state, 0f);
            return;
        }

        float danger = PointDanger(zone, creaturePos, BodyClearance(ai.creature.realizedCreature));
        float proximity = 1f - Mathf.Clamp01(distance / 130f);
        if (proximity <= 0f && danger < EnterDanger)
        {
            SetSeverity(state, 0f);
            return;
        }

        float severity = danger >= EnterDanger
            ? 0.46f
            : Mathf.Lerp(0.10f, 0.32f, proximity);

        for (int i = 0; i < state.Points.Length; i++)
        {
            float x = Mathf.Clamp(surface.x + (i - 1) * 60f, zone.startX, zone.endX);
            if (!SampleSurface(zone, x, out Vector2 point))
            {
                state.Points[i].severity = 0f;
                continue;
            }

            point.y += 6f;
            state.Points[i].pos = room.GetWorldCoordinate(point);
            state.Points[i].severity = severity * (i == 1 ? 1f : 0.72f);
        }
    }

    private static void EnsureFearPoints(FearState state)
    {
        if (state?.Tracker == null || state.AI?.creature == null) return;
        for (int i = 0; i < state.Points.Length; i++)
        {
            if (state.Points[i] == null)
            {
                state.Points[i] = state.Tracker.AddThreatPoint(null, state.AI.creature.pos, 0f);
            }
        }
    }

    private static void RemoveFearPoints(FearState state)
    {
        if (state == null) return;
        for (int i = 0; i < state.Points.Length; i++)
        {
            if (state.Tracker != null && state.Points[i] != null)
            {
                state.Tracker.RemoveThreatPoint(state.Points[i]);
            }
            state.Points[i] = null;
        }
        state.Tracker = null;
        state.Room = null;
    }

    private static void SetSeverity(FearState state, float severity)
    {
        for (int i = 0; i < state.Points.Length; i++)
        {
            if (state.Points[i] != null) state.Points[i].severity = severity;
        }
    }

    private static List<QuicksandZone> Zones(Room room)
    {
        RoomCache cache = RoomCaches.GetValue(room, _ => new RoomCache());
        if (cache.Countdown-- > 0) return cache.Zones;

        cache.Countdown = 512;
        cache.Zones.Clear();
        if (room?.updateList == null) return cache.Zones;

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is QuicksandZone zone && Usable(zone))
            {
                cache.Zones.Add(zone);
            }
        }
        return cache.Zones;
    }

    private static float SegmentDanger(
        List<QuicksandZone> zones,
        Vector2 a,
        Vector2 b,
        float clearance)
    {
        int samples = Mathf.Clamp(
            Mathf.CeilToInt(Vector2.Distance(a, b) / SampleSpacing),
            1,
            MaxSamples);
        float result = 0f;
        for (int i = 0; i <= samples; i++)
        {
            result = Mathf.Max(result, Danger(zones, Vector2.Lerp(a, b, (float)i / samples), clearance));
        }
        return result;
    }

    private static float Danger(List<QuicksandZone> zones, Vector2 point, float clearance)
    {
        float result = 0f;
        for (int i = 0; i < zones.Count; i++)
        {
            result = Mathf.Max(result, PointDanger(zones[i], point, clearance));
        }
        return result;
    }

    private static float PointDanger(QuicksandZone zone, Vector2 point, float clearance)
    {
        if (!Usable(zone)) return 0f;

        float x = point.x;
        float sideGap = 0f;
        if (x < zone.startX) { sideGap = zone.startX - x; x = zone.startX; }
        else if (x > zone.endX) { sideGap = x - zone.endX; x = zone.endX; }

        float sideReach = SideMargin + clearance;
        if (sideGap > sideReach || !SampleSurface(zone, x, out Vector2 surface)) return 0f;

        float sideFactor = 1f - Mathf.Clamp01(sideGap / sideReach);
        float bottomY = zone.PlacedObject.pos.y - zone.Data.BottomDepth;
        if (point.y < bottomY - clearance || point.y > surface.y + NearHeight + clearance) return 0f;

        float gap = point.y - surface.y;
        if (gap > 0f)
        {
            float near = 1f - Mathf.Clamp01(Mathf.Max(0f, gap - clearance) / NearHeight);
            return near * 0.55f * sideFactor;
        }

        float depthLength = Mathf.Max(4f, surface.y - bottomY);
        float depthT = Mathf.Clamp01((-gap + clearance) / Mathf.Max(20f, depthLength * 0.35f));
        return Mathf.Lerp(0.78f, 1f, depthT) * sideFactor;
    }

    private static bool NearestSurface(
        List<QuicksandZone> zones,
        Vector2 point,
        out QuicksandZone nearest,
        out Vector2 surface,
        out float distance)
    {
        nearest = null;
        surface = Vector2.zero;
        distance = float.PositiveInfinity;

        for (int i = 0; i < zones.Count; i++)
        {
            QuicksandZone zone = zones[i];
            float x = Mathf.Clamp(point.x, zone.startX, zone.endX);
            if (!SampleSurface(zone, x, out Vector2 candidate)) continue;

            float d = Vector2.Distance(point, candidate);
            if (d < distance)
            {
                nearest = zone;
                surface = candidate;
                distance = d;
            }
        }
        return nearest != null;
    }

    private static bool SampleSurface(QuicksandZone zone, float x, out Vector2 surface)
    {
        surface = Vector2.zero;
        if (!Usable(zone) || x < zone.startX || x > zone.endX) return false;

        float u = zone.MaterialUAtWorldX(x);
        return zone.Data.IsQuicksand(u) &&
               zone.TrySampleSurfaceFrame(u, out surface, out _, out _, out _);
    }

    private static Vector2 CreaturePoint(Creature creature, Room room)
    {
        if (creature?.bodyChunks != null && creature.bodyChunks.Length > 0)
        {
            Vector2 total = Vector2.zero;
            int count = 0;
            for (int i = 0; i < creature.bodyChunks.Length; i++)
            {
                if (creature.bodyChunks[i] == null) continue;
                total += creature.bodyChunks[i].pos;
                count++;
            }
            if (count > 0) return total / count;
        }

        return creature?.abstractCreature != null
            ? room.MiddleOfTile(creature.abstractCreature.pos.Tile)
            : Vector2.zero;
    }

    private static float BodyClearance(Creature creature)
    {
        float result = 8f;
        if (creature?.bodyChunks == null) return result;
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            if (creature.bodyChunks[i] != null) result = Mathf.Max(result, creature.bodyChunks[i].rad);
        }
        return result;
    }

    private static bool Usable(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data != null;
    }
}
