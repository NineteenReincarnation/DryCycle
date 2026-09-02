using System;
using DryCycle.DayNight;
using DryCycle.TemperatureSystem;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Immutable room-space terrain/sky field shared by HeatWave simulation and optics.
/// R = solid mask, G = normalized distance to solid, B = upward-facing hot-surface
/// source, A = local sky transmission. B/A are geometry/local-shade data rather than
/// baked clock intensity so day/night lighting can change without rebuilding the field.
///
/// Sky exposure is deliberately hemispherical rather than a single vertical test. Rain
/// World rooms contain bridges, ledges and machinery that frequently block a straight
/// ray to the top while still being obviously outdoors. Treating the first overhead
/// tile as a sealed roof starved the previous HeatWave implementation of essentially all
/// ground sources in rooms such as SU_A53.
/// </summary>
internal sealed class HeatWaveTerrainField : IDisposable
{
    private const float MaxEncodedDistanceTiles = 16f;
    private const float DiagonalCost = 1.41421356237f;
    private const float Infinity = 1000000f;

    // A small 2D approximation of an upper hemisphere. The center ray carries most of
    // the direct-sun meaning while oblique rays allow hot surfaces under thin platforms
    // to see open sky through the sides. Leaving the room horizontally counts as sky.
    private static readonly float[] SkyRaySlopes =
    {
        -1.35f, -0.82f, -0.46f, -0.22f, 0f, 0.22f, 0.46f, 0.82f, 1.35f
    };

    private static readonly float[] SkyRayWeights =
    {
        0.38f, 0.58f, 0.78f, 0.92f, 1.20f, 0.92f, 0.78f, 0.58f, 0.38f
    };

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
        float[] skyExposure = BuildSkyExposure(room, width, height);
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
                if (!solid)
                {
                    Vector2 samplePoint = new(
                        x * 20f + 10f,
                        y * 20f + 10f);
                    float localShade = SolarEnvironment.GetLocalShadeAt(room, samplePoint);
                    float localTransmission = 1f - Mathf.Clamp01(localShade);
                    float geometrySky = Mathf.Clamp01(skyExposure[index]);

                    skyTransmission = geometrySky * localTransmission;

                    if (y > 0 && room.GetTile(x, y - 1).Solid)
                    {
                        // HeatWave is a hot-air weather state, not a binary sunlight
                        // decal. Even a partly covered upward-facing surface retains a
                        // weak boundary layer from the already-heated room air; open sky
                        // then ramps it rapidly toward full strength. This 10% floor is
                        // intentionally too weak to fill enclosed rooms by itself, but it
                        // prevents a single bridge tile from deleting an entire plume.
                        float skyDriven = Mathf.Pow(geometrySky, 0.72f);
                        float ambientBoundary = Mathf.Lerp(0.10f, 1f, skyDriven);
                        boundaryExposure = ambientBoundary * localTransmission;
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

    private static float[] BuildSkyExposure(Room room, int width, int height)
    {
        float[] result = new float[width * height];
        float totalWeight = 0f;
        for (int i = 0; i < SkyRayWeights.Length; i++)
        {
            totalWeight += SkyRayWeights[i];
        }
        totalWeight = Mathf.Max(0.0001f, totalWeight);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (room.GetTile(x, y).Solid)
                {
                    result[index] = 0f;
                    continue;
                }

                float visibleWeight = 0f;
                for (int ray = 0; ray < SkyRaySlopes.Length; ray++)
                {
                    if (RayEscapesToSky(room, width, height, x, y, SkyRaySlopes[ray]))
                    {
                        visibleWeight += SkyRayWeights[ray];
                    }
                }

                result[index] = Mathf.Clamp01(visibleWeight / totalWeight);
            }
        }

        return result;
    }

    private static bool RayEscapesToSky(
        Room room,
        int width,
        int height,
        int startX,
        int startY,
        float slope)
    {
        for (int step = 1; step <= height - startY + width; step++)
        {
            int y = startY + step;
            int x = Mathf.RoundToInt(startX + slope * step);

            // Exiting the authored room through the top or either open side means the
            // ray reached the exterior atmosphere.
            if (y >= height || x < 0 || x >= width)
            {
                return true;
            }

            if (room.GetTile(x, y).Solid)
            {
                return false;
            }
        }

        return true;
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
