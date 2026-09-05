using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.TerrainExt.QuicksandZone;
using RWCustom;
using UnityEngine;

namespace DryCycle.WatcherExts.PeachLizard;

/// <summary>
/// Builds a tiny per-room lookup for the *ordinary sand* portions authored inside
/// QuicksandZone objects. Real quicksand is deliberately never exposed here.
///
/// QuicksandZone already participates in TerrainManager as BurrowAllowed terrain on
/// its non-quicksand material sections, so the normal AI bake turns those cells into
/// AItile.Accessibility.Sand. This class does not create a parallel path graph; it
/// only identifies which native Sand cells belong to our safe material and provides
/// a few room-wide lurk candidates for Peach Lizard's existing AI.
/// </summary>
internal static class PeachLizardQuicksandSandMap
{
    // A Peach Lizard is not a point. Reject a tile when a quicksand boundary lies
    // immediately beside it, even if the tile centre itself is ordinary sand.
    private const float MaterialSafetyPadding = 8f;
    private const int MaterialSamplesPerTile = 7;
    private const float PreferredLurkDepth = 0.42f;

    private sealed class RoomCache
    {
        internal bool Built;
        internal int Width;
        internal int Height;
        internal bool[,] SafeSand;
        internal float[,] NormalizedDepth;
        internal readonly List<IntVector2> LurkCandidates = new();
    }

    private static ConditionalWeakTable<Room, RoomCache> _roomCaches = new();

    internal static void Reset()
    {
        _roomCaches = new ConditionalWeakTable<Room, RoomCache>();
    }

    internal static void Prepare(Room room)
    {
        if (room == null) return;
        RoomCache cache = _roomCaches.GetValue(room, _ => new RoomCache());
        BuildIfNeeded(room, cache);
    }

    internal static bool TryGetSafeSand(Room room, WorldCoordinate coordinate, out float depth)
    {
        depth = 0f;
        if (room == null || !coordinate.TileDefined ||
            coordinate.room != room.abstractRoom.index)
            return false;

        return TryGetSafeSand(room, coordinate.Tile, out depth);
    }

    internal static bool TryGetSafeSand(Room room, IntVector2 tile, out float depth)
    {
        depth = 0f;
        if (room == null || tile.x < 0 || tile.y < 0 ||
            tile.x >= room.TileWidth || tile.y >= room.TileHeight)
            return false;

        RoomCache cache = _roomCaches.GetValue(room, _ => new RoomCache());
        BuildIfNeeded(room, cache);
        if (!cache.Built || cache.SafeSand == null || !cache.SafeSand[tile.x, tile.y])
            return false;

        depth = cache.NormalizedDepth[tile.x, tile.y];
        return true;
    }

    internal static int CandidateCount(Room room)
    {
        if (room == null) return 0;
        RoomCache cache = _roomCaches.GetValue(room, _ => new RoomCache());
        BuildIfNeeded(room, cache);
        return cache.Built ? cache.LurkCandidates.Count : 0;
    }

    internal static bool TryGetCandidate(Room room, int seed, out WorldCoordinate coordinate)
    {
        coordinate = default;
        if (room == null) return false;

        RoomCache cache = _roomCaches.GetValue(room, _ => new RoomCache());
        BuildIfNeeded(room, cache);
        if (!cache.Built || cache.LurkCandidates.Count == 0) return false;

        int index = PositiveHash(seed) % cache.LurkCandidates.Count;
        coordinate = room.GetWorldCoordinate(cache.LurkCandidates[index]);
        return true;
    }

