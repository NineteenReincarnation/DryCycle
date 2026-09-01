using System;
using DryCycle.DayNight;
using DryCycle.TemperatureSystem;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Immutable room-space terrain/sky field shared by HeatWave simulation and optics.
/// R = solid mask, G = normalized distance to solid, B = upward-facing exposed surface
/// source, A = local sky transmission. B/A are geometric/local-shade data rather than
/// baked sunlight intensity so day/night lighting can change without rebuilding the
/// texture and HeatWave still works in rooms that omitted optional TemperatureSets data.
/// </summary>
internal sealed class HeatWaveTerrainField : IDisposable
{
    private const float MaxEncodedDistanceTiles = 16f;
    private const float DiagonalCost = 1.41421356237f;
    private const float Infinity = 1000000f;

    internal Texture2D Texture { get; }
    internal Vector2 RoomSizePixels { get; }
    internal float AuthoredSunlightIntensity { get; }
    internal float RoomShade { get; }

    internal HeatWaveTerrainField(Room room)
    {
        if (room == null)
        {
            throw new ArgumentNullException(nameof(room));
        }

        int width = Math.Max(1, room.TileWidth);
        int height = Math.Max(1, room.TileHeight);
        RoomSizePixels = new Vector2(width * 20f, height * 20f);

        AuthoredSunlightIntensity = Mathf.Clamp01(SolarEnvironment.GetSunlightIntensity(room));
        RoomShade = Mathf.Clamp01(SolarEnvironment.GetRoomShade(room));

        float[] distances = BuildDistances(room, width, height);
        bool[] skyOpen = BuildSkyVisibility(room, width, height);
        Color32[] pixels = new Color32[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                bool solid = room.GetTile(x, y).Solid;
                float normalizedDistance = Mathf.Clamp01(
                    distances[index] / MaxEncodedDistanceTiles);

                float skyTransmission = 0f;
                float boundaryExposure = 0f;
                if (!solid && skyOpen[index])
                {
                    Vector2 samplePoint = new(
                        x * 20f + 10f,
                        y * 20f + 10f);
                    float localShade = SolarEnvironment.GetLocalShadeAt(room, samplePoint);
                    skyTransmission = 1f - Mathf.Clamp01(localShade);

                    // Only the first air cell over an upward-facing solid surface is a
                    // direct ground boundary source. The GPU solver spreads/advects the
                    // resulting heat instead of filling a hand-authored vertical mask.
                    if (y > 0 && room.GetTile(x, y - 1).Solid)
                    {
                        boundaryExposure = skyTransmission;
                    }
                }

                pixels[index] = new Color32(
                    solid ? (byte)255 : (byte)0,
                    (byte)Mathf.RoundToInt(normalizedDistance * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(boundaryExposure) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(skyTransmission) * 255f));
            }
        }

        Texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = $"DryCycleHeatWaveTerrain_{room.abstractRoom?.name ?? "Room"}",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            anisoLevel = 0
        };
        Texture.SetPixels32(pixels);
        Texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
    }

    /// <summary>
    /// Visual/direct-solar drive for the current clock. Authored SunlightIntensity is
    /// respected as an artistic boost, but it is not mandatory: an open HeatWave room
    /// still gets a strong midday sun when the optional TemperatureSets field was left
    /// at its neutral zero default. RoomShade remains authoritative and can suppress it.
    /// </summary>
    internal float EvaluateSolar(WorldClock clock)
    {
        float directLight = clock?.Lighting.DirectLight ?? 1f;
        float roomTransmission = 1f - RoomShade;
        float heatWaveOutdoorBaseline = Mathf.Lerp(
            0.62f,
            1f,
            AuthoredSunlightIntensity);

        return Mathf.Clamp01(
            directLight *
            roomTransmission *
            heatWaveOutdoorBaseline);
    }

    public void Dispose()
    {
        if (Texture != null)
        {
            UnityEngine.Object.Destroy(Texture);
        }
    }

    private static bool[] BuildSkyVisibility(Room room, int width, int height)
    {
        bool[] result = new bool[width * height];
        for (int x = 0; x < width; x++)
        {
            bool blocked = false;
            for (int y = height - 1; y >= 0; y--)
            {
                int index = y * width + x;
                bool solid = room.GetTile(x, y).Solid;
                result[index] = !blocked && !solid;
                if (solid)
                {
                    blocked = true;
                }
            }
        }
        return result;
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

        if (!hasSolid)
        {
            for (int i = 0; i < count; i++)
            {
                distances[i] = MaxEncodedDistanceTiles;
            }
            return distances;
        }

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
