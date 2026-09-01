using System;
using UnityEngine;

namespace DryCycle.Weather.Fog;

/// <summary>
/// Immutable per-room texture used by the fog fluid solver and the composite shader.
/// R = solid tile mask. G = normalized approximate Euclidean distance to the nearest
/// solid tile. The solver accesses R through Texture.Load so the texture may use
/// bilinear filtering for smooth renderer-side G sampling without softening physics.
/// </summary>
internal sealed class DryCycleFogObstacleField : IDisposable
{
    private const float MaxEncodedDistanceTiles = 16f;
    private const float DiagonalCost = 1.41421356237f;
    private const float Infinity = 1000000f;

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

        float[] distances = BuildDistances(room, width, height);
        Color32[] pixels = new Color32[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                bool solid = room.GetTile(x, y).Solid;
                float normalizedDistance = Mathf.Clamp01(
                    distances[index] / MaxEncodedDistanceTiles);

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
            // Compute kernels use Texture.Load for the solid mask, so filtering only
            // affects renderer-side tex2D sampling. Bilinear G removes the old visible
            // tile-step bands in wall pooling and volumetric edge shading.
            filterMode = FilterMode.Bilinear,
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

    private static float[] BuildDistances(Room room, int width, int height)
    {
        int count = width * height;
        float[] distances = new float[count];
        bool hasSolid = false;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (room.GetTile(x, y).Solid)
                {
                    distances[index] = 0f;
                    hasSolid = true;
                }
                else
                {
                    distances[index] = Infinity;
                }
            }
        }

        // Rooms with no solid tiles are legal. Treat all cells as far from walls.
        if (!hasSolid)
        {
            for (int i = 0; i < count; i++)
            {
                distances[i] = MaxEncodedDistanceTiles;
            }
            return distances;
        }

        // Two chamfer sweeps with orthogonal cost 1 and diagonal cost sqrt(2). This is
        // a close Euclidean approximation at tile resolution and avoids the diamond
        // isocontours created by the previous 4-neighbour Manhattan BFS.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                float best = distances[index];
                best = Math.Min(best, Read(distances, width, height, x - 1, y) + 1f);
                best = Math.Min(best, Read(distances, width, height, x, y - 1) + 1f);
                best = Math.Min(best, Read(distances, width, height, x - 1, y - 1) + DiagonalCost);
                best = Math.Min(best, Read(distances, width, height, x + 1, y - 1) + DiagonalCost);
                distances[index] = best;
            }
        }

        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = width - 1; x >= 0; x--)
            {
                int index = y * width + x;
                float best = distances[index];
                best = Math.Min(best, Read(distances, width, height, x + 1, y) + 1f);
                best = Math.Min(best, Read(distances, width, height, x, y + 1) + 1f);
                best = Math.Min(best, Read(distances, width, height, x + 1, y + 1) + DiagonalCost);
                best = Math.Min(best, Read(distances, width, height, x - 1, y + 1) + DiagonalCost);
                distances[index] = Math.Min(best, MaxEncodedDistanceTiles);
            }
        }

        return distances;
    }

    private static float Read(
        float[] distances,
        int width,
        int height,
        int x,
        int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return Infinity;
        }

        return distances[y * width + x];
    }
}
