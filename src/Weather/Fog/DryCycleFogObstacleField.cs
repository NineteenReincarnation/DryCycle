using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Weather.Fog;

/// <summary>
/// Immutable per-room texture used by the fog fluid solver and the composite shader.
/// R = solid tile mask. G = normalized Manhattan distance to the nearest solid tile.
/// The distance channel lets the renderer accumulate fog near walls without requiring
/// an additional signed-distance compute pass.
/// </summary>
internal sealed class DryCycleFogObstacleField : IDisposable
{
    private const int MaxEncodedDistanceTiles = 16;

    internal Texture2D Texture { get; }
    internal Vector2 RoomSizePixels { get; }

    internal DryCycleFogObstacleField(Room room)
    {
        if (room == null)
        {
            throw new ArgumentNullException(nameof(room));
        }

        int width = Math.Max(1, room.TileWidth);
        int height = Math.Max(1, room.TileHeight);
        RoomSizePixels = new Vector2(width * 20f, height * 20f);

        int[] distances = BuildDistances(room, width, height);
        Color32[] pixels = new Color32[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                bool solid = room.GetTile(x, y).Solid;
                float normalizedDistance = Mathf.Clamp01(
                    distances[index] / (float)MaxEncodedDistanceTiles);

                pixels[index] = new Color32(
                    solid ? (byte)255 : (byte)0,
                    (byte)Mathf.RoundToInt(normalizedDistance * 255f),
                    0,
                    255);
            }
        }

        Texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = $"DryCycleFogObstacle_{room.abstractRoom?.name ?? "Room"}",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            anisoLevel = 0
        };
        Texture.SetPixels32(pixels);
        Texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
    }

    public void Dispose()
    {
        if (Texture != null)
        {
            UnityEngine.Object.Destroy(Texture);
        }
    }

    private static int[] BuildDistances(Room room, int width, int height)
    {
        int count = width * height;
        int[] distances = new int[count];
        for (int i = 0; i < count; i++)
        {
            distances[i] = int.MaxValue;
        }

        Queue<int> queue = new();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!room.GetTile(x, y).Solid)
                {
                    continue;
                }

                int index = y * width + x;
                distances[index] = 0;
                queue.Enqueue(index);
            }
        }

        // Rooms with no solid tiles are legal. Treat all cells as far from walls.
        if (queue.Count == 0)
        {
            for (int i = 0; i < count; i++)
            {
                distances[i] = MaxEncodedDistanceTiles;
            }
            return distances;
        }

        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            int current = distances[index];
            if (current >= MaxEncodedDistanceTiles)
            {
                continue;
            }

            int x = index % width;
            int y = index / width;
            Visit(x - 1, y, current + 1);
            Visit(x + 1, y, current + 1);
            Visit(x, y - 1, current + 1);
            Visit(x, y + 1, current + 1);
        }

        for (int i = 0; i < count; i++)
        {
            if (distances[i] == int.MaxValue)
            {
                distances[i] = MaxEncodedDistanceTiles;
            }
        }
        return distances;

        void Visit(int x, int y, int value)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }

            int index = y * width + x;
            if (value >= distances[index])
            {
                return;
            }

            distances[index] = value;
            queue.Enqueue(index);
        }
    }
}
