using System;
using UnityEngine;

namespace DryCycle.Weather.Foehn;

/// <summary>
/// Shared Foehn optical textures. These textures describe air motion, not a fluid
/// simulation: RG is wind-local flow, B is broad gust density, A is turbulence.
/// The streak texture provides macro/fine wind sheets and phase variation.
/// </summary>
internal static class FoehnWindField
{
    private const int FlowWidth = 256;
    private const int FlowHeight = 128;
    private const int StreakWidth = 256;
    private const int StreakHeight = 128;

    internal static Texture2D FlowTexture { get; private set; }
    internal static Texture2D StreakTexture { get; private set; }
    internal static bool IsAvailable => FlowTexture != null && StreakTexture != null;

    internal static void Ensure()
    {
        if (IsAvailable)
        {
            return;
        }

        try
        {
            FlowTexture ??= BuildFlowTexture();
            StreakTexture ??= BuildStreakTexture();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                "DryCycle Foehn could not generate its procedural optical textures. " +
                "The shader will use its procedural fallback.");
            Plugin.Logger?.LogWarning(ex);
        }
    }

    private static Texture2D BuildFlowTexture()
    {
        Color32[] pixels = new Color32[FlowWidth * FlowHeight];

        for (int y = 0; y < FlowHeight; y++)
        {
            float fy = y / (float)FlowHeight;
            for (int x = 0; x < FlowWidth; x++)
            {
                float fx = x / (float)FlowWidth;

                float macro = FractalNoise(fx * 2.15f + 17.31f, fy * 3.45f + 41.73f, 3);
                float detail = FractalNoise(fx * 5.8f + 73.8f, fy * 7.2f + 11.4f, 2);
                float curl = FractalNoise(fx * 4.1f + 129.5f, fy * 4.9f + 211.6f, 3) * 2f - 1f;

                // Tangent-space flow. X is forward speed bias and Y is cross-wind
                // meander. The shader rotates this basis into the actual room wind.
                float forward = Mathf.Lerp(0.68f, 1f, macro);
                float lateral = Mathf.Clamp(curl * 0.34f + (detail - 0.5f) * 0.18f, -0.48f, 0.48f);
                Vector2 flow = new(forward, lateral);
                flow.Normalize();

                float gust = Smooth01(Mathf.Clamp01(macro * 0.72f + detail * 0.42f - 0.12f));
                float turbulence = Mathf.Clamp01(
                    Mathf.Abs(curl) * 0.62f + Mathf.Abs(detail - 0.5f) * 0.76f);

                pixels[y * FlowWidth + x] = new Color32(
                    ToByte(flow.x * 0.5f + 0.5f),
                    ToByte(flow.y * 0.5f + 0.5f),
                    ToByte(gust),
                    ToByte(turbulence));
            }
        }

        Texture2D texture = new(FlowWidth, FlowHeight, TextureFormat.RGBA32, false)
        {
            name = "DryCycleFoehnFlowField",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Repeat,
            anisoLevel = 0
        };
        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }

    private static Texture2D BuildStreakTexture()
    {
        Color32[] pixels = new Color32[StreakWidth * StreakHeight];

        for (int y = 0; y < StreakHeight; y++)
        {
            float fy = y / (float)StreakHeight;
            for (int x = 0; x < StreakWidth; x++)
            {
                float fx = x / (float)StreakWidth;
                float warp = FractalNoise(fx * 2.7f + 29.4f, fy * 3.1f + 83.1f, 3) - 0.5f;
                float phase = FractalNoise(fx * 1.4f + 131.2f, fy * 2.2f + 17.8f, 2);

                // Long, coherent wind sheets. The X contribution is deliberately much
                // smaller than Y so these read as streaks carried along the wind rather
                // than isotropic smoke/noise blobs.
                float macroWave = Mathf.Sin(
                    (fy * 13.0f + warp * 1.9f + fx * 0.42f) * Mathf.PI * 2f);
                float fineWave = Mathf.Sin(
                    (fy * 31.0f - warp * 3.2f + fx * 0.88f + phase) * Mathf.PI * 2f);

                float macro = Mathf.Pow(Mathf.Clamp01(macroWave * 0.5f + 0.5f), 2.25f);
                float fine = Mathf.Pow(Mathf.Clamp01(fineWave * 0.5f + 0.5f), 3.1f);
                float dust = Smooth01(Mathf.Clamp01(
                    FractalNoise(fx * 5.0f + 211.3f, fy * 6.2f + 157.7f, 3) * 0.82f +
                    macro * 0.34f - 0.10f));

                pixels[y * StreakWidth + x] = new Color32(
                    ToByte(macro),
                    ToByte(fine),
                    ToByte(phase),
                    ToByte(dust));
            }
        }

        Texture2D texture = new(StreakWidth, StreakHeight, TextureFormat.RGBA32, false)
        {
            name = "DryCycleFoehnStreakField",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Repeat,
            anisoLevel = 0
        };
        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }

    private static float FractalNoise(float x, float y, int octaves)
    {
        float value = 0f;
        float weight = 0f;
        float amplitude = 1f;
        float frequency = 1f;

        for (int octave = 0; octave < octaves; octave++)
        {
            value += Mathf.PerlinNoise(x * frequency, y * frequency) * amplitude;
            weight += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.03f;
        }

        return weight > 0f ? value / weight : 0.5f;
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

/// <summary>
/// Room geometry translated into a low-cost Foehn guide field.
/// R = wind exposure, G = lee-side wake, B = nozzle acceleration,
/// A = obstacle-edge turbulence.
/// </summary>
internal sealed class FoehnTerrainField : IDisposable
{
    private const int ShadowSearchTiles = 12;
    private const int NozzleSearchTiles = 7;
    private const int EdgeSearchRadius = 3;

    private readonly int _width;
    private readonly int _height;
    private readonly Color32[] _samples;

    internal Texture2D Texture { get; }

    private FoehnTerrainField(int width, int height, Color32[] samples, Texture2D texture)
    {
        _width = width;
        _height = height;
        _samples = samples;
        Texture = texture;
    }

    internal static FoehnTerrainField Build(Room room, Vector2 windDirection)
    {
        if (room == null || room.TileWidth <= 0 || room.TileHeight <= 0)
        {
            return null;
        }

        try
        {
            int width = Mathf.Max(1, room.TileWidth);
            int height = Mathf.Max(1, room.TileHeight);
            Color32[] samples = new Color32[width * height];
            int windSign = windDirection.x < 0f ? -1 : 1;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Room.Tile tile = room.GetTile(x, y);
                    if (IsTerrain(tile))
                    {
                        samples[y * width + x] = new Color32(0, 0, 0, 255);
                        continue;
                    }

                    EvaluateShadow(room, x, y, windSign, width, out float exposure, out float wake);
                    float nozzle = EvaluateNozzle(room, x, y, width, height);
                    float edge = EvaluateEdgeTurbulence(room, x, y, width, height);

                    // Water suppresses lifted dust but not the optical wind itself. Fold
                    // that information into the edge channel so particles can cheaply
                    // detect wet cells without adding another texture.
                    if (tile != null && tile.AnyWater)
                    {
                        edge = Mathf.Max(edge, 0.92f);
                        nozzle *= 0.35f;
                    }

                    samples[y * width + x] = new Color32(
                        ToByte(exposure),
                        ToByte(wake),
                        ToByte(nozzle),
                        ToByte(edge));
                }
            }

            Texture2D texture = new(width, height, TextureFormat.RGBA32, false)
            {
                name = "DryCycleFoehnTerrainField",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0
            };
            texture.SetPixels32(samples);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

            Plugin.Logger?.LogInfo(
                $"DryCycle Foehn terrain field generated: {width}x{height}, windSign={windSign}.");
            return new FoehnTerrainField(width, height, samples, texture);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                "DryCycle Foehn could not generate its room wind field. " +
                "The weather will continue with open-air wind only.");
            Plugin.Logger?.LogWarning(ex);
            return null;
        }
    }

    internal FoehnTerrainSample Sample(Vector2 worldPosition)
    {
        if (_samples == null || _samples.Length == 0)
        {
            return FoehnTerrainSample.OpenAir;
        }

        int x = Mathf.Clamp(Mathf.FloorToInt(worldPosition.x / 20f), 0, _width - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(worldPosition.y / 20f), 0, _height - 1);
        Color32 sample = _samples[y * _width + x];
        return new FoehnTerrainSample(
            sample.r / 255f,
            sample.g / 255f,
            sample.b / 255f,
            sample.a / 255f);
    }

    public void Dispose()
    {
        if (Texture != null)
        {
            UnityEngine.Object.Destroy(Texture);
        }
    }

    private static void EvaluateShadow(
        Room room,
        int x,
        int y,
        int windSign,
        int width,
        out float exposure,
        out float wake)
    {
        exposure = 1f;
        wake = 0f;

        for (int distance = 1; distance <= ShadowSearchTiles; distance++)
        {
            int sx = x - windSign * distance;
            if (sx < 0 || sx >= width)
            {
                break;
            }

            bool blocked = IsTerrain(room.GetTile(sx, y));
            if (!blocked && y > 0)
            {
                blocked = IsTerrain(room.GetTile(sx, y - 1));
            }
            if (!blocked && y + 1 < room.TileHeight)
            {
                blocked = IsTerrain(room.GetTile(sx, y + 1));
            }

            if (!blocked)
            {
                continue;
            }

            float proximity = 1f - (distance - 1f) / ShadowSearchTiles;
            wake = Smooth01(proximity);
            exposure = Mathf.Clamp01(1f - wake * 0.82f);
            return;
        }
    }

    private static float EvaluateNozzle(Room room, int x, int y, int width, int height)
    {
        int up = FindTerrainDistance(room, x, y, 0, 1, width, height, NozzleSearchTiles);
        int down = FindTerrainDistance(room, x, y, 0, -1, width, height, NozzleSearchTiles);

        if (up <= 0 || down <= 0)
        {
            return 0f;
        }

        int clearance = up + down;
        float narrow = 1f - Mathf.InverseLerp(4f, NozzleSearchTiles * 2f, clearance);
        return Smooth01(narrow);
    }

    private static float EvaluateEdgeTurbulence(
        Room room,
        int x,
        int y,
        int width,
        int height)
    {
        int best = int.MaxValue;
        for (int oy = -EdgeSearchRadius; oy <= EdgeSearchRadius; oy++)
        {
            int sy = y + oy;
            if (sy < 0 || sy >= height)
            {
                continue;
            }

            for (int ox = -EdgeSearchRadius; ox <= EdgeSearchRadius; ox++)
            {
                int sx = x + ox;
                if (sx < 0 || sx >= width)
                {
                    continue;
                }

                int d2 = ox * ox + oy * oy;
                if (d2 >= best || d2 > EdgeSearchRadius * EdgeSearchRadius)
                {
                    continue;
                }

                if (IsTerrain(room.GetTile(sx, sy)))
                {
                    best = d2;
                }
            }
        }

        if (best == int.MaxValue)
        {
            return 0f;
        }

        float distance = Mathf.Sqrt(best);
        return Smooth01(1f - distance / Mathf.Max(1f, EdgeSearchRadius));
    }

    private static int FindTerrainDistance(
        Room room,
        int x,
        int y,
        int dx,
        int dy,
        int width,
        int height,
        int maximum)
    {
        for (int distance = 1; distance <= maximum; distance++)
        {
            int sx = x + dx * distance;
            int sy = y + dy * distance;
            if (sx < 0 || sx >= width || sy < 0 || sy >= height)
            {
                return -1;
            }

            if (IsTerrain(room.GetTile(sx, sy)))
            {
                return distance;
            }
        }

        return -1;
    }

    private static bool IsTerrain(Room.Tile tile)
    {
        if (tile == null)
        {
            return false;
        }

        return tile.Terrain == Room.Tile.TerrainType.Solid ||
               tile.Terrain == Room.Tile.TerrainType.Slope ||
               tile.Terrain == Room.Tile.TerrainType.Floor;
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

internal readonly struct FoehnTerrainSample
{
    internal static readonly FoehnTerrainSample OpenAir = new(1f, 0f, 0f, 0f);

    internal readonly float Exposure;
    internal readonly float Wake;
    internal readonly float Nozzle;
    internal readonly float Edge;

    internal FoehnTerrainSample(float exposure, float wake, float nozzle, float edge)
    {
        Exposure = Mathf.Clamp01(exposure);
        Wake = Mathf.Clamp01(wake);
        Nozzle = Mathf.Clamp01(nozzle);
        Edge = Mathf.Clamp01(edge);
    }
}
