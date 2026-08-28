using System;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.DewPod;

internal readonly struct DewPodPlantAttachment
{
    internal DewPodPlantAttachment(Vector2 position, Vector2 normal)
    {
        Position = position;
        Normal = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector2.up;
    }

    internal Vector2 Position { get; }
    internal Vector2 Normal { get; }
}

/// <summary>
/// Resolves a placed Dew Pod mother plant onto the nearest exposed terrain surface.
/// Standard tile faces and slopes are handled explicitly, while Watcher terrain is
/// sampled through TerrainManager.ITerrain.SnapToTerrain so curved surfaces supply
/// their real collision normal instead of being approximated as a horizontal floor.
/// </summary>
internal static class DewPodPlantAttachmentResolver
{
    private const float NearbySurfaceRadius = 72f;
    private const float LegacyFloorFallbackDistance = 120f;
    private const float TileHalfSize = 10f;
    private const float SlopeHalfLength = 14.142136f;

    private static readonly IntVector2[] FaceDirections =
    {
        new(-1, 0),
        new(1, 0),
        new(0, -1),
        new(0, 1)
    };

    private static readonly Vector2[] FaceNormals =
    {
        Vector2.left,
        Vector2.right,
        Vector2.down,
        Vector2.up
    };

    private struct SurfaceCandidate
    {
        internal bool Valid;
        internal Vector2 Position;
        internal Vector2 Normal;
        internal float Score;
    }

    internal static DewPodPlantAttachment Resolve(Room room, Vector2 placedPos)
    {
        if (room == null)
        {
            return new DewPodPlantAttachment(placedPos, Vector2.up);
        }

        SurfaceCandidate best = new()
        {
            Valid = false,
            Score = float.MaxValue
        };

        ConsiderWatcherTerrain(room, placedPos, ref best);
        ConsiderTileTerrain(room, placedPos, ref best);

        if (best.Valid)
        {
            return new DewPodPlantAttachment(best.Position, best.Normal);
        }

        // Preserve the old editor behavior as a last resort: if a placed point is
        // floating above ordinary terrain, search downward for a floor before
        // leaving the plant at the raw placed-object coordinate.
        IntVector2 tile = room.GetTilePosition(placedPos);
        int maxTiles = Mathf.CeilToInt(LegacyFloorFallbackDistance / 20f);
        int minY = Math.Max(0, tile.y - maxTiles);
        for (int y = tile.y; y >= minY; y--)
        {
            if (!IsInsideRoom(room, tile.x, y) || !room.GetTile(tile.x, y).Solid)
            {
                continue;
            }

            Vector2 center = room.MiddleOfTile(tile.x, y);
            return new DewPodPlantAttachment(
                new Vector2(
                    Mathf.Clamp(placedPos.x, center.x - TileHalfSize, center.x + TileHalfSize),
                    center.y + TileHalfSize),
                Vector2.up);
        }

        return new DewPodPlantAttachment(placedPos, Vector2.up);
    }

    private static void ConsiderWatcherTerrain(
        Room room,
        Vector2 placedPos,
        ref SurfaceCandidate best)
    {
        if (room.terrain?.terrainList == null)
        {
            return;
        }

        for (int i = 0; i < room.terrain.terrainList.Count; i++)
        {
            TerrainManager.ITerrain terrain = room.terrain.terrainList[i];
            if (terrain == null)
            {
                continue;
            }

            Vector2 snapped = terrain.SnapToTerrain(
                placedPos,
                0f,
                out Vector2 normal,
                placedPos);

            if (normal.sqrMagnitude <= 0.0001f || !IsFinite(snapped) || !IsFinite(normal))
            {
                continue;
            }

            // Watcher terrain already knows which side of thick local terrain the
            // placed point is on. Keep that supplied normal; it is what allows a
            // CurvedSlope/SuperSlope underside to orient correctly as well.
            ConsiderCandidate(
                placedPos,
                snapped,
                normal.normalized,
                curvedTerrain: true,
                ref best);
        }
    }

