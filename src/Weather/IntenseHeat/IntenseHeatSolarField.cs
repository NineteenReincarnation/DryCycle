using System;
using DryCycle.TemperatureSystem;
using UnityEngine;

namespace DryCycle.Weather.IntenseHeat;

/// <summary>
/// Room-anchored direct-sun field for IntenseHeat.
///
/// R = direct solar exposure after terrain/local-shade occlusion
/// G = penumbra / sun-shadow boundary response
/// B = open-sky confidence
/// A = stable spatial phase
///
/// The field is deliberately geometry-driven instead of being a screen-space mask, so
/// the same sun/shade logic can drive rendering, creature exposure and gameplay heat.
/// </summary>
internal static class IntenseHeatSolarField
{
    private static readonly Vector2 TowardSun = new(-0.36f, 0.933f);

    internal static Texture2D Build(Room room)
    {
        if (room == null || room.TileWidth <= 0 || room.TileHeight <= 0)
        {
            return null;
        }

        try
        {
            int width = room.TileWidth;
            int height = room.TileHeight;
            float[] exposure = new float[width * height];
            float[] sky = new float[width * height];
            Color32[] pixels = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Vector2 worldPos = room.MiddleOfTile(x, y);
                    float geometryExposure = EvaluateGeometryExposure(room, worldPos);
                    float localShade = SolarEnvironment.GetLocalShadeAt(room, worldPos);
                    float localTransmission = 1f - Mathf.Clamp01(localShade);
                    float direct = geometryExposure * localTransmission;
                    exposure[y * width + x] = direct;
                    sky[y * width + x] = EvaluateOpenSky(room, worldPos);
                }
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    float center = exposure[index];
                    float neighborhood = AverageExposure(exposure, x, y, width, height, 2);
                    float penumbra = Mathf.Clamp01(Mathf.Abs(center - neighborhood) * 2.8f +
                                                   neighborhood * (1f - center) * 0.75f);
                    float phase = Hash01(x, y, width, height);

                    pixels[index] = new Color32(
                        ToByte(center),
                        ToByte(penumbra),
                        ToByte(sky[index]),
                        ToByte(phase));
                }
            }

            Texture2D texture = new(width, height, TextureFormat.RGBA32, false)
            {
                name = "DryCycleIntenseHeatSolarField",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

            Plugin.Logger?.LogInfo(
                $"DryCycle IntenseHeat solar exposure field generated: {width}x{height}.");
            return texture;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                "DryCycle IntenseHeat could not generate the room solar field. " +
                "The hazard will continue with room-wide sunlight fallback.");
            Plugin.Logger?.LogWarning(ex);
            return null;
        }
    }

    internal static float SampleExposure(Room room, Vector2 worldPos)
    {
        if (room == null)
        {
            return 0f;
        }

        float roomSun = Mathf.Clamp01(SolarEnvironment.GetSunlightIntensity(room));
        float roomTransmission = 1f - Mathf.Clamp01(SolarEnvironment.GetRoomShade(room));
        float localTransmission = 1f - Mathf.Clamp01(SolarEnvironment.GetLocalShadeAt(room, worldPos));
        float geometry = EvaluateGeometryExposure(room, worldPos);

        // IntenseHeat represents exceptional direct solar load. Authored Sunlight still
        // matters, but a normally outdoor room is never allowed to look like weak sun.
        float hazardSun = Mathf.Lerp(0.82f, 1f, roomSun);
        return Mathf.Clamp01(geometry * localTransmission * roomTransmission * hazardSun);
    }

    internal static void Dispose(Texture2D texture)
    {
        if (texture != null)
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    private static float EvaluateGeometryExposure(Room room, Vector2 worldPos)
    {
        if (room == null)
        {
            return 0f;
        }

        Vector2 direction = TowardSun.normalized;
        float maxDistance = Mathf.Max(room.PixelWidth, room.PixelHeight) * 1.45f;
        float step = 12f;
        int steps = Mathf.CeilToInt(maxDistance / step);

        for (int i = 1; i <= steps; i++)
        {
            Vector2 sample = worldPos + direction * (i * step);
            IntVector2 tilePos = room.GetTilePosition(sample);

            // Leaving through the top/side toward the sun counts as open sky.
            if (tilePos.x < 0 || tilePos.x >= room.TileWidth ||
                tilePos.y < 0 || tilePos.y >= room.TileHeight)
            {
                return 1f;
            }

            Room.Tile tile = room.GetTile(tilePos);
            if (IsSolarBlocker(tile))
            {
                return 0f;
            }
        }

        return 1f;
    }

    private static float EvaluateOpenSky(Room room, Vector2 worldPos)
    {
        if (room == null)
        {
            return 0f;
        }

        IntVector2 origin = room.GetTilePosition(worldPos);
        int clear = 0;
        int total = 5;

        for (int offset = -2; offset <= 2; offset++)
        {
            bool blocked = false;
            int x = Mathf.Clamp(origin.x + offset, 0, room.TileWidth - 1);
            for (int y = origin.y + 1; y < room.TileHeight; y++)
            {
                if (IsSolarBlocker(room.GetTile(x, y)))
                {
                    blocked = true;
                    break;
                }
            }

            if (!blocked)
            {
                clear++;
            }
        }

        return clear / (float)total;
    }

    private static bool IsSolarBlocker(Room.Tile tile)
    {
        if (tile == null)
        {
            return false;
        }

        return tile.Terrain == Room.Tile.TerrainType.Solid ||
               tile.Terrain == Room.Tile.TerrainType.Slope;
    }

    private static float AverageExposure(
        float[] values,
        int x,
        int y,
        int width,
        int height,
        int radius)
    {
        float total = 0f;
        int count = 0;
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

                total += values[sy * width + sx];
                count++;
            }
        }

        return count > 0 ? total / count : 0f;
    }

    private static float Hash01(int x, int y, int width, int height)
    {
        unchecked
        {
            uint h = 0xA511E9B3u;
            h ^= (uint)x * 0x9E3779B9u;
            h = (h ^ (h >> 16)) * 0x85EBCA6Bu;
            h ^= (uint)y * 0xC2B2AE35u;
            h ^= (uint)width * 0x27D4EB2Fu;
            h ^= (uint)height * 0x165667B1u;
            h ^= h >> 15;
            return (h & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static byte ToByte(float value)
    {
        return (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);
    }
}
