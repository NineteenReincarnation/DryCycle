using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal static class DesertBatflyEnvironmentalExposure
{
    private const int RoofProbeTiles = 8;
    private const int SideProbeTiles = 6;

    internal static DesertBatflyEnvironmentalExposureSample Sample(
        Room room,
        IntVector2 tile,
        float visibilityConfidence)
    {
        if (room == null || !InBounds(room, tile) || room.GetTile(tile).Solid)
            return new DesertBatflyEnvironmentalExposureSample(
                1f, 0f, 0f, 0f, 0f, 0f, visibilityConfidence);

        float roof = ProbeRoof(room, tile);
        float left = ProbeSide(room, tile, -1);
        float right = ProbeSide(room, tile, 1);
        float side = Mathf.Clamp01((left + right) * 0.5f);
        float enclosure = Mathf.Clamp01(roof * 0.48f + side * 0.52f);
        float exposure = Mathf.Clamp01(1f - (roof * 0.46f + side * 0.24f + enclosure * 0.30f));
        float rainExposure = Mathf.Clamp01(1f - roof);

        return new DesertBatflyEnvironmentalExposureSample(
            exposure,
            roof,
            side,
            enclosure,
            roof,
            rainExposure,
            visibilityConfidence);
    }

    internal static bool RoostCompatibilityHint(Room room, IntVector2 tile)
    {
        if (room == null || tile.x <= 0 || tile.x >= room.TileWidth - 1 ||
            tile.y < 4 || tile.y >= room.TileHeight - 1)
            return false;

        Room.Tile current = room.GetTile(tile);
        if (current.Terrain != Room.Tile.TerrainType.Air) return false;

        Room.Tile above = room.GetTile(tile + new IntVector2(0, 1));
        if (above.Terrain == Room.Tile.TerrainType.Solid ||
            above.Terrain == Room.Tile.TerrainType.Floor ||
            current.horizontalBeam)
            return HasDownwardClearance(room, tile);

        return above.verticalBeam && !current.verticalBeam && HasDownwardClearance(room, tile);
    }

    internal static bool NearHive(Room room, IntVector2 tile, int radiusTiles = 8)
    {
        if (room?.hives == null || room.hives.Length == 0) return false;
        int radiusSq = radiusTiles * radiusTiles;
        for (int i = 0; i < room.hives.Length; i++)
        {
            IntVector2[] hive = room.hives[i];
            if (hive == null) continue;
            for (int n = 0; n < hive.Length; n++)
            {
                int dx = hive[n].x - tile.x;
                int dy = hive[n].y - tile.y;
                if (dx * dx + dy * dy <= radiusSq) return true;
            }
        }
        return false;
    }

    private static float ProbeRoof(Room room, IntVector2 origin)
    {
        for (int i = 1; i <= RoofProbeTiles; i++)
        {
            IntVector2 tile = origin + new IntVector2(0, i);
            if (!InBounds(room, tile)) return 0f;
            Room.Tile test = room.GetTile(tile);
            if (test.Terrain == Room.Tile.TerrainType.Solid)
                return Mathf.Lerp(1f, 0.35f, (i - 1f) / Mathf.Max(1f, RoofProbeTiles - 1f));
        }
        return 0f;
    }

    private static float ProbeSide(Room room, IntVector2 origin, int direction)
    {
        for (int i = 1; i <= SideProbeTiles; i++)
        {
            IntVector2 tile = origin + new IntVector2(direction * i, 0);
            if (!InBounds(room, tile)) return 0f;
            if (room.GetTile(tile).Terrain == Room.Tile.TerrainType.Solid)
                return Mathf.Lerp(1f, 0.30f, (i - 1f) / Mathf.Max(1f, SideProbeTiles - 1f));
        }
        return 0f;
    }

    private static bool HasDownwardClearance(Room room, IntVector2 tile)
    {
        for (int i = 0; i < 5; i++)
        {
            IntVector2 below = tile + new IntVector2(0, -i);
            if (!InBounds(room, below)) return false;
            Room.Tile test = room.GetTile(below);
            if (test.Terrain == Room.Tile.TerrainType.Solid || test.AnyWater)
                return false;
        }
        return true;
    }

    private static bool InBounds(Room room, IntVector2 tile)
        => tile.x >= 0 && tile.x < room.TileWidth && tile.y >= 0 && tile.y < room.TileHeight;
}