    private static void BuildIfNeeded(Room room, RoomCache cache)
    {
        if (cache.Built && cache.Width == room.TileWidth && cache.Height == room.TileHeight)
            return;

        // Do not lock in an empty cache while the room is still baking its AI map.
        if (room.aimap == null || room.terrain?.terrainList == null)
            return;

        cache.Width = room.TileWidth;
        cache.Height = room.TileHeight;
        cache.SafeSand = new bool[cache.Width, cache.Height];
        cache.NormalizedDepth = new float[cache.Width, cache.Height];
        cache.LurkCandidates.Clear();

        List<QuicksandZone> zones = new();
        for (int i = 0; i < room.terrain.terrainList.Count; i++)
        {
            if (room.terrain.terrainList[i] is QuicksandZone zone && Usable(zone))
                zones.Add(zone);
        }

        float[] bestCandidateDistance = new float[cache.Width];
        int[] bestCandidateY = new int[cache.Width];
        for (int x = 0; x < cache.Width; x++)
        {
            bestCandidateDistance[x] = float.PositiveInfinity;
            bestCandidateY[x] = -1;
        }

        for (int zoneIndex = 0; zoneIndex < zones.Count; zoneIndex++)
        {
            QuicksandZone zone = zones[zoneIndex];
            float authoredA = zone.PlacedObject.pos.x + zone.Data.SurfaceSpline.posA.x;
            float authoredB = zone.PlacedObject.pos.x + zone.Data.SurfaceSpline.posB.x;
            float authoredMinX = Mathf.Min(authoredA, authoredB);
            float authoredMaxX = Mathf.Max(authoredA, authoredB);
            float bottomY = zone.PlacedObject.pos.y - zone.Data.BottomDepth;

            int minX = Mathf.Clamp(Mathf.FloorToInt(authoredMinX / 20f), 0, cache.Width - 1);
            int maxX = Mathf.Clamp(Mathf.FloorToInt(authoredMaxX / 20f), 0, cache.Width - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(bottomY / 20f), 0, cache.Height - 1);

            TerrainManager.ITerrain terrain = zone;
            for (int x = minX; x <= maxX; x++)
            {
                if (!ColumnMaterialIsSafelyOrdinarySand(zone, x, authoredMinX, authoredMaxX))
                    continue;

                float centerX = x * 20f + 10f;
                float u = zone.MaterialUAtWorldX(centerX);
                if (zone.Data.IsQuicksand(u) ||
                    !zone.TrySampleSurfaceFrame(
                        u,
                        out Vector2 surface,
                        out _,
                        out Vector2 inward,
                        out float depthLength))
                    continue;

                int maxY = Mathf.Clamp(Mathf.CeilToInt(surface.y / 20f), 0, cache.Height - 1);
                for (int y = minY; y <= maxY; y++)
                {
                    // This guarantees the tile is actually part of this particular
                    // safe QuicksandZone section, not merely another terrain object.
                    if (!terrain.ObstructsTile(x, y)) continue;

                    // Overlapping placed objects are allowed. A tile that is ordinary
                    // sand in this zone is still unsafe if any other zone puts true
                    // quicksand through the same physical tile volume.
                    if (OverlapsAnyActualQuicksand(zones, x, y)) continue;

                    AItile aiTile = room.aimap.getAItile(x, y);
                    if (aiTile == null || aiTile.acc != AItile.Accessibility.Sand)
                        continue;

                    Vector2 tileCenter = room.MiddleOfTile(x, y);
                    float normalizedDepth = Mathf.Clamp01(
                        Vector2.Dot(tileCenter - surface, inward) /
                        Mathf.Max(4f, depthLength));

                    cache.SafeSand[x, y] = true;
                    cache.NormalizedDepth[x, y] = normalizedDepth;

                    // Keep one useful, moderately buried destination per X column.
                    // This is enough to let the native LurkTracker discover every
                    // safe sand region without storing every tile as a candidate.
                    float candidateDistance = Mathf.Abs(normalizedDepth - PreferredLurkDepth);
                    if (!aiTile.narrowSpace && candidateDistance < bestCandidateDistance[x])
                    {
                        bestCandidateDistance[x] = candidateDistance;
                        bestCandidateY[x] = y;
                    }
                }
            }
        }

        for (int x = 0; x < cache.Width; x++)
        {
            if (bestCandidateY[x] >= 0)
                cache.LurkCandidates.Add(new IntVector2(x, bestCandidateY[x]));
        }

        cache.Built = true;
    }

    private static bool ColumnMaterialIsSafelyOrdinarySand(
        QuicksandZone zone,
        int tileX,
        float authoredMinX,
        float authoredMaxX)
    {
        float tileLeft = tileX * 20f;
        float tileRight = tileLeft + 20f;
        float tileCenter = (tileLeft + tileRight) * 0.5f;

        // Never use TerrainCurve's room-edge mesh seal extension as burrow habitat.
        if (tileCenter < authoredMinX || tileCenter > authoredMaxX)
            return false;

        float sampleLeft = tileLeft - MaterialSafetyPadding;
        float sampleRight = tileRight + MaterialSafetyPadding;
        bool sampled = false;

        for (int i = 0; i < MaterialSamplesPerTile; i++)
        {
            float t = MaterialSamplesPerTile <= 1
                ? 0.5f
                : i / (float)(MaterialSamplesPerTile - 1);
            float worldX = Mathf.Lerp(sampleLeft, sampleRight, t);
            if (worldX < authoredMinX || worldX > authoredMaxX)
                continue;

            sampled = true;
            if (zone.Data.IsQuicksand(zone.MaterialUAtWorldX(worldX)))
                return false;
        }

        return sampled;
    }

    private static bool OverlapsAnyActualQuicksand(
        List<QuicksandZone> zones,
        int tileX,
        int tileY)
    {
        float tileLeft = tileX * 20f - MaterialSafetyPadding;
        float tileRight = (tileX + 1) * 20f + MaterialSafetyPadding;
        float tileBottom = tileY * 20f - MaterialSafetyPadding;
        float tileTop = (tileY + 1) * 20f + MaterialSafetyPadding;

        for (int zoneIndex = 0; zoneIndex < zones.Count; zoneIndex++)
        {
            QuicksandZone zone = zones[zoneIndex];
            float authoredA = zone.PlacedObject.pos.x + zone.Data.SurfaceSpline.posA.x;
            float authoredB = zone.PlacedObject.pos.x + zone.Data.SurfaceSpline.posB.x;
            float authoredMinX = Mathf.Min(authoredA, authoredB);
            float authoredMaxX = Mathf.Max(authoredA, authoredB);
            if (tileRight < authoredMinX || tileLeft > authoredMaxX)
                continue;

            float zoneBottom = zone.PlacedObject.pos.y - zone.Data.BottomDepth;
            if (tileTop < zoneBottom)
                continue;

            for (int i = 0; i < MaterialSamplesPerTile; i++)
            {
                float t = MaterialSamplesPerTile <= 1
                    ? 0.5f
                    : i / (float)(MaterialSamplesPerTile - 1);
                float worldX = Mathf.Lerp(tileLeft, tileRight, t);
                if (worldX < authoredMinX || worldX > authoredMaxX)
                    continue;

                float u = zone.MaterialUAtWorldX(worldX);
                if (!zone.Data.IsQuicksand(u) ||
                    !zone.TrySampleSurfaceFrame(
                        u,
                        out Vector2 surface,
                        out _,
                        out _,
                        out _))
                    continue;

                if (tileBottom <= surface.y && tileTop >= zoneBottom)
                    return true;
            }
        }

        return false;
    }

    private static bool Usable(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data?.SurfaceSpline != null;
    }

    private static int PositiveHash(int value)
    {
        unchecked
        {
            uint x = (uint)value;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (int)(x & 0x7FFFFFFF);
        }
    }
}