    private static void ConsiderTileTerrain(
        Room room,
        Vector2 placedPos,
        ref SurfaceCandidate best)
    {
        IntVector2 centerTile = room.GetTilePosition(placedPos);
        int tileRadius = Mathf.CeilToInt(NearbySurfaceRadius / 20f) + 1;

        int minX = Math.Max(0, centerTile.x - tileRadius);
        int maxX = Math.Min(room.TileWidth - 1, centerTile.x + tileRadius);
        int minY = Math.Max(0, centerTile.y - tileRadius);
        int maxY = Math.Min(room.TileHeight - 1, centerTile.y + tileRadius);

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                Room.Tile tile = room.GetTile(x, y);

                if (tile.Terrain == Room.Tile.TerrainType.Slope)
                {
                    ConsiderSlope(room, placedPos, x, y, ref best);
                    continue;
                }

                if (tile.Solid)
                {
                    ConsiderSolidTileFaces(room, placedPos, x, y, ref best);
                    continue;
                }

                if (tile.Terrain == Room.Tile.TerrainType.Floor)
                {
                    Vector2 center = room.MiddleOfTile(x, y);
                    Vector2 point = new(
                        Mathf.Clamp(placedPos.x, center.x - TileHalfSize, center.x + TileHalfSize),
                        center.y + TileHalfSize);
                    ConsiderCandidate(
                        placedPos,
                        point,
                        Vector2.up,
                        curvedTerrain: false,
                        ref best);
                }
            }
        }
    }

    private static void ConsiderSolidTileFaces(
        Room room,
        Vector2 placedPos,
        int x,
        int y,
        ref SurfaceCandidate best)
    {
        Vector2 center = room.MiddleOfTile(x, y);

        for (int i = 0; i < FaceDirections.Length; i++)
        {
            IntVector2 direction = FaceDirections[i];
            int neighborX = x + direction.x;
            int neighborY = y + direction.y;

            // Only exposed faces are valid roots. Treat Watcher/local terrain in
            // the adjacent tile as blocking as well so roots do not appear inside
            // a curved solid that overlaps an ordinary tile boundary.
            if (IsBlockingTile(room, neighborX, neighborY))
            {
                continue;
            }

            Vector2 normal = FaceNormals[i];
            Vector2 tangent = new(normal.y, -normal.x);
            float along = Mathf.Clamp(
                Vector2.Dot(placedPos - center, tangent),
                -TileHalfSize,
                TileHalfSize);
            Vector2 point = center + normal * TileHalfSize + tangent * along;

            ConsiderCandidate(
                placedPos,
                point,
                normal,
                curvedTerrain: false,
                ref best);
        }
    }

    private static void ConsiderSlope(
        Room room,
        Vector2 placedPos,
        int x,
        int y,
        ref SurfaceCandidate best)
    {
        Room.SlopeDirection slope = room.IdentifySlope(x, y);
        if (slope == Room.SlopeDirection.Broken)
        {
            return;
        }

        Vector2 tangent;
        Vector2 normal;

        if (slope == Room.SlopeDirection.UpLeft)
        {
            tangent = new Vector2(1f, 1f).normalized;
            normal = new Vector2(-1f, 1f).normalized;
        }
        else if (slope == Room.SlopeDirection.UpRight)
        {
            tangent = new Vector2(1f, -1f).normalized;
            normal = new Vector2(1f, 1f).normalized;
        }
        else if (slope == Room.SlopeDirection.DownLeft)
        {
            tangent = new Vector2(1f, -1f).normalized;
            normal = new Vector2(-1f, -1f).normalized;
        }
        else
        {
            tangent = new Vector2(1f, 1f).normalized;
            normal = new Vector2(1f, -1f).normalized;
        }

        Vector2 center = room.MiddleOfTile(x, y);
        float along = Mathf.Clamp(
            Vector2.Dot(placedPos - center, tangent),
            -SlopeHalfLength,
            SlopeHalfLength);
        Vector2 point = center + tangent * along;

        ConsiderCandidate(
            placedPos,
            point,
            normal,
            curvedTerrain: false,
            ref best);
    }

    private static void ConsiderCandidate(
        Vector2 placedPos,
        Vector2 surfacePos,
        Vector2 normal,
        bool curvedTerrain,
        ref SurfaceCandidate best)
    {
        if (!IsFinite(surfacePos) || !IsFinite(normal) || normal.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        normal.Normalize();
        float distance = Vector2.Distance(placedPos, surfacePos);
        if (distance > NearbySurfaceRadius)
        {
            return;
        }

        // Curved terrain receives a tiny tie-break preference because its actual
        // collision surface is more precise than the coarse tile silhouette when
        // both happen to occupy nearly the same place.
        float score = distance - (curvedTerrain ? 0.2f : 0f);

        // At exact corners several exposed faces can be equidistant. A very small
        // upward bias preserves the familiar floor placement without preventing a
        // side wall from winning whenever it is actually closer to the marker.
        score += (1f - Mathf.Clamp01(normal.y)) * 0.015f;

        if (best.Valid && score >= best.Score)
        {
            return;
        }

        best.Valid = true;
        best.Position = surfacePos;
        best.Normal = normal;
        best.Score = score;
    }

    private static bool IsBlockingTile(Room room, int x, int y)
    {
        if (!IsInsideRoom(room, x, y))
        {
            return true;
        }

        Room.Tile tile = room.GetTile(x, y);
        if (tile.Solid || tile.Terrain == Room.Tile.TerrainType.Slope)
        {
            return true;
        }

        return room.terrain != null && room.terrain.ObstructsTile(x, y);
    }

    private static bool IsInsideRoom(Room room, int x, int y)
    {
        return room != null &&
               x >= 0 &&
               y >= 0 &&
               x < room.TileWidth &&
               y < room.TileHeight;
    }

    private static bool IsFinite(Vector2 value)
    {
        return !float.IsNaN(value.x) &&
               !float.IsNaN(value.y) &&
               !float.IsInfinity(value.x) &&
               !float.IsInfinity(value.y);
    }
}
