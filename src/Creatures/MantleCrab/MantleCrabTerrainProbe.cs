using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>Three narrow columns; the visible foot has no large terrain collision circle.</summary>
internal static class MantleCrabTerrainProbe
{
    internal static bool StillSupported(Room room, Vector2 point) =>
        Surface(room, point.x, Mathf.FloorToInt((point.y - 2f) / 20f), out float height, out _) &&
        Mathf.Abs(point.y - height - 1f) < 3f;

    internal static bool Find(Room room, Vector2 anchor, Vector2 desired, float reach,
        out Vector2 point, out Vector2 normal)
    {
        point = desired; normal = Vector2.up;
        float best = float.MaxValue;
        for (int column = -1; column <= 1; column++)
        {
            float x = desired.x + column * 9f;
            if (x < 1f || x >= room.PixelWidth - 1f) continue;
            for (int y = Mathf.Min(room.TileHeight - 1, Mathf.FloorToInt(anchor.y / 20f));
                y >= Mathf.Max(0, Mathf.FloorToInt((anchor.y - reach) / 20f)); y--)
            {
                if (!Surface(room, x, y, out float height, out Vector2 candidateNormal)) continue;
                Vector2 candidate = new(x, height + 1f);
                if (candidate.y >= anchor.y - 12f || Vector2.Distance(anchor, candidate) > reach) continue;
                float score = (candidate - desired).sqrMagnitude;
                if (score < best) { best = score; point = candidate; normal = candidateNormal; }
                // Never find a floor through a higher solid surface.
                break;
            }
        }
        return best < float.MaxValue;
    }

    private static bool Surface(Room room, float x, int y, out float height, out Vector2 normal)
    {
        int tileX = Mathf.FloorToInt(x / 20f);
        height = (y + 1) * 20f; normal = Vector2.up;
        if (y < 0 || y >= room.TileHeight || tileX < 0 || tileX >= room.TileWidth) return false;
        Room.Tile tile = room.GetTile(tileX, y);
        if (room.GetTile(tileX, y + 1).Solid) return false;
        if (tile.Solid || tile.Terrain == Room.Tile.TerrainType.Floor) return true;
        if (tile.Terrain != Room.Tile.TerrainType.Slope) return false;
        Room.SlopeDirection slope = room.IdentifySlope(tileX, y);
        float localX = x - tileX * 20f;
        if (slope == Room.SlopeDirection.UpLeft)
        { height = y * 20f + localX; normal = new Vector2(-1, 1).normalized; return true; }
        if (slope == Room.SlopeDirection.UpRight)
        { height = y * 20f + 20f - localX; normal = new Vector2(1, 1).normalized; return true; }
        return false;
    }
}
