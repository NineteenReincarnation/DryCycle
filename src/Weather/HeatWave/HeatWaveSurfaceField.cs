using System;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Builds a compact, room-anchored geometry mask for HeatWave optics.
///
/// This is not a thermal simulation. It is a static optical guide derived from room
/// tiles so the fullscreen atmosphere can tell the difference between open air and
/// air immediately above hot terrain. Channels are:
/// R = floor/ground proximity, G = any terrain proximity,
/// B = dry-air mask (suppresses ground mirage under water), A = spatial phase.
/// </summary>
internal static class HeatWaveSurfaceField
{
    private const int GroundSearchTiles = 9;
    private const int SurfaceSearchRadius = 4;

    internal static Texture2D Build(Room room)
    {
        if (room == null || room.TileWidth <= 0 || room.TileHeight <= 0)
        {
            return null;
        }

        try
        {
            int width = Mathf.Max(1, room.TileWidth);
            int height = Mathf.Max(1, room.TileHeight);
            Color32[] pixels = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Room.Tile tile = room.GetTile(x, y);
                    float ground = EvaluateGroundProximity(room, x, y, height);
                    float surface = EvaluateSurfaceProximity(room, x, y, width, height);
                    float dryAir = tile.AnyWater ? 0f : 1f;
                    float phase = Hash01(x, y, width, height);

                    pixels[y * width + x] = new Color32(
                        ToByte(ground),
                        ToByte(surface),
                        ToByte(dryAir),
                        ToByte(phase));
                }
            }

            Texture2D texture = new(width, height, TextureFormat.RGBA32, false)
            {
                name = "DryCycleHeatWaveSurfaceField",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

            Plugin.Logger?.LogInfo(
                $"DryCycle HeatWave surface field generated: {width}x{height}.");
            return texture;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                "DryCycle HeatWave could not generate its room surface field. " +
                "HeatWave will continue without terrain-proximity mirage shaping.");
            Plugin.Logger?.LogWarning(ex);
            return null;
        }
    }

    internal static void Dispose(Texture2D texture)
    {
        if (texture != null)
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    private static float EvaluateGroundProximity(Room room, int x, int y, int height)
    {
        int maxDistance = Mathf.Min(GroundSearchTiles, height - 1);
        for (int distance = 0; distance <= maxDistance; distance++)
        {
            int sampleY = y - distance;
            if (sampleY < 0)
            {
                break;
            }

            if (!IsHeatSurface(room.GetTile(x, sampleY)))
            {
                continue;
            }

            float normalized = 1f - distance / (float)Mathf.Max(1, GroundSearchTiles);
            return Smooth01(normalized);
        }

        return 0f;
    }

    private static float EvaluateSurfaceProximity(
        Room room,
        int x,
        int y,
        int width,
        int height)
    {
        int bestDistanceSq = int.MaxValue;
        int radius = SurfaceSearchRadius;

        for (int oy = -radius; oy <= radius; oy++)
        {
            int sy = y + oy;
            if (sy < 0 || sy >= height)
            {
                continue;
            }

            for (int ox = -radius; ox <= radius; ox++)
            {
                int sx = x + ox;
                if (sx < 0 || sx >= width)
                {
                    continue;
                }

                int distanceSq = ox * ox + oy * oy;
                if (distanceSq >= bestDistanceSq || distanceSq > radius * radius)
                {
                    continue;
                }

                if (IsHeatSurface(room.GetTile(sx, sy)))
                {
                    bestDistanceSq = distanceSq;
                }
            }
        }

        if (bestDistanceSq == int.MaxValue)
        {
            return 0f;
        }

        float distance = Mathf.Sqrt(bestDistanceSq);
        float normalized = 1f - distance / Mathf.Max(1f, SurfaceSearchRadius);
        return Smooth01(normalized);
    }

    private static bool IsHeatSurface(Room.Tile tile)
    {
        if (tile == null)
        {
            return false;
        }

        return tile.Terrain == Room.Tile.TerrainType.Solid ||
               tile.Terrain == Room.Tile.TerrainType.Slope ||
               tile.Terrain == Room.Tile.TerrainType.Floor;
    }

    private static float Hash01(int x, int y, int width, int height)
    {
        unchecked
        {
            uint h = 0x9E3779B9u;
            h ^= (uint)x * 0x85EBCA6Bu;
            h = (h ^ (h >> 16)) * 0xC2B2AE35u;
            h ^= (uint)y * 0x27D4EB2Fu;
            h ^= (uint)width * 0x165667B1u;
            h ^= (uint)height * 0xD3A2646Cu;
            h ^= h >> 15;
            return (h & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static float Smooth01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }

    private static byte ToByte(float value)
    {
        return (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);
    }
}
