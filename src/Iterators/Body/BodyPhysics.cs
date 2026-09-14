using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Iterators;

internal static class BodyPhysics
{
    internal static bool CanTranslate(Room room, IReadOnlyList<BodyChunk> chunks, Vector2 offset)
    {
        // A bounded sweep prevents an Arm correction from teleporting across a wall.
        float distance = offset.magnitude;
        if (distance > 20f) return false;
        int steps = Mathf.Max(1, Mathf.CeilToInt(distance / 2f));
        for (int step = 1; step <= steps; step++)
        for (int i = 0; i < chunks.Count; i++)
            if (chunks[i].collideWithTerrain && !CanOccupy(room, chunks[i].pos + offset * ((float)step / steps), chunks[i].rad)) return false;
        return true;
    }

    internal static bool CanOccupy(Room room, Vector2 position, float radius)
    {
        int left = Mathf.FloorToInt((position.x - radius) / 20f);
        int right = Mathf.FloorToInt((position.x + radius) / 20f);
        int bottom = Mathf.FloorToInt((position.y - radius) / 20f);
        int top = Mathf.FloorToInt((position.y + radius) / 20f);
        if (left < 0 || bottom < 0 || right >= room.TileWidth || top >= room.TileHeight) return false;
        for (int x = left; x <= right; x++)
        for (int y = bottom; y <= top; y++)
        {
            Room.Tile tile = room.GetTile(x, y);
            // Slopes and one-way floors are conservatively protected during Arm
            // corrections; ordinary movement still uses the game's exact collision.
            if (!tile.Solid && tile.Terrain != Room.Tile.TerrainType.Slope && tile.Terrain != Room.Tile.TerrainType.Floor) continue;
            Vector2 nearest = new(Mathf.Clamp(position.x, x * 20f, (x + 1) * 20f), Mathf.Clamp(position.y, y * 20f, (y + 1) * 20f));
            if ((position - nearest).sqrMagnitude < radius * radius - 0.001f) return false;
        }
        if (room.terrain != null && room.terrain.TrySnapToTerrain(position, radius, out Vector2 snapped) &&
            (position - snapped).sqrMagnitude > 0.001f) return false;
        return true;
    }
}
