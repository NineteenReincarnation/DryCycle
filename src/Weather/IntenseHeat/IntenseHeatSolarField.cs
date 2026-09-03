using System;
using DryCycle.DayNight;
using DryCycle.TemperatureSystem;
using UnityEngine;

namespace DryCycle.Weather.IntenseHeat;

/// <summary>
/// Room-anchored solar field for IntenseHeat.
///
/// Ordinary room terrain deliberately does NOT block this hazard. IntenseHeat is a
/// region-scale extreme-sun event, so normal buildings do not carve the presentation
/// into hard illuminated/shadowed pieces. Only authored environmental shade remains:
/// RoomShade controls the room-wide transmission and SolarShadeZone controls deliberate
/// local relief areas.
///
/// R = local solar transmission after SolarShadeZone attenuation
/// G = authored shade-boundary response
/// B = broad open solar load (room-wide; actual daylight is supplied at runtime)
/// A = stable spatial phase
/// </summary>
internal static class IntenseHeatSolarField
{
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
            float[] transmission = new float[width * height];
            Color32[] pixels = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Vector2 worldPos = room.MiddleOfTile(x, y);
                    float localShade = SolarEnvironment.GetLocalShadeAt(room, worldPos);
                    transmission[y * width + x] = 1f - Mathf.Clamp01(localShade);
                }
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    float center = transmission[index];
                    float neighborhood = AverageTransmission(
                        transmission,
                        x,
                        y,
                        width,
                        height,
                        2);
                    float shadeBoundary = Mathf.Clamp01(
                        Mathf.Abs(center - neighborhood) * 3.2f +
                        neighborhood * (1f - center) * 0.55f);
                    float phase = Hash01(x, y, width, height);

                    pixels[index] = new Color32(
                        ToByte(center),
                        ToByte(shadeBoundary),
                        255,
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
                $"DryCycle IntenseHeat solar field generated without terrain occlusion: " +
                $"{width}x{height}.");
            return texture;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                "DryCycle IntenseHeat could not generate its local shade field. " +
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

        if (room.world != null &&
            WorldClockHooks.TryGetClock(room.world, out WorldClock clock) &&
            clock.IsNight)
        {
            return 0f;
        }

        float roomSun = Mathf.Clamp01(SolarEnvironment.GetSunlightIntensity(room));
        float roomTransmission = 1f - Mathf.Clamp01(SolarEnvironment.GetRoomShade(room));
        float localTransmission = 1f - Mathf.Clamp01(
            SolarEnvironment.GetLocalShadeAt(room, worldPos));

        // IntenseHeat is an extreme direct-sun hazard. Ordinary terrain never blocks
        // it; only explicit environmental shade can provide relief.
        float hazardSun = Mathf.Lerp(0.82f, 1f, roomSun);
        return Mathf.Clamp01(localTransmission * roomTransmission * hazardSun);
    }

    internal static void Dispose(Texture2D texture)
    {
        if (texture != null)
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    private static float AverageTransmission(
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

        return count > 0 ? total / count : 1f;
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
